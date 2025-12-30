using System.Runtime.CompilerServices;
using Grpc.Core;
using Grpc.Net.Client;
using GrpcChannel.Protocol;
using GrpcChannel.Protocol.Contracts;
using GrpcChannel.Protocol.Protos;
using Microsoft.Extensions.Logging;

namespace GrpcChannel.Client;

/// <summary>
/// Client for subscribing to high-throughput data streams.
/// Separate from the duplex channel to avoid flooding the control channel.
/// </summary>
/// <param name="options">Connection options.</param>
/// <param name="serializer">Optional payload serializer for non-protobuf types. Defaults to JSON.</param>
/// <param name="logger">Optional logger.</param>
public sealed class DataStreamClient(
    DuplexClientOptions options,
    IPayloadSerializer? serializer = null,
    ILogger<DataStreamClient>? logger = null) : IAsyncDisposable
{
    private readonly IPayloadSerializer _serializer = serializer ?? JsonPayloadSerializer.Default;
    private GrpcChannel? _grpcChannel;
    private DuplexService.DuplexServiceClient? _client;

    /// <summary>
    /// Ensures the gRPC channel is initialized.
    /// </summary>
    private DuplexService.DuplexServiceClient EnsureClient()
    {
        if (_client is not null)
        {
            return _client;
        }

        var httpHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        _grpcChannel = GrpcChannel.ForAddress(options.ServerAddress, new GrpcChannelOptions
        {
            HttpHandler = httpHandler
        });

        _client = new DuplexService.DuplexServiceClient(_grpcChannel);
        return _client;
    }

    /// <summary>
    /// Subscribes to a data stream and returns an async enumerable of messages.
    /// </summary>
    /// <typeparam name="T">Expected payload type.</typeparam>
    /// <param name="topic">Topic to subscribe to.</param>
    /// <param name="filter">Optional filter expression.</param>
    /// <param name="cursor">Optional starting position.</param>
    /// <param name="maxRate">Maximum messages per second (0 = unlimited).</param>
    /// <param name="bufferSize">Buffer size hint for the server.</param>
    /// <param name="options">Additional subscription options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Async enumerable of data stream messages.</returns>
    public async IAsyncEnumerable<DataStreamItem<T>> SubscribeAsync<T>(
        string topic,
        string? filter = null,
        string? cursor = null,
        int maxRate = 0,
        int bufferSize = 0,
        IReadOnlyDictionary<string, string>? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = EnsureClient();
        var streamId = Guid.NewGuid().ToString("N");

        var request = new DataStreamRequest
        {
            StreamId = streamId,
            Topic = topic,
            Filter = filter ?? string.Empty,
            Cursor = cursor ?? string.Empty,
            MaxRate = maxRate,
            BufferSize = bufferSize
        };

        if (options is not null)
        {
            foreach (var kvp in options)
            {
                request.Options.Add(kvp.Key, kvp.Value);
            }
        }

        var metadata = new Metadata
        {
            { "x-client-id", this.options.ClientId }
        };

        logger?.LogInformation(
            "Subscribing to stream {Topic} with filter '{Filter}'",
            topic, filter ?? "(none)");

        using var call = client.Subscribe(request, metadata, cancellationToken: cancellationToken);

        await foreach (var message in call.ResponseStream.ReadAllAsync(cancellationToken))
        {
            // Check for error
            if (message.Error is not null && !string.IsNullOrEmpty(message.Error.Code))
            {
                var problem = MapProblemDetails(message.Error);
                logger?.LogError(
                    "Stream error: {Code} - {Detail}",
                    problem.Code, problem.Detail);

                throw new DuplexException(problem);
            }

            // Check for completion
            if (message.IsComplete)
            {
                logger?.LogInformation(
                    "Stream {StreamId} completed after {Count} messages",
                    streamId, message.Sequence);
                yield break;
            }

            // Unpack payload
            T? payload = default;
            if (message.Payload is not null && !message.Payload.TypeUrl.Equals(string.Empty))
            {
                payload = UnpackPayload<T>(message.Payload);
            }

            yield return new DataStreamItem<T>(
                StreamId: message.StreamId,
                Sequence: message.Sequence,
                Payload: payload,
                Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(message.TimestampUtc),
                PartitionKey: message.PartitionKey,
                MessageType: message.MessageType);
        }
    }

    /// <summary>
    /// Subscribes to a data stream with a callback for each message.
    /// </summary>
    /// <typeparam name="T">Expected payload type.</typeparam>
    /// <param name="topic">Topic to subscribe to.</param>
    /// <param name="onMessage">Callback for each message.</param>
    /// <param name="filter">Optional filter expression.</param>
    /// <param name="cursor">Optional starting position.</param>
    /// <param name="maxRate">Maximum messages per second (0 = unlimited).</param>
    /// <param name="bufferSize">Buffer size hint for the server.</param>
    /// <param name="options">Additional subscription options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task that completes when the stream ends.</returns>
    public async Task SubscribeAsync<T>(
        string topic,
        Func<DataStreamItem<T>, CancellationToken, ValueTask> onMessage,
        string? filter = null,
        string? cursor = null,
        int maxRate = 0,
        int bufferSize = 0,
        IReadOnlyDictionary<string, string>? options = null,
        CancellationToken cancellationToken = default)
    {
        await foreach (var item in SubscribeAsync<T>(
            topic, filter, cursor, maxRate, bufferSize, options, cancellationToken))
        {
            await onMessage(item, cancellationToken);
        }
    }

    /// <summary>
    /// Unpacks a payload from google.protobuf.Any.
    /// </summary>
    private T? UnpackPayload<T>(Google.Protobuf.WellKnownTypes.Any any)
    {
        // Check if it's a RawPayload wrapper
        if (any.Is(RawPayload.Descriptor))
        {
            var rawPayload = any.Unpack<RawPayload>();
            return _serializer.Deserialize<T>(rawPayload.Data.ToByteArray());
        }

        // Check if T is a protobuf message
        if (typeof(Google.Protobuf.IMessage).IsAssignableFrom(typeof(T)))
        {
            var descriptor = (Google.Protobuf.MessageDescriptor?)typeof(T)
                .GetProperty("Descriptor", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                ?.GetValue(null);

            if (descriptor is not null && any.Is(descriptor))
            {
                var unpacked = (T)any.Unpack(descriptor.Parser);
                return unpacked;
            }
        }

        // Try to deserialize as JSON from the Any value
        if (any.Value.Length > 0)
        {
            return _serializer.Deserialize<T>(any.Value.ToByteArray());
        }

        return default;
    }

    /// <summary>
    /// Maps proto ProblemDetails to contract ProblemDetails.
    /// </summary>
    private static ProblemDetails MapProblemDetails(Protos.ProblemDetails proto)
    {
        return new ProblemDetails(
            Type: proto.Type,
            Title: proto.Title,
            Status: proto.Status,
            Detail: proto.Detail,
            Code: proto.Code,
            Instance: proto.Instance,
            Trace: proto.Trace,
            Extensions: proto.Extensions.Count > 0
                ? new Dictionary<string, string>(proto.Extensions)
                : null,
            Errors: proto.Errors.Count > 0
                ? proto.Errors.Select(MapProblemDetails).ToList()
                : null);
    }

    public async ValueTask DisposeAsync()
    {
        _grpcChannel?.Dispose();
    }
}

/// <summary>
/// A single item from a data stream.
/// </summary>
/// <typeparam name="T">Payload type.</typeparam>
/// <param name="StreamId">Stream identifier.</param>
/// <param name="Sequence">Sequence number within the stream.</param>
/// <param name="Payload">Message payload.</param>
/// <param name="Timestamp">Message timestamp.</param>
/// <param name="PartitionKey">Partition key for ordered delivery.</param>
/// <param name="MessageType">Message type hint.</param>
public sealed record DataStreamItem<T>(
    string StreamId,
    long Sequence,
    T? Payload,
    DateTimeOffset Timestamp,
    string? PartitionKey,
    string? MessageType);
