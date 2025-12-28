using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf;
using GrpcChannel.Protocol.Contracts;
using GrpcChannel.Protocol.Protos;

namespace GrpcChannel.Protocol;

/// <summary>
/// Core duplex channel implementation providing bidirectional request/response
/// with handler registration. This class is used by both client and server.
/// </summary>
/// <param name="channelId">Unique channel identifier.</param>
/// <param name="serializer">Payload serializer.</param>
public sealed class DuplexChannel(string channelId, IPayloadSerializer serializer) : IDuplexChannel
{
    private readonly ConcurrentDictionary<string, PendingRequest> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, RequestHandler> _requestHandlers = new();
    private readonly ConcurrentDictionary<string, NotificationHandler> _notificationHandlers = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private Func<DuplexMessage, CancellationToken, ValueTask>? _sendFunc;
    private ChannelState _state = ChannelState.Disconnected;
    private string? _remoteId;

    public string ChannelId { get; } = channelId;
    public bool IsConnected => _state == ChannelState.Connected;

    public event EventHandler<ChannelStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Attaches the send function for outgoing messages.
    /// Called by transport layer (gRPC streaming).
    /// </summary>
    public void AttachSender(Func<DuplexMessage, CancellationToken, ValueTask> sendFunc, string? remoteId = null)
    {
        _sendFunc = sendFunc;
        _remoteId = remoteId;
        SetState(ChannelState.Connected);
    }

    /// <summary>
    /// Processes an incoming message from the transport layer.
    /// </summary>
    public async ValueTask ProcessIncomingAsync(DuplexMessage message, CancellationToken cancellationToken = default)
    {
        switch (message.ContentCase)
        {
            case DuplexMessage.ContentOneofCase.Request:
                await HandleIncomingRequestAsync(message, cancellationToken);
                break;

            case DuplexMessage.ContentOneofCase.Response:
                HandleIncomingResponse(message.Response);
                break;

            case DuplexMessage.ContentOneofCase.Notification:
                await HandleIncomingNotificationAsync(message, cancellationToken);
                break;
        }
    }

    public async ValueTask<DuplexResult<TResponse>> SendRequestAsync<TRequest, TResponse>(
        string method,
        TRequest request,
        int? timeoutMs = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
        where TRequest : class
        where TResponse : class
    {
        var payload = serializer.Serialize(request);
        var result = await SendRequestAsync(method, payload, timeoutMs, headers, cancellationToken);

        if (!result.IsSuccess)
        {
            return DuplexResult<TResponse>.Fail(result.Status, result.Error ?? "Unknown error", result.ErrorCode, result.DurationMs);
        }

        var response = serializer.Deserialize<TResponse>(result.Value!);
        return DuplexResult<TResponse>.Ok(response, result.DurationMs, result.Headers);
    }

    public async ValueTask<DuplexResult<byte[]>> SendRequestAsync(
        string method,
        byte[] payload,
        int? timeoutMs = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRequest(correlationId, tcs, Stopwatch.StartNew());

        if (!_pendingRequests.TryAdd(correlationId, pending))
        {
            throw new InvalidOperationException($"Duplicate correlation ID: {correlationId}");
        }

        try
        {
            var message = new DuplexMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Request = new Request
                {
                    CorrelationId = correlationId,
                    Method = method,
                    Payload = ByteString.CopyFrom(payload)
                }
            };

            if (timeoutMs.HasValue)
            {
                message.Request.TimeoutMs = timeoutMs.Value;
            }

            if (headers is not null)
            {
                foreach (var kvp in headers)
                {
                    message.Request.Headers[kvp.Key] = kvp.Value;
                }
            }

            await SendMessageAsync(message, cancellationToken);

            // Wait for response with timeout
            using var cts = timeoutMs.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;

            if (cts is not null)
            {
                cts.CancelAfter(timeoutMs!.Value);
            }

            var effectiveToken = cts?.Token ?? cancellationToken;

            using var registration = effectiveToken.Register(() =>
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

            return response.Status == Protos.StatusCode.Ok
                ? DuplexResult<byte[]>.Ok(response.Payload.ToByteArray(), pending.Stopwatch.ElapsedMilliseconds, responseHeaders)
                : DuplexResult<byte[]>.Fail(
                    MapStatus(response.Status),
                    response.Error ?? "Unknown error",
                    response.HasErrorCode ? response.ErrorCode : null,
                    pending.Stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (timeoutMs.HasValue)
        {
            pending.Stopwatch.Stop();
            _pendingRequests.TryRemove(correlationId, out _);
            return DuplexResult<byte[]>.Fail(StatusCode.Timeout, "Request timed out", durationMs: pending.Stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            pending.Stopwatch.Stop();
            _pendingRequests.TryRemove(correlationId, out _);
            return DuplexResult<byte[]>.Fail(StatusCode.Cancelled, "Request was cancelled", durationMs: pending.Stopwatch.ElapsedMilliseconds);
        }
    }

    public async ValueTask SendNotificationAsync<T>(
        string topic,
        T payload,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        var bytes = serializer.Serialize(payload);
        await SendNotificationAsync(topic, bytes, headers, cancellationToken);
    }

    public async ValueTask SendNotificationAsync(
        string topic,
        byte[] payload,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var message = new DuplexMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Notification = new Notification
            {
                Topic = topic,
                Payload = ByteString.CopyFrom(payload)
            }
        };

        if (headers is not null)
        {
            foreach (var kvp in headers)
            {
                message.Notification.Headers[kvp.Key] = kvp.Value;
            }
        }

        await SendMessageAsync(message, cancellationToken);
    }

    public IDisposable OnRequest<TRequest, TResponse>(
        string method,
        Func<TRequest, RequestContext, CancellationToken, ValueTask<TResponse>> handler)
        where TRequest : class
        where TResponse : class
    {
        var wrapper = new RequestHandler(async (bytes, ctx, ct) =>
        {
            var request = serializer.Deserialize<TRequest>(bytes);
            var response = await handler(request, ctx, ct);
            return serializer.Serialize(response);
        });

        _requestHandlers[method] = wrapper;

        return new HandlerRegistration(() => _requestHandlers.TryRemove(method, out _));
    }

    public IDisposable OnRequest(
        string method,
        Func<byte[], RequestContext, CancellationToken, ValueTask<byte[]>> handler)
    {
        _requestHandlers[method] = new RequestHandler(handler);
        return new HandlerRegistration(() => _requestHandlers.TryRemove(method, out _));
    }

    public IDisposable OnNotification<T>(
        string topic,
        Func<T, NotificationContext, CancellationToken, ValueTask> handler)
        where T : class
    {
        var wrapper = new NotificationHandler(async (bytes, ctx, ct) =>
        {
            var payload = serializer.Deserialize<T>(bytes);
            await handler(payload, ctx, ct);
        });

        _notificationHandlers[topic] = wrapper;

        return new HandlerRegistration(() => _notificationHandlers.TryRemove(topic, out _));
    }

    public IDisposable OnNotification(
        string topic,
        Func<byte[], NotificationContext, CancellationToken, ValueTask> handler)
    {
        _notificationHandlers[topic] = new NotificationHandler(handler);
        return new HandlerRegistration(() => _notificationHandlers.TryRemove(topic, out _));
    }

    public async ValueTask DisposeAsync()
    {
        SetState(ChannelState.Disconnected);

        // Cancel all pending requests
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
    /// Marks the channel as disconnected.
    /// </summary>
    public void Disconnect(string? reason = null)
    {
        SetState(ChannelState.Disconnected, reason);

        // Fail all pending requests
        foreach (var kvp in _pendingRequests)
        {
            kvp.Value.Completion.TrySetException(new DuplexException(StatusCode.Error, reason ?? "Channel disconnected"));
        }
        _pendingRequests.Clear();
    }

    private async ValueTask HandleIncomingRequestAsync(DuplexMessage message, CancellationToken cancellationToken)
    {
        var request = message.Request;
        var stopwatch = Stopwatch.StartNew();

        Response response;

        if (_requestHandlers.TryGetValue(request.Method, out var handler))
        {
            var context = new RequestContext(
                request.CorrelationId,
                request.Method,
                new Dictionary<string, string>(request.Headers),
                new Dictionary<string, string>(message.Metadata),
                _remoteId);

            try
            {
                var responseBytes = await handler.Handler(request.Payload.ToByteArray(), context, cancellationToken);
                stopwatch.Stop();

                response = new Response
                {
                    CorrelationId = request.CorrelationId,
                    Status = Protos.StatusCode.Ok,
                    Payload = ByteString.CopyFrom(responseBytes),
                    DurationMs = stopwatch.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                response = new Response
                {
                    CorrelationId = request.CorrelationId,
                    Status = Protos.StatusCode.Error,
                    Error = ex.Message,
                    DurationMs = stopwatch.ElapsedMilliseconds
                };
            }
        }
        else
        {
            stopwatch.Stop();

            response = new Response
            {
                CorrelationId = request.CorrelationId,
                Status = Protos.StatusCode.NotFound,
                Error = $"Method '{request.Method}' not found",
                DurationMs = stopwatch.ElapsedMilliseconds
            };
        }

        var responseMessage = new DuplexMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Response = response
        };

        await SendMessageAsync(responseMessage, cancellationToken);
    }

    private void HandleIncomingResponse(Response response)
    {
        if (_pendingRequests.TryRemove(response.CorrelationId, out var pending))
        {
            pending.Completion.TrySetResult(response);
        }
    }

    private async ValueTask HandleIncomingNotificationAsync(DuplexMessage message, CancellationToken cancellationToken)
    {
        var notification = message.Notification;

        if (_notificationHandlers.TryGetValue(notification.Topic, out var handler))
        {
            var context = new NotificationContext(
                notification.Topic,
                new Dictionary<string, string>(notification.Headers),
                new Dictionary<string, string>(message.Metadata),
                _remoteId);

            try
            {
                await handler.Handler(notification.Payload.ToByteArray(), context, cancellationToken);
            }
            catch
            {
                // Notifications are fire-and-forget, swallow exceptions
            }
        }
    }

    private async ValueTask SendMessageAsync(DuplexMessage message, CancellationToken cancellationToken)
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
            throw new InvalidOperationException("Channel is not connected");
        }
    }

    private static StatusCode MapStatus(Protos.StatusCode status) => status switch
    {
        Protos.StatusCode.Ok => StatusCode.Ok,
        Protos.StatusCode.Error => StatusCode.Error,
        Protos.StatusCode.NotFound => StatusCode.NotFound,
        Protos.StatusCode.Timeout => StatusCode.Timeout,
        Protos.StatusCode.Cancelled => StatusCode.Cancelled,
        Protos.StatusCode.Unauthorized => StatusCode.Unauthorized,
        Protos.StatusCode.InvalidRequest => StatusCode.InvalidRequest,
        _ => StatusCode.Unspecified
    };

    private sealed record PendingRequest(
        string CorrelationId,
        TaskCompletionSource<Response> Completion,
        Stopwatch Stopwatch);

    private sealed record RequestHandler(
        Func<byte[], RequestContext, CancellationToken, ValueTask<byte[]>> Handler);

    private sealed record NotificationHandler(
        Func<byte[], NotificationContext, CancellationToken, ValueTask> Handler);

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

/// <summary>
/// Default JSON-based payload serializer. AOT-compatible when used with source generators.
/// </summary>
public sealed class JsonPayloadSerializer : IPayloadSerializer
{
    /// <summary>
    /// Default instance with standard options.
    /// </summary>
    public static JsonPayloadSerializer Default { get; } = new();

    private readonly JsonSerializerOptions _options;

    /// <summary>
    /// Creates a new JSON serializer with the specified options.
    /// </summary>
    public JsonPayloadSerializer(JsonSerializerOptions? options = null)
    {
        _options = options ?? new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    public byte[] Serialize<T>(T value) where T : class =>
        JsonSerializer.SerializeToUtf8Bytes(value, _options);

    public T Deserialize<T>(byte[] data) where T : class =>
        JsonSerializer.Deserialize<T>(data, _options)
            ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name}");

    public bool TryDeserialize<T>(byte[] data, out T? value) where T : class
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(data, _options);
            return value is not null;
        }
        catch
        {
            value = default;
            return false;
        }
    }
}
