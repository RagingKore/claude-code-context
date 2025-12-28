namespace GrpcChannel.Protocol.Contracts;

/// <summary>
/// Represents a bidirectional duplex channel where both sides can send
/// requests and receive correlated responses.
/// </summary>
public interface IDuplexChannel : IAsyncDisposable
{
    /// <summary>
    /// Unique channel identifier.
    /// </summary>
    string ChannelId { get; }

    /// <summary>
    /// Indicates whether the channel is connected and active.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Sends a request and awaits the correlated response.
    /// </summary>
    /// <typeparam name="TRequest">Request payload type.</typeparam>
    /// <typeparam name="TResponse">Response payload type.</typeparam>
    /// <param name="method">The method/handler name to invoke on the remote side.</param>
    /// <param name="request">The request payload.</param>
    /// <param name="timeoutMs">Optional timeout in milliseconds.</param>
    /// <param name="headers">Optional request headers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response result.</returns>
    ValueTask<DuplexResult<TResponse>> SendRequestAsync<TRequest, TResponse>(
        string method,
        TRequest request,
        int? timeoutMs = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
        where TRequest : class
        where TResponse : class;

    /// <summary>
    /// Sends a request with raw bytes and awaits the correlated response.
    /// </summary>
    ValueTask<DuplexResult<byte[]>> SendRequestAsync(
        string method,
        byte[] payload,
        int? timeoutMs = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a fire-and-forget notification (no response expected).
    /// </summary>
    ValueTask SendNotificationAsync<T>(
        string topic,
        T payload,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
        where T : class;

    /// <summary>
    /// Sends a fire-and-forget notification with raw bytes.
    /// </summary>
    ValueTask SendNotificationAsync(
        string topic,
        byte[] payload,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a request handler for a specific method.
    /// </summary>
    /// <typeparam name="TRequest">Request payload type.</typeparam>
    /// <typeparam name="TResponse">Response payload type.</typeparam>
    /// <param name="method">The method name to handle.</param>
    /// <param name="handler">The handler function.</param>
    /// <returns>A disposable that unregisters the handler when disposed.</returns>
    IDisposable OnRequest<TRequest, TResponse>(
        string method,
        Func<TRequest, RequestContext, CancellationToken, ValueTask<TResponse>> handler)
        where TRequest : class
        where TResponse : class;

    /// <summary>
    /// Registers a request handler with raw bytes.
    /// </summary>
    IDisposable OnRequest(
        string method,
        Func<byte[], RequestContext, CancellationToken, ValueTask<byte[]>> handler);

    /// <summary>
    /// Registers a notification handler for a specific topic.
    /// </summary>
    /// <typeparam name="T">Notification payload type.</typeparam>
    /// <param name="topic">The topic to subscribe to.</param>
    /// <param name="handler">The handler function.</param>
    /// <returns>A disposable that unregisters the handler when disposed.</returns>
    IDisposable OnNotification<T>(
        string topic,
        Func<T, NotificationContext, CancellationToken, ValueTask> handler)
        where T : class;

    /// <summary>
    /// Registers a notification handler with raw bytes.
    /// </summary>
    IDisposable OnNotification(
        string topic,
        Func<byte[], NotificationContext, CancellationToken, ValueTask> handler);

    /// <summary>
    /// Event raised when the connection state changes.
    /// </summary>
    event EventHandler<ChannelStateChangedEventArgs>? StateChanged;
}

/// <summary>
/// Result of a duplex request.
/// </summary>
/// <typeparam name="T">Response payload type.</typeparam>
public readonly record struct DuplexResult<T>(
    bool IsSuccess,
    T? Value,
    StatusCode Status,
    string? Error = null,
    int? ErrorCode = null,
    long DurationMs = 0,
    IReadOnlyDictionary<string, string>? Headers = null)
{
    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static DuplexResult<T> Ok(T value, long durationMs = 0, IReadOnlyDictionary<string, string>? headers = null) =>
        new(true, value, StatusCode.Ok, null, null, durationMs, headers);

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static DuplexResult<T> Fail(StatusCode status, string error, int? errorCode = null, long durationMs = 0) =>
        new(false, default, status, error, errorCode, durationMs, null);

    /// <summary>
    /// Gets the value or throws if not successful.
    /// </summary>
    public T GetValueOrThrow() =>
        IsSuccess ? Value! : throw new DuplexException(Status, Error ?? "Unknown error", ErrorCode);
}

/// <summary>
/// Status codes for duplex operations.
/// </summary>
public enum StatusCode
{
    Unspecified = 0,
    Ok = 1,
    Error = 2,
    NotFound = 3,
    Timeout = 4,
    Cancelled = 5,
    Unauthorized = 6,
    InvalidRequest = 7
}

/// <summary>
/// Context for request handling.
/// </summary>
/// <param name="CorrelationId">The correlation ID of the request.</param>
/// <param name="Method">The method being invoked.</param>
/// <param name="Headers">Request headers.</param>
/// <param name="Metadata">Channel metadata.</param>
/// <param name="RemoteId">Remote endpoint identifier.</param>
public sealed record RequestContext(
    string CorrelationId,
    string Method,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> Metadata,
    string? RemoteId = null);

/// <summary>
/// Context for notification handling.
/// </summary>
/// <param name="Topic">The notification topic.</param>
/// <param name="Headers">Notification headers.</param>
/// <param name="Metadata">Channel metadata.</param>
/// <param name="RemoteId">Remote endpoint identifier.</param>
public sealed record NotificationContext(
    string Topic,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> Metadata,
    string? RemoteId = null);

/// <summary>
/// Event arguments for channel state changes.
/// </summary>
/// <param name="PreviousState">Previous channel state.</param>
/// <param name="CurrentState">Current channel state.</param>
/// <param name="Reason">Optional reason for the state change.</param>
public sealed record ChannelStateChangedEventArgs(
    ChannelState PreviousState,
    ChannelState CurrentState,
    string? Reason = null);

/// <summary>
/// Channel connection state.
/// </summary>
public enum ChannelState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Faulted
}

/// <summary>
/// Exception thrown when a duplex operation fails.
/// </summary>
/// <param name="status">The status code.</param>
/// <param name="message">The error message.</param>
/// <param name="errorCode">Optional error code.</param>
public sealed class DuplexException(StatusCode status, string message, int? errorCode = null)
    : Exception(message)
{
    /// <summary>
    /// The status code of the failure.
    /// </summary>
    public StatusCode Status { get; } = status;

    /// <summary>
    /// Optional application-specific error code.
    /// </summary>
    public int? ErrorCode { get; } = errorCode;
}
