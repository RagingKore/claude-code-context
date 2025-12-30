using System.Collections.Concurrent;
using System.Diagnostics;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using GrpcChannel.Protocol.Contracts;
using GrpcChannel.Protocol.Protos;
using ProtoStatusCode = GrpcChannel.Protocol.Protos.StatusCode;
using StatusCode = GrpcChannel.Protocol.Contracts.StatusCode;
using ProtoProblemDetails = GrpcChannel.Protocol.Protos.ProblemDetails;
using ProblemDetails = GrpcChannel.Protocol.Contracts.ProblemDetails;

namespace GrpcChannel.Protocol;

/// <summary>
/// Core duplex channel implementation providing bidirectional request/response
/// with handler registration. This class is used by both client and server.
/// Supports both protobuf messages (packed directly into Any) and arbitrary types
/// (serialized and wrapped in RawPayload).
/// </summary>
/// <param name="channelId">Unique channel identifier.</param>
/// <param name="serializer">Optional serializer for non-protobuf types. Defaults to JSON.</param>
public sealed class DuplexChannel(string channelId, IPayloadSerializer? serializer = null) : IDuplexChannel
{
    private readonly IPayloadSerializer _serializer = serializer ?? JsonPayloadSerializer.Default;
    private readonly ConcurrentDictionary<string, PendingRequest> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, IRequestHandler> _requestHandlers = new();
    private readonly ConcurrentDictionary<string, INotificationHandler> _notificationHandlers = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private Func<ProtocolDataUnit, CancellationToken, ValueTask>? _sendFunc;
    private ChannelState _state = ChannelState.Disconnected;
    private string? _remoteId;
    private bool _includeStackTrace;

    public string ChannelId { get; } = channelId;
    public bool IsConnected => _state == ChannelState.Connected;

    public event EventHandler<ChannelStateChangedEventArgs>? StateChanged;
    public event EventHandler<ChannelErrorEventArgs>? Error;

    /// <summary>
    /// Attaches the send function for outgoing messages.
    /// Called by transport layer (gRPC streaming).
    /// </summary>
    public void AttachSender(
        Func<ProtocolDataUnit, CancellationToken, ValueTask> sendFunc,
        string? remoteId = null,
        bool includeStackTrace = false)
    {
        _sendFunc = sendFunc;
        _remoteId = remoteId;
        _includeStackTrace = includeStackTrace;
        SetState(ChannelState.Connected);
    }

    /// <summary>
    /// Processes an incoming message from the transport layer.
    /// </summary>
    public async ValueTask ProcessIncomingAsync(ProtocolDataUnit message, CancellationToken cancellationToken = default)
    {
        // Determine message type based on fields:
        // - Response: has correlation_id, no method (response to a request)
        // - Request: has correlation_id and method (expects response)
        // - Notification: has method, no correlation_id (fire-and-forget)

        var hasCorrelationId = !string.IsNullOrEmpty(message.CorrelationId);
        var hasMethod = !string.IsNullOrEmpty(message.Method);

        if (hasCorrelationId && !hasMethod)
        {
            // This is a response
            HandleIncomingResponse(message);
        }
        else if (hasCorrelationId && hasMethod)
        {
            // This is a request expecting a response
            await HandleIncomingRequestAsync(message, cancellationToken);
        }
        else if (hasMethod)
        {
            // This is a notification (fire-and-forget)
            await HandleIncomingNotificationAsync(message, cancellationToken);
        }
    }

    public async ValueTask<DuplexResult<TResponse>> SendRequestAsync<TRequest, TResponse>(
        string method,
        TRequest request,
        int? timeoutMs = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ProtocolDataUnit>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRequest(correlationId, tcs, Stopwatch.StartNew(), timeoutMs, typeof(TResponse));

        if (!_pendingRequests.TryAdd(correlationId, pending))
        {
            throw new InvalidOperationException($"Duplicate correlation ID: {correlationId}");
        }

        try
        {
            var message = new ProtocolDataUnit
            {
                Id = Guid.NewGuid().ToString("N"),
                CorrelationId = correlationId,
                Method = method,
                Payload = PackPayload(request),
                TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            if (timeoutMs.HasValue)
            {
                message.TimeoutMs = timeoutMs.Value;
            }

            if (headers is not null)
            {
                foreach (var kvp in headers)
                {
                    message.Headers[kvp.Key] = kvp.Value;
                }
            }

            await SendMessageAsync(message, cancellationToken);

            // Wait for response with timeout
            using var cts = timeoutMs.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;

            cts?.CancelAfter(timeoutMs!.Value);

            var effectiveToken = cts?.Token ?? cancellationToken;

            await using var registration = effectiveToken.Register(() =>
            {
                if (_pendingRequests.TryRemove(correlationId, out var removed))
                {
                    removed.Stopwatch.Stop();
                    removed.Completion.TrySetCanceled(effectiveToken);
                }
            });

            var response = await tcs.Task;
            pending.Stopwatch.Stop();

            var responseHeaders = response.Headers.Count > 0
                ? new Dictionary<string, string>(response.Headers)
                : null;

            if (response.Status == ProtoStatusCode.Ok)
            {
                var responsePayload = UnpackPayload<TResponse>(response.Payload);
                return DuplexResult<TResponse>.Ok(responsePayload, pending.Stopwatch.ElapsedMilliseconds, responseHeaders);
            }

            return DuplexResult<TResponse>.Fail(
                MapStatus(response.Status),
                MapProblemDetails(response.Problem),
                pending.Stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (timeoutMs.HasValue)
        {
            pending.Stopwatch.Stop();
            _pendingRequests.TryRemove(correlationId, out _);
            return DuplexResult<TResponse>.Fail(
                StatusCode.Timeout,
                ProblemDetails.Timeout(timeoutMs.Value),
                pending.Stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            pending.Stopwatch.Stop();
            _pendingRequests.TryRemove(correlationId, out _);
            return DuplexResult<TResponse>.Fail(
                StatusCode.Cancelled,
                ProblemDetails.Cancelled(),
                pending.Stopwatch.ElapsedMilliseconds);
        }
    }

    public async ValueTask SendNotificationAsync<T>(
        string topic,
        T payload,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var message = new ProtocolDataUnit
        {
            Id = Guid.NewGuid().ToString("N"),
            Method = topic, // For notifications, method is the topic
            // No correlation_id for notifications
            Payload = PackPayload(payload),
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        if (headers is not null)
        {
            foreach (var kvp in headers)
            {
                message.Headers[kvp.Key] = kvp.Value;
            }
        }

        await SendMessageAsync(message, cancellationToken);
    }

    public IDisposable OnRequest<TRequest, TResponse>(
        string method,
        Func<TRequest, RequestContext, CancellationToken, ValueTask<TResponse>> handler)
    {
        var wrapper = new RequestHandler<TRequest, TResponse>(handler, this);
        _requestHandlers[method] = wrapper;
        return new HandlerRegistration(() => _requestHandlers.TryRemove(method, out _));
    }

    public IDisposable OnNotification<T>(
        string topic,
        Func<T, NotificationContext, CancellationToken, ValueTask> handler)
    {
        var wrapper = new NotificationHandler<T>(handler, this);
        _notificationHandlers[topic] = wrapper;
        return new HandlerRegistration(() => _notificationHandlers.TryRemove(topic, out _));
    }

    public async ValueTask DisposeAsync()
    {
        SetState(ChannelState.Disconnected);

        foreach (var kvp in _pendingRequests)
        {
            kvp.Value.Completion.TrySetCanceled();
        }
        _pendingRequests.Clear();

        _requestHandlers.Clear();
        _notificationHandlers.Clear();
        _sendFunc = null;

        _sendLock.Dispose();

        await ValueTask.CompletedTask;
    }

    /// <summary>
    /// Marks the channel as disconnected and fails all pending requests.
    /// </summary>
    public void Disconnect(string? reason = null)
    {
        var problem = ProblemDetails.ConnectionLost(reason);
        SetState(ChannelState.Disconnected, reason);
        FailAllPendingRequests(problem);
    }

    /// <summary>
    /// Marks the channel as faulted due to an error.
    /// </summary>
    public void Fault(ProblemDetails problem, Exception? exception = null)
    {
        SetState(ChannelState.Faulted, problem.Detail);
        RaiseError(problem, exception, isFatal: true);
        FailAllPendingRequests(problem);
    }

    /// <summary>
    /// Raises an error event without changing channel state.
    /// </summary>
    public void RaiseError(ProblemDetails problem, Exception? exception = null, bool isFatal = false)
    {
        Error?.Invoke(this, new ChannelErrorEventArgs(problem, exception, isFatal));
    }

    /// <summary>
    /// Fails all pending requests with the given problem details.
    /// </summary>
    private void FailAllPendingRequests(ProblemDetails problem)
    {
        var exception = new DuplexException(
            (StatusCode)(problem.Status ?? (int)StatusCode.Error),
            problem);

        foreach (var kvp in _pendingRequests)
        {
            kvp.Value.Completion.TrySetException(exception);
        }
        _pendingRequests.Clear();
    }

    /// <summary>
    /// Packs a payload into protobuf Any.
    /// For IMessage types: packs directly.
    /// For other types: serializes to RawPayload wrapper, then packs.
    /// </summary>
    internal Any PackPayload<T>(T value)
    {
        if (value is null)
        {
            return new Any();
        }

        // If it's already a protobuf message, pack directly
        if (value is IMessage protoMessage)
        {
            return Any.Pack(protoMessage);
        }

        // Otherwise, serialize and wrap in RawPayload
        var data = _serializer.Serialize(value);
        var rawPayload = new RawPayload
        {
            Data = Google.Protobuf.ByteString.CopyFrom(data),
            TypeName = typeof(T).FullName ?? typeof(T).Name,
            ContentType = _serializer.ContentType
        };

        return Any.Pack(rawPayload);
    }

    /// <summary>
    /// Unpacks a payload from protobuf Any.
    /// Detects whether it's a direct IMessage or a RawPayload wrapper.
    /// </summary>
    internal T UnpackPayload<T>(Any? any)
    {
        if (any is null || any.TypeUrl == string.Empty)
        {
            return default!;
        }

        // Check if it's a RawPayload wrapper
        if (any.Is(RawPayload.Descriptor))
        {
            var rawPayload = any.Unpack<RawPayload>();
            return _serializer.Deserialize<T>(rawPayload.Data.ToByteArray());
        }

        // Otherwise, it's a direct protobuf message
        if (typeof(IMessage).IsAssignableFrom(typeof(T)))
        {
            return any.Unpack<T>();
        }

        // Type mismatch - try to deserialize anyway (shouldn't happen normally)
        throw new InvalidOperationException(
            $"Cannot unpack payload of type '{any.TypeUrl}' as '{typeof(T).Name}'. " +
            "Expected either a protobuf IMessage or RawPayload wrapper.");
    }

    /// <summary>
    /// Unpacks a payload from protobuf Any to a specific type.
    /// Used by handlers when the type is known at runtime.
    /// </summary>
    internal object UnpackPayload(Any? any, Type targetType)
    {
        if (any is null || any.TypeUrl == string.Empty)
        {
            return Activator.CreateInstance(targetType)!;
        }

        // Check if it's a RawPayload wrapper
        if (any.Is(RawPayload.Descriptor))
        {
            var rawPayload = any.Unpack<RawPayload>();
            return _serializer.Deserialize(rawPayload.Data.ToByteArray(), targetType);
        }

        // Otherwise, it's a direct protobuf message - use reflection to call Unpack<T>
        if (typeof(IMessage).IsAssignableFrom(targetType))
        {
            var unpackMethod = typeof(Any).GetMethod(nameof(Any.Unpack))!.MakeGenericMethod(targetType);
            return unpackMethod.Invoke(any, null)!;
        }

        throw new InvalidOperationException(
            $"Cannot unpack payload of type '{any.TypeUrl}' as '{targetType.Name}'.");
    }

    private async ValueTask HandleIncomingRequestAsync(ProtocolDataUnit message, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        ProtocolDataUnit response;

        if (_requestHandlers.TryGetValue(message.Method, out var handler))
        {
            var context = new RequestContext(
                message.CorrelationId,
                message.Method,
                new Dictionary<string, string>(message.Headers),
                _remoteId);

            try
            {
                var responsePayload = await handler.HandleAsync(message.Payload, context, cancellationToken);
                stopwatch.Stop();

                response = new ProtocolDataUnit
                {
                    Id = Guid.NewGuid().ToString("N"),
                    CorrelationId = message.CorrelationId,
                    // No method for responses
                    Status = ProtoStatusCode.Ok,
                    Payload = responsePayload,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                response = new ProtocolDataUnit
                {
                    Id = Guid.NewGuid().ToString("N"),
                    CorrelationId = message.CorrelationId,
                    Status = ProtoStatusCode.Error,
                    Problem = CreateProtoProblemDetails(ex),
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
            }
        }
        else
        {
            stopwatch.Stop();

            response = new ProtocolDataUnit
            {
                Id = Guid.NewGuid().ToString("N"),
                CorrelationId = message.CorrelationId,
                Status = ProtoStatusCode.NotFound,
                Problem = new ProtoProblemDetails
                {
                    Type = "urn:grpc:duplex:not-found",
                    Title = "Method Not Found",
                    Status = (int)StatusCode.NotFound,
                    Detail = $"Method '{message.Method}' was not found",
                    Code = "METHOD_NOT_FOUND"
                },
                DurationMs = stopwatch.ElapsedMilliseconds,
                TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }

        await SendMessageAsync(response, cancellationToken);
    }

    private void HandleIncomingResponse(ProtocolDataUnit message)
    {
        if (_pendingRequests.TryRemove(message.CorrelationId, out var pending))
        {
            pending.Completion.TrySetResult(message);
        }
    }

    private async ValueTask HandleIncomingNotificationAsync(ProtocolDataUnit message, CancellationToken cancellationToken)
    {
        if (_notificationHandlers.TryGetValue(message.Method, out var handler))
        {
            var context = new NotificationContext(
                message.Method,
                new Dictionary<string, string>(message.Headers),
                _remoteId);

            try
            {
                await handler.HandleAsync(message.Payload, context, cancellationToken);
            }
            catch
            {
                // Notifications are fire-and-forget, swallow exceptions
            }
        }
    }

    private async ValueTask SendMessageAsync(ProtocolDataUnit message, CancellationToken cancellationToken)
    {
        if (_sendFunc is null)
        {
            throw new InvalidOperationException("Channel is not connected");
        }

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _sendFunc(message, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void SetState(ChannelState newState, string? reason = null)
    {
        var previousState = _state;
        _state = newState;

        if (previousState != newState)
        {
            StateChanged?.Invoke(this, new ChannelStateChangedEventArgs(previousState, newState, reason));
        }
    }

    private void EnsureConnected()
    {
        if (_state != ChannelState.Connected || _sendFunc is null)
        {
            throw new DuplexException(StatusCode.Unavailable, ProblemDetails.NotConnected());
        }
    }

    private ProtoProblemDetails CreateProtoProblemDetails(Exception ex)
    {
        var problem = new ProtoProblemDetails
        {
            Type = "urn:grpc:duplex:error",
            Title = "An error occurred",
            Status = (int)StatusCode.Error,
            Detail = ex.Message,
            Code = ex.GetType().Name
        };

        if (_includeStackTrace && ex.StackTrace is not null)
        {
            problem.Trace = ex.StackTrace;
        }

        if (ex.InnerException is not null)
        {
            problem.Errors.Add(new ProtoProblemDetails
            {
                Type = "urn:grpc:duplex:error",
                Title = "Inner exception",
                Detail = ex.InnerException.Message,
                Code = ex.InnerException.GetType().Name
            });
        }

        return problem;
    }

    private static StatusCode MapStatus(ProtoStatusCode status) => status switch
    {
        ProtoStatusCode.Ok => StatusCode.Ok,
        ProtoStatusCode.Error => StatusCode.Error,
        ProtoStatusCode.NotFound => StatusCode.NotFound,
        ProtoStatusCode.Timeout => StatusCode.Timeout,
        ProtoStatusCode.Cancelled => StatusCode.Cancelled,
        ProtoStatusCode.Unauthorized => StatusCode.Unauthorized,
        ProtoStatusCode.InvalidRequest => StatusCode.InvalidRequest,
        ProtoStatusCode.Unavailable => StatusCode.Unavailable,
        ProtoStatusCode.Internal => StatusCode.Internal,
        _ => StatusCode.Unspecified
    };

    private static ProblemDetails MapProblemDetails(ProtoProblemDetails? proto)
    {
        if (proto is null)
        {
            return new ProblemDetails(Detail: "Unknown error");
        }

        return new ProblemDetails(
            Type: proto.Type,
            Title: proto.Title,
            Status: proto.Status,
            Detail: proto.Detail,
            Instance: proto.Instance,
            Code: proto.Code,
            Trace: proto.Trace,
            Extensions: proto.Extensions.Count > 0 ? new Dictionary<string, string>(proto.Extensions) : null,
            Errors: proto.Errors.Count > 0 ? proto.Errors.Select(MapProblemDetails).ToList() : null);
    }

    private interface IRequestHandler
    {
        ValueTask<Any?> HandleAsync(Any? payload, RequestContext context, CancellationToken cancellationToken);
    }

    private interface INotificationHandler
    {
        ValueTask HandleAsync(Any? payload, NotificationContext context, CancellationToken cancellationToken);
    }

    private sealed class RequestHandler<TRequest, TResponse>(
        Func<TRequest, RequestContext, CancellationToken, ValueTask<TResponse>> handler,
        DuplexChannel channel) : IRequestHandler
    {
        public async ValueTask<Any?> HandleAsync(Any? payload, RequestContext context, CancellationToken cancellationToken)
        {
            var request = channel.UnpackPayload<TRequest>(payload);
            var response = await handler(request, context, cancellationToken);
            return channel.PackPayload(response);
        }
    }

    private sealed class NotificationHandler<T>(
        Func<T, NotificationContext, CancellationToken, ValueTask> handler,
        DuplexChannel channel) : INotificationHandler
    {
        public async ValueTask HandleAsync(Any? payload, NotificationContext context, CancellationToken cancellationToken)
        {
            var notification = channel.UnpackPayload<T>(payload);
            await handler(notification, context, cancellationToken);
        }
    }

    private sealed record PendingRequest(
        string CorrelationId,
        TaskCompletionSource<ProtocolDataUnit> Completion,
        Stopwatch Stopwatch,
        int? TimeoutMs,
        Type ResponseType);

    private sealed class HandlerRegistration(Action unregister) : IDisposable
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
