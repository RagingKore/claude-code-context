using System.Collections.Concurrent;
using Grpc.Core;
using GrpcChannel.Protocol;
using GrpcChannel.Protocol.Contracts;
using GrpcChannel.Protocol.Protos;

namespace GrpcChannel.Server.Services;

/// <summary>
/// gRPC service implementation for the duplex channel.
/// Manages connections and provides bidirectional RPC for both sides.
/// Handles both protobuf messages and arbitrary types (via serializer).
/// </summary>
/// <param name="logger">Logger instance.</param>
/// <param name="connectionRegistry">Connection registry for tracking active channels.</param>
/// <param name="dataStreamRegistry">Registry for data stream handlers.</param>
/// <param name="hostEnvironment">Host environment for determining debug mode.</param>
/// <param name="serializer">Optional payload serializer for non-protobuf types. Defaults to JSON.</param>
public sealed class DuplexServiceImpl(
    ILogger<DuplexServiceImpl> logger,
    IConnectionRegistry connectionRegistry,
    IDataStreamRegistry dataStreamRegistry,
    IHostEnvironment hostEnvironment,
    IPayloadSerializer? serializer = null) : DuplexService.DuplexServiceBase
{
    /// <summary>
    /// Opens a bidirectional duplex channel.
    /// </summary>
    public override async Task Open(
        IAsyncStreamReader<ProtocolDataUnit> requestStream,
        IServerStreamWriter<ProtocolDataUnit> responseStream,
        ServerCallContext context)
    {
        var channelId = Guid.NewGuid().ToString("N");
        var clientId = context.RequestHeaders.GetValue("x-client-id") ?? channelId;

        logger.LogInformation("Client {ClientId} connected, channel {ChannelId}", clientId, channelId);

        var channel = new DuplexChannel(channelId, serializer);

        // Attach the gRPC stream as the sender
        var writeLock = new SemaphoreSlim(1, 1);
        channel.AttachSender(
            async (message, ct) =>
            {
                await writeLock.WaitAsync(ct);
                try
                {
                    await responseStream.WriteAsync(message, ct);
                }
                finally
                {
                    writeLock.Release();
                }
            },
            clientId,
            includeStackTrace: hostEnvironment.IsDevelopment());

        // Register with the connection registry
        connectionRegistry.Register(channelId, channel, clientId);

        try
        {
            // Process incoming messages
            await foreach (var message in requestStream.ReadAllAsync(context.CancellationToken))
            {
                await channel.ProcessIncomingAsync(message, context.CancellationToken);
            }

            // Stream ended normally
            logger.LogInformation("Client {ClientId} closed the connection", clientId);
            channel.Disconnect("Client closed the connection");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            logger.LogInformation("Channel {ChannelId} was cancelled", channelId);
            channel.Disconnect("Connection cancelled");
        }
        catch (RpcException ex)
        {
            // Convert gRPC exception to ProblemDetails
            var problem = MapGrpcException(ex);
            logger.LogError(ex, "gRPC error on channel {ChannelId}: {Status}", channelId, ex.StatusCode);
            channel.Fault(problem, ex);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Channel {ChannelId} was cancelled", channelId);
            channel.Disconnect("Operation cancelled");
        }
        catch (Exception ex)
        {
            // Convert general exception to ProblemDetails
            var problem = ProblemDetails.FromException(ex, hostEnvironment.IsDevelopment());
            logger.LogError(ex, "Error processing channel {ChannelId}", channelId);
            channel.Fault(problem, ex);
        }
        finally
        {
            connectionRegistry.Unregister(channelId);
            await channel.DisposeAsync();
            writeLock.Dispose();

            logger.LogInformation("Client {ClientId} disconnected from channel {ChannelId}", clientId, channelId);
        }
    }

    /// <summary>
    /// Subscribes to a high-throughput data stream.
    /// Server streams data to the client based on the subscription request.
    /// </summary>
    public override async Task Subscribe(
        DataStreamRequest request,
        IServerStreamWriter<DataStreamMessage> responseStream,
        ServerCallContext context)
    {
        var clientId = context.RequestHeaders.GetValue("x-client-id") ?? "unknown";
        var streamId = request.StreamId;

        if (string.IsNullOrEmpty(streamId))
        {
            streamId = Guid.NewGuid().ToString("N");
        }

        logger.LogInformation(
            "Client {ClientId} subscribing to stream {StreamId} on topic {Topic}",
            clientId, streamId, request.Topic);

        // Get the handler for this topic
        var handler = dataStreamRegistry.GetHandler(request.Topic);

        if (handler is null)
        {
            // Send error message and complete
            await responseStream.WriteAsync(new DataStreamMessage
            {
                StreamId = streamId,
                Sequence = 0,
                TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IsComplete = true,
                Error = new Protos.ProblemDetails
                {
                    Type = "urn:grpc:duplex:topic-not-found",
                    Title = "Topic Not Found",
                    Status = (int)Contracts.StatusCode.NotFound,
                    Detail = $"No handler registered for topic '{request.Topic}'",
                    Code = "TOPIC_NOT_FOUND"
                }
            }, context.CancellationToken);
            return;
        }

        try
        {
            // Create stream context
            var streamContext = new DataStreamContext(
                StreamId: streamId,
                Topic: request.Topic,
                ClientId: clientId,
                Filter: request.Filter,
                Cursor: request.Cursor,
                MaxRate: request.MaxRate,
                BufferSize: request.BufferSize,
                Options: new Dictionary<string, string>(request.Options),
                CancellationToken: context.CancellationToken);

            // Create writer wrapper
            var writer = new DataStreamWriter(responseStream, streamId, serializer);

            // Invoke the handler
            await handler(streamContext, writer, context.CancellationToken);

            // Send completion message if not already sent
            if (!writer.IsCompleted)
            {
                await writer.CompleteAsync();
            }

            logger.LogInformation(
                "Stream {StreamId} completed. Sent {Count} messages",
                streamId, writer.MessageCount);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Stream {StreamId} was cancelled", streamId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in stream {StreamId}", streamId);

            // Try to send error message
            try
            {
                await responseStream.WriteAsync(new DataStreamMessage
                {
                    StreamId = streamId,
                    TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    IsComplete = true,
                    Error = new Protos.ProblemDetails
                    {
                        Type = "urn:grpc:duplex:stream-error",
                        Title = "Stream Error",
                        Status = (int)Contracts.StatusCode.Error,
                        Detail = ex.Message,
                        Code = ex.GetType().Name,
                        Trace = hostEnvironment.IsDevelopment() ? ex.StackTrace : null
                    }
                }, context.CancellationToken);
            }
            catch
            {
                // Stream may already be closed
            }
        }
    }

    /// <summary>
    /// Maps a gRPC exception to ProblemDetails.
    /// </summary>
    private static ProblemDetails MapGrpcException(RpcException ex)
    {
        var grpcStatus = ex.StatusCode.ToString();
        var detail = !string.IsNullOrEmpty(ex.Status.Detail)
            ? ex.Status.Detail
            : ex.Message;

        return ex.StatusCode switch
        {
            StatusCode.Unavailable => ProblemDetails.TransportError(grpcStatus, $"Service unavailable: {detail}"),
            StatusCode.DeadlineExceeded => ProblemDetails.Timeout(0) with { Detail = detail },
            StatusCode.Cancelled => ProblemDetails.Cancelled(),
            StatusCode.InvalidArgument => ProblemDetails.InvalidMessage(detail),
            StatusCode.Internal => new ProblemDetails(
                Type: "urn:grpc:duplex:internal",
                Title: "Internal Error",
                Status: (int)Contracts.StatusCode.Internal,
                Detail: detail,
                Code: "GRPC_INTERNAL"),
            _ => ProblemDetails.TransportError(grpcStatus, detail)
        };
    }
}

/// <summary>
/// Registry for tracking active duplex channels.
/// </summary>
public interface IConnectionRegistry
{
    /// <summary>
    /// Registers a new channel.
    /// </summary>
    void Register(string channelId, IDuplexChannel channel, string? clientId = null);

    /// <summary>
    /// Unregisters a channel.
    /// </summary>
    void Unregister(string channelId);

    /// <summary>
    /// Gets a channel by ID.
    /// </summary>
    IDuplexChannel? GetChannel(string channelId);

    /// <summary>
    /// Gets a channel by client ID.
    /// </summary>
    IDuplexChannel? GetChannelByClientId(string clientId);

    /// <summary>
    /// Gets all active channels.
    /// </summary>
    IReadOnlyCollection<ChannelInfo> GetAllChannels();

    /// <summary>
    /// Registers handlers on all current and future channels.
    /// </summary>
    IDisposable OnAllChannels(Action<IDuplexChannel> configure);
}

/// <summary>
/// Information about an active channel.
/// </summary>
/// <param name="ChannelId">The channel ID.</param>
/// <param name="ClientId">The client ID.</param>
/// <param name="Channel">The channel instance.</param>
/// <param name="ConnectedAt">When the channel was connected.</param>
public sealed record ChannelInfo(
    string ChannelId,
    string? ClientId,
    IDuplexChannel Channel,
    DateTimeOffset ConnectedAt);

/// <summary>
/// Default implementation of connection registry.
/// </summary>
/// <param name="logger">Logger instance.</param>
public sealed class ConnectionRegistry(ILogger<ConnectionRegistry> logger) : IConnectionRegistry
{
    private readonly ConcurrentDictionary<string, ChannelInfo> _channels = new();
    private readonly ConcurrentDictionary<string, string> _clientToChannel = new();
    private readonly List<Action<IDuplexChannel>> _channelConfigurators = [];
    private readonly object _configuratorsLock = new();

    public void Register(string channelId, IDuplexChannel channel, string? clientId = null)
    {
        var info = new ChannelInfo(channelId, clientId, channel, DateTimeOffset.UtcNow);
        _channels[channelId] = info;

        if (clientId is not null)
        {
            _clientToChannel[clientId] = channelId;
        }

        // Apply all registered configurators
        lock (_configuratorsLock)
        {
            foreach (var configure in _channelConfigurators)
            {
                try
                {
                    configure(channel);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error applying channel configurator");
                }
            }
        }

        logger.LogDebug("Registered channel {ChannelId} for client {ClientId}. Total: {Count}",
            channelId, clientId, _channels.Count);
    }

    public void Unregister(string channelId)
    {
        if (_channels.TryRemove(channelId, out var info) && info.ClientId is not null)
        {
            _clientToChannel.TryRemove(info.ClientId, out _);
        }

        logger.LogDebug("Unregistered channel {ChannelId}. Total: {Count}", channelId, _channels.Count);
    }

    public IDuplexChannel? GetChannel(string channelId) =>
        _channels.TryGetValue(channelId, out var info) ? info.Channel : null;

    public IDuplexChannel? GetChannelByClientId(string clientId) =>
        _clientToChannel.TryGetValue(clientId, out var channelId) ? GetChannel(channelId) : null;

    public IReadOnlyCollection<ChannelInfo> GetAllChannels() =>
        _channels.Values.ToList().AsReadOnly();

    public IDisposable OnAllChannels(Action<IDuplexChannel> configure)
    {
        lock (_configuratorsLock)
        {
            _channelConfigurators.Add(configure);
        }

        // Apply to existing channels
        foreach (var info in _channels.Values)
        {
            try
            {
                configure(info.Channel);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error applying channel configurator to existing channel");
            }
        }

        return new ConfiguratorRegistration(() =>
        {
            lock (_configuratorsLock)
            {
                _channelConfigurators.Remove(configure);
            }
        });
    }

    private sealed class ConfiguratorRegistration(Action unregister) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                unregister();
            }
        }
    }
}

/// <summary>
/// Registry for data stream handlers.
/// </summary>
public interface IDataStreamRegistry
{
    /// <summary>
    /// Registers a handler for a topic.
    /// </summary>
    void Register(string topic, DataStreamHandler handler);

    /// <summary>
    /// Gets the handler for a topic.
    /// </summary>
    DataStreamHandler? GetHandler(string topic);
}

/// <summary>
/// Handler for data streams.
/// </summary>
public delegate Task DataStreamHandler(
    DataStreamContext context,
    IDataStreamWriter writer,
    CancellationToken cancellationToken);

/// <summary>
/// Context for a data stream subscription.
/// </summary>
public sealed record DataStreamContext(
    string StreamId,
    string Topic,
    string ClientId,
    string? Filter,
    string? Cursor,
    int MaxRate,
    int BufferSize,
    IReadOnlyDictionary<string, string> Options,
    CancellationToken CancellationToken);

/// <summary>
/// Writer for sending data stream messages.
/// </summary>
public interface IDataStreamWriter
{
    /// <summary>
    /// Number of messages sent.
    /// </summary>
    long MessageCount { get; }

    /// <summary>
    /// Whether the stream has been completed.
    /// </summary>
    bool IsCompleted { get; }

    /// <summary>
    /// Writes a message to the stream.
    /// </summary>
    ValueTask WriteAsync<T>(T payload, string? partitionKey = null, string? messageType = null);

    /// <summary>
    /// Completes the stream.
    /// </summary>
    ValueTask CompleteAsync();
}

/// <summary>
/// Default implementation of data stream writer.
/// </summary>
internal sealed class DataStreamWriter(
    IServerStreamWriter<DataStreamMessage> stream,
    string streamId,
    IPayloadSerializer? serializer) : IDataStreamWriter
{
    private readonly IPayloadSerializer _serializer = serializer ?? JsonPayloadSerializer.Default;
    private long _sequence;
    private bool _isCompleted;

    public long MessageCount => _sequence;
    public bool IsCompleted => _isCompleted;

    public async ValueTask WriteAsync<T>(T payload, string? partitionKey = null, string? messageType = null)
    {
        if (_isCompleted)
        {
            throw new InvalidOperationException("Stream has already been completed");
        }

        var message = new DataStreamMessage
        {
            StreamId = streamId,
            Sequence = Interlocked.Increment(ref _sequence),
            Payload = PackPayload(payload),
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PartitionKey = partitionKey ?? string.Empty,
            MessageType = messageType ?? typeof(T).Name
        };

        await stream.WriteAsync(message);
    }

    public async ValueTask CompleteAsync()
    {
        if (_isCompleted) return;
        _isCompleted = true;

        await stream.WriteAsync(new DataStreamMessage
        {
            StreamId = streamId,
            Sequence = _sequence + 1,
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            IsComplete = true
        });
    }

    private Google.Protobuf.WellKnownTypes.Any PackPayload<T>(T value)
    {
        if (value is null)
        {
            return new Google.Protobuf.WellKnownTypes.Any();
        }

        if (value is Google.Protobuf.IMessage protoMessage)
        {
            return Google.Protobuf.WellKnownTypes.Any.Pack(protoMessage);
        }

        var data = _serializer.Serialize(value);
        var rawPayload = new RawPayload
        {
            Data = Google.Protobuf.ByteString.CopyFrom(data),
            TypeName = typeof(T).FullName ?? typeof(T).Name,
            ContentType = _serializer.ContentType
        };

        return Google.Protobuf.WellKnownTypes.Any.Pack(rawPayload);
    }
}

/// <summary>
/// Default implementation of data stream registry.
/// </summary>
public sealed class DataStreamRegistry : IDataStreamRegistry
{
    private readonly ConcurrentDictionary<string, DataStreamHandler> _handlers = new();

    public void Register(string topic, DataStreamHandler handler)
    {
        _handlers[topic] = handler;
    }

    public DataStreamHandler? GetHandler(string topic)
    {
        return _handlers.TryGetValue(topic, out var handler) ? handler : null;
    }
}
