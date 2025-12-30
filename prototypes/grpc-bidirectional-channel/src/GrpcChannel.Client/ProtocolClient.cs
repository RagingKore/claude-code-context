using System.Runtime.CompilerServices;
using Grpc.Core;
using Grpc.Net.Client;
using GrpcChannel.Protocol;
using GrpcChannel.Protocol.Contracts;
using GrpcChannel.Protocol.Protos;
using Microsoft.Extensions.Logging;

namespace GrpcChannel.Client;

/// <summary>
/// Unified protocol client for gRPC duplex communication.
/// Supports bidirectional RPC (request/response) and high-throughput data streams.
/// Handles both protobuf messages and arbitrary types (via serializer).
/// </summary>
/// <param name="options">Connection options.</param>
/// <param name="serializer">Optional payload serializer for non-protobuf types. Defaults to JSON.</param>
/// <param name="logger">Optional logger.</param>
public sealed class ProtocolClient(
    ProtocolClientOptions options,
    IPayloadSerializer? serializer = null,
    ILogger<ProtocolClient>? logger = null) : IAsyncDisposable
{
    private readonly IPayloadSerializer _serializer = serializer ?? JsonPayloadSerializer.Default;
    private GrpcChannel? _grpcChannel;
    private DuplexService.DuplexServiceClient? _client;
    private AsyncDuplexStreamingCall<ProtocolDataUnit, ProtocolDataUnit>? _streamingCall;
    private DuplexChannel? _duplexChannel;
    private CancellationTokenSource? _connectionCts;
    private Task? _receiveTask;
    private SemaphoreSlim? _writeLock;

    // Callback delegates
    private Func<ChannelState, ChannelState, string?, ValueTask>? _stateChangedCallback;
    private Func<ProblemDetails, Exception?, bool, ValueTask>? _errorCallback;

    /// <summary>
    /// Indicates whether the client is connected to the server.
    /// </summary>
    public bool IsConnected => _duplexChannel?.IsConnected ?? false;

    /// <summary>
    /// Gets the client options.
    /// </summary>
    public ProtocolClientOptions Options => options;

    /// <summary>
    /// Registers a callback for connection state changes.
    /// </summary>
    /// <param name="callback">Callback invoked when state changes (previousState, currentState, reason).</param>
    /// <returns>This client instance for fluent chaining.</returns>
    public ProtocolClient OnStateChanged(Func<ChannelState, ChannelState, string?, ValueTask> callback)
    {
        _stateChangedCallback = callback;
        return this;
    }

    /// <summary>
    /// Registers a callback for connection state changes (synchronous version).
    /// </summary>
    /// <param name="callback">Callback invoked when state changes (previousState, currentState, reason).</param>
    /// <returns>This client instance for fluent chaining.</returns>
    public ProtocolClient OnStateChanged(Action<ChannelState, ChannelState, string?> callback)
    {
        _stateChangedCallback = (prev, current, reason) =>
        {
            callback(prev, current, reason);
            return ValueTask.CompletedTask;
        };
        return this;
    }

    /// <summary>
    /// Registers a callback for channel-level errors.
    /// </summary>
    /// <param name="callback">Callback invoked on error (problem, exception, isFatal).</param>
    /// <returns>This client instance for fluent chaining.</returns>
    public ProtocolClient OnError(Func<ProblemDetails, Exception?, bool, ValueTask> callback)
    {
        _errorCallback = callback;
        return this;
    }

    /// <summary>
    /// Registers a callback for channel-level errors (synchronous version).
    /// </summary>
    /// <param name="callback">Callback invoked on error (problem, exception, isFatal).</param>
    /// <returns>This client instance for fluent chaining.</returns>
    public ProtocolClient OnError(Action<ProblemDetails, Exception?, bool> callback)
    {
        _errorCallback = (problem, ex, isFatal) =>
        {
            callback(problem, ex, isFatal);
            return ValueTask.CompletedTask;
        };
        return this;
    }

    /// <summary>
    /// Gets the channel, throwing if not connected.
    /// </summary>
    private DuplexChannel Channel => _duplexChannel
        ?? throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

    /// <summary>
    /// Ensures the gRPC client is initialized (for streaming without full connection).
    /// </summary>
    private DuplexService.DuplexServiceClient EnsureGrpcClient()
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
    /// Connects to the server and establishes the bidirectional RPC channel.
    /// </summary>
    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_duplexChannel is not null)
        {
            throw new InvalidOperationException("Already connected");
        }

        logger?.LogInformation("Connecting to {Server}", options.ServerAddress);

        EnsureGrpcClient();

        var metadata = new Metadata
        {
            { "x-client-id", options.ClientId }
        };

        if (options.Metadata is not null)
        {
            foreach (var kvp in options.Metadata)
            {
                metadata.Add(kvp.Key, kvp.Value);
            }
        }

        _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _streamingCall = _client!.Open(metadata, cancellationToken: _connectionCts.Token);
        _writeLock = new SemaphoreSlim(1, 1);

        // Create the duplex channel with serializer
        var channelId = Guid.NewGuid().ToString("N");
        _duplexChannel = new DuplexChannel(channelId, _serializer);

        // Wire up callbacks from the channel
        _duplexChannel.StateChanged += async (s, e) =>
        {
            if (_stateChangedCallback is not null)
            {
                try
                {
                    await _stateChangedCallback(e.PreviousState, e.CurrentState, e.Reason);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Error in state changed callback");
                }
            }
        };

        _duplexChannel.Error += async (s, e) =>
        {
            if (_errorCallback is not null)
            {
                try
                {
                    await _errorCallback(e.Problem, e.Exception, e.IsFatal);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Error in error callback");
                }
            }
        };

        // Attach the sender
        _duplexChannel.AttachSender(
            async (message, ct) =>
            {
                await _writeLock!.WaitAsync(ct);
                try
                {
                    await _streamingCall.RequestStream.WriteAsync(message, ct);
                }
                finally
                {
                    _writeLock.Release();
                }
            },
            "server",
            includeStackTrace: options.IncludeStackTrace);

        // Start receiving messages
        _receiveTask = ReceiveMessagesAsync(_connectionCts.Token);

        logger?.LogInformation("Connected to {Server} as {ClientId}", options.ServerAddress, options.ClientId);
    }

    /// <summary>
    /// Disconnects from the server.
    /// </summary>
    public async ValueTask DisconnectAsync()
    {
        if (_duplexChannel is null)
        {
            return;
        }

        logger?.LogInformation("Disconnecting from server");

        try
        {
            _connectionCts?.Cancel();

            if (_streamingCall is not null)
            {
                await _streamingCall.RequestStream.CompleteAsync();
            }

            if (_receiveTask is not null)
            {
                try
                {
                    await _receiveTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    logger?.LogWarning("Receive task did not complete in time");
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Error during disconnect");
        }
        finally
        {
            _duplexChannel?.Disconnect("Client disconnected");
        }
    }

    // =========================================================================
    // RPC OPERATIONS
    // =========================================================================

    /// <summary>
    /// Sends a request and awaits the correlated response.
    /// </summary>
    /// <typeparam name="TRequest">Request payload type (protobuf IMessage or any serializable type).</typeparam>
    /// <typeparam name="TResponse">Response payload type (protobuf IMessage or any serializable type).</typeparam>
    /// <param name="method">The method/handler name to invoke on the server.</param>
    /// <param name="request">The request payload.</param>
    /// <param name="timeoutMs">Optional timeout in milliseconds.</param>
    /// <param name="headers">Optional request headers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response result.</returns>
    public ValueTask<DuplexResult<TResponse>> SendRequestAsync<TRequest, TResponse>(
        string method,
        TRequest request,
        int? timeoutMs = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
        => Channel.SendRequestAsync<TRequest, TResponse>(method, request, timeoutMs, headers, cancellationToken);

    /// <summary>
    /// Sends a fire-and-forget notification (no response expected).
    /// </summary>
    /// <typeparam name="T">Notification payload type.</typeparam>
    /// <param name="topic">The notification topic.</param>
    /// <param name="payload">The notification payload.</param>
    /// <param name="headers">Optional headers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public ValueTask SendNotificationAsync<T>(
        string topic,
        T payload,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
        => Channel.SendNotificationAsync(topic, payload, headers, cancellationToken);

    /// <summary>
    /// Registers a request handler for a specific method.
    /// The server can invoke this handler by sending a request with the matching method name.
    /// </summary>
    /// <typeparam name="TRequest">Request payload type (protobuf IMessage or any serializable type).</typeparam>
    /// <typeparam name="TResponse">Response payload type (protobuf IMessage or any serializable type).</typeparam>
    /// <param name="method">The method name to handle.</param>
    /// <param name="handler">The handler function.</param>
    /// <returns>A disposable that unregisters the handler when disposed.</returns>
    public IDisposable OnRequest<TRequest, TResponse>(
        string method,
        Func<TRequest, RequestContext, CancellationToken, ValueTask<TResponse>> handler)
        => Channel.OnRequest<TRequest, TResponse>(method, handler);

    /// <summary>
    /// Registers a notification handler for a specific topic.
    /// The server can send notifications that will be handled by this handler.
    /// </summary>
    /// <typeparam name="T">Notification payload type (protobuf IMessage or any serializable type).</typeparam>
    /// <param name="topic">The topic to subscribe to.</param>
    /// <param name="handler">The handler function.</param>
    /// <returns>A disposable that unregisters the handler when disposed.</returns>
    public IDisposable OnNotification<T>(
        string topic,
        Func<T, NotificationContext, CancellationToken, ValueTask> handler)
        => Channel.OnNotification<T>(topic, handler);

    // =========================================================================
    // HIGH-THROUGHPUT DATA STREAMS
    // =========================================================================

    /// <summary>
    /// Subscribes to a high-throughput data stream.
    /// Uses a separate server-side streaming RPC to avoid flooding the control channel.
    /// </summary>
    /// <typeparam name="T">Expected payload type.</typeparam>
    /// <param name="topic">Topic to subscribe to.</param>
    /// <param name="filter">Optional filter expression.</param>
    /// <param name="cursor">Optional starting position for resumption.</param>
    /// <param name="maxRate">Maximum messages per second (0 = unlimited).</param>
    /// <param name="bufferSize">Buffer size hint for the server.</param>
    /// <param name="streamOptions">Additional topic-specific options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Async enumerable of data stream items.</returns>
    public async IAsyncEnumerable<DataStreamItem<T>> SubscribeAsync<T>(
        string topic,
        string? filter = null,
        string? cursor = null,
        int maxRate = 0,
        int bufferSize = 0,
        IReadOnlyDictionary<string, string>? streamOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = EnsureGrpcClient();
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

        if (streamOptions is not null)
        {
            foreach (var kvp in streamOptions)
            {
                request.Options.Add(kvp.Key, kvp.Value);
            }
        }

        var metadata = new Metadata
        {
            { "x-client-id", options.ClientId }
        };

        logger?.LogDebug(
            "Subscribing to stream {Topic} with filter '{Filter}'",
            topic, filter ?? "(none)");

        using var call = client.Subscribe(request, metadata, cancellationToken: cancellationToken);

        await foreach (var message in call.ResponseStream.ReadAllAsync(cancellationToken))
        {
            // Check for error
            if (message.Error is not null && !string.IsNullOrEmpty(message.Error.Code))
            {
                var problem = MapProtoProblemDetails(message.Error);
                logger?.LogError(
                    "Stream error: {Code} - {Detail}",
                    problem.Code, problem.Detail);

                throw new DuplexException(problem);
            }

            // Check for completion
            if (message.IsComplete)
            {
                logger?.LogDebug(
                    "Stream {StreamId} completed after {Count} messages",
                    streamId, message.Sequence);
                yield break;
            }

            // Unpack payload
            T? payload = default;
            if (message.Payload is not null && !string.IsNullOrEmpty(message.Payload.TypeUrl))
            {
                payload = UnpackPayload<T>(message.Payload);
            }

            yield return new DataStreamItem<T>(
                StreamId: message.StreamId,
                Sequence: message.Sequence,
                Payload: payload,
                Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(message.TimestampUtc),
                PartitionKey: string.IsNullOrEmpty(message.PartitionKey) ? null : message.PartitionKey,
                MessageType: string.IsNullOrEmpty(message.MessageType) ? null : message.MessageType);
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
    /// <param name="streamOptions">Additional subscription options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task that completes when the stream ends.</returns>
    public async Task SubscribeAsync<T>(
        string topic,
        Func<DataStreamItem<T>, CancellationToken, ValueTask> onMessage,
        string? filter = null,
        string? cursor = null,
        int maxRate = 0,
        int bufferSize = 0,
        IReadOnlyDictionary<string, string>? streamOptions = null,
        CancellationToken cancellationToken = default)
    {
        await foreach (var item in SubscribeAsync<T>(
            topic, filter, cursor, maxRate, bufferSize, streamOptions, cancellationToken))
        {
            await onMessage(item, cancellationToken);
        }
    }

    // =========================================================================
    // INTERNAL HELPERS
    // =========================================================================

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
    private static ProblemDetails MapProtoProblemDetails(Protos.ProblemDetails proto)
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
                ? proto.Errors.Select(MapProtoProblemDetails).ToList()
                : null);
    }

    private async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in _streamingCall!.ResponseStream.ReadAllAsync(cancellationToken))
            {
                await _duplexChannel!.ProcessIncomingAsync(message, cancellationToken);
            }

            // Stream ended normally
            logger?.LogInformation("Server closed the connection");
            _duplexChannel?.Disconnect("Server closed the connection");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            logger?.LogInformation("Connection was cancelled");
            _duplexChannel?.Disconnect("Connection cancelled");
        }
        catch (RpcException ex)
        {
            // Convert gRPC exception to ProblemDetails
            var problem = MapGrpcException(ex);
            logger?.LogError(ex, "gRPC error: {Status} - {Detail}", ex.StatusCode, ex.Status.Detail);
            _duplexChannel?.Fault(problem, ex);
        }
        catch (OperationCanceledException)
        {
            logger?.LogInformation("Receive loop was cancelled");
            _duplexChannel?.Disconnect("Operation cancelled");
        }
        catch (Exception ex)
        {
            // Convert general exception to ProblemDetails
            var problem = ProblemDetails.FromException(ex, options.IncludeStackTrace);
            logger?.LogError(ex, "Error receiving messages");
            _duplexChannel?.Fault(problem, ex);
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
            StatusCode.Unauthenticated => new ProblemDetails(
                Type: "urn:grpc:duplex:unauthenticated",
                Title: "Unauthenticated",
                Status: (int)Contracts.StatusCode.Unauthorized,
                Detail: detail,
                Code: "GRPC_UNAUTHENTICATED"),
            StatusCode.PermissionDenied => new ProblemDetails(
                Type: "urn:grpc:duplex:permission-denied",
                Title: "Permission Denied",
                Status: (int)Contracts.StatusCode.Unauthorized,
                Detail: detail,
                Code: "GRPC_PERMISSION_DENIED"),
            StatusCode.NotFound => ProblemDetails.NotFound(detail),
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

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();

        if (_duplexChannel is not null)
        {
            await _duplexChannel.DisposeAsync();
            _duplexChannel = null;
        }

        _streamingCall?.Dispose();
        _grpcChannel?.Dispose();
        _connectionCts?.Dispose();
        _writeLock?.Dispose();
    }
}

/// <summary>
/// Options for configuring the protocol client.
/// </summary>
/// <param name="ServerAddress">Server address (e.g., "https://localhost:5001").</param>
/// <param name="ClientId">Client identifier.</param>
/// <param name="Metadata">Optional connection metadata.</param>
/// <param name="IncludeStackTrace">Include stack traces in error responses.</param>
public sealed record ProtocolClientOptions(
    string ServerAddress,
    string ClientId,
    IReadOnlyDictionary<string, string>? Metadata = null,
    bool IncludeStackTrace = false)
{
    /// <summary>
    /// Creates options for local development.
    /// </summary>
    public static ProtocolClientOptions ForLocalDevelopment(string? clientId = null, int port = 5001) =>
        new($"https://localhost:{port}", clientId ?? $"client-{Environment.ProcessId}", IncludeStackTrace: true);
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
