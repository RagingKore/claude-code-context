namespace GrpcChannel.Protocol.Contracts;

/// <summary>
/// Represents a bidirectional duplex channel where both sides can send
/// requests and receive correlated responses.
/// Supports both protobuf messages (IMessage) and arbitrary types (via serializer).
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
    /// <typeparam name="TRequest">Request payload type (protobuf IMessage or any serializable type).</typeparam>
    /// <typeparam name="TResponse">Response payload type (protobuf IMessage or any serializable type).</typeparam>
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
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a fire-and-forget notification (no response expected).
    /// </summary>
    ValueTask SendNotificationAsync<T>(
        string topic,
        T payload,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a request handler for a specific method.
    /// </summary>
    /// <typeparam name="TRequest">Request payload type (protobuf IMessage or any serializable type).</typeparam>
    /// <typeparam name="TResponse">Response payload type (protobuf IMessage or any serializable type).</typeparam>
    /// <param name="method">The method name to handle.</param>
    /// <param name="handler">The handler function.</param>
    /// <returns>A disposable that unregisters the handler when disposed.</returns>
    IDisposable OnRequest<TRequest, TResponse>(
        string method,
        Func<TRequest, RequestContext, CancellationToken, ValueTask<TResponse>> handler);

    /// <summary>
    /// Registers a notification handler for a specific topic.
    /// </summary>
    /// <typeparam name="T">Notification payload type (protobuf IMessage or any serializable type).</typeparam>
    /// <param name="topic">The topic to subscribe to.</param>
    /// <param name="handler">The handler function.</param>
    /// <returns>A disposable that unregisters the handler when disposed.</returns>
    IDisposable OnNotification<T>(
        string topic,
        Func<T, NotificationContext, CancellationToken, ValueTask> handler);

    /// <summary>
    /// Event raised when the connection state changes.
    /// </summary>
    event EventHandler<ChannelStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Event raised when a channel-level error occurs.
    /// This includes transport errors, serialization errors, and other non-request-specific failures.
    /// </summary>
    event EventHandler<ChannelErrorEventArgs>? Error;
}

/// <summary>
/// Result of a duplex request.
/// </summary>
/// <typeparam name="T">Response payload type.</typeparam>
public readonly record struct DuplexResult<T>(
    bool IsSuccess,
    T? Value,
    StatusCode Status,
    ProblemDetails? Problem = null,
    long DurationMs = 0,
    IReadOnlyDictionary<string, string>? Headers = null)
{
    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static DuplexResult<T> Ok(T value, long durationMs = 0, IReadOnlyDictionary<string, string>? headers = null) =>
        new(true, value, StatusCode.Ok, null, durationMs, headers);

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static DuplexResult<T> Fail(StatusCode status, ProblemDetails problem, long durationMs = 0) =>
        new(false, default, status, problem, durationMs, null);

    /// <summary>
    /// Creates a failed result with a simple error message.
    /// </summary>
    public static DuplexResult<T> Fail(StatusCode status, string error, string? code = null, long durationMs = 0) =>
        new(false, default, status, ProblemDetails.FromError(status, error, code), durationMs, null);

    /// <summary>
    /// Gets the value or throws if not successful.
    /// </summary>
    public T GetValueOrThrow() =>
        IsSuccess ? Value! : throw new DuplexException(Status, Problem ?? ProblemDetails.FromError(Status, "Unknown error"));
}

/// <summary>
/// Problem details for error responses (RFC 7807 compatible).
/// </summary>
/// <param name="Type">URI reference identifying the problem type.</param>
/// <param name="Title">Short, human-readable summary.</param>
/// <param name="Status">HTTP-style status code.</param>
/// <param name="Detail">Human-readable explanation.</param>
/// <param name="Instance">URI identifying this occurrence.</param>
/// <param name="Code">Application-specific error code.</param>
/// <param name="Trace">Stack trace (debug only).</param>
/// <param name="Extensions">Additional properties.</param>
/// <param name="Errors">Nested errors for error chains.</param>
public sealed record ProblemDetails(
    string? Type = null,
    string? Title = null,
    int? Status = null,
    string? Detail = null,
    string? Instance = null,
    string? Code = null,
    string? Trace = null,
    IReadOnlyDictionary<string, string>? Extensions = null,
    IReadOnlyList<ProblemDetails>? Errors = null)
{
    /// <summary>
    /// Creates a problem details from a status and error message.
    /// </summary>
    public static ProblemDetails FromError(StatusCode status, string detail, string? code = null) =>
        new(
            Type: $"urn:grpc:duplex:{status.ToString().ToLowerInvariant()}",
            Title: status.ToTitle(),
            Status: (int)status,
            Detail: detail,
            Code: code);

    /// <summary>
    /// Creates a problem details from an exception.
    /// </summary>
    public static ProblemDetails FromException(Exception ex, bool includeTrace = false) =>
        new(
            Type: $"urn:grpc:duplex:error",
            Title: "An error occurred",
            Status: (int)StatusCode.Error,
            Detail: ex.Message,
            Code: ex.GetType().Name,
            Trace: includeTrace ? ex.StackTrace : null,
            Errors: ex.InnerException is not null
                ? [FromException(ex.InnerException, includeTrace)]
                : null);

    /// <summary>
    /// Creates a not found problem details.
    /// </summary>
    public static ProblemDetails NotFound(string method) =>
        new(
            Type: "urn:grpc:duplex:not-found",
            Title: "Method Not Found",
            Status: (int)StatusCode.NotFound,
            Detail: $"Method '{method}' was not found",
            Code: "METHOD_NOT_FOUND");

    /// <summary>
    /// Creates a timeout problem details.
    /// </summary>
    public static ProblemDetails Timeout(int timeoutMs) =>
        new(
            Type: "urn:grpc:duplex:timeout",
            Title: "Request Timeout",
            Status: (int)StatusCode.Timeout,
            Detail: $"Request timed out after {timeoutMs}ms",
            Code: "TIMEOUT");

    /// <summary>
    /// Creates a cancelled problem details.
    /// </summary>
    public static ProblemDetails Cancelled() =>
        new(
            Type: "urn:grpc:duplex:cancelled",
            Title: "Request Cancelled",
            Status: (int)StatusCode.Cancelled,
            Detail: "The request was cancelled",
            Code: "CANCELLED");

    /// <summary>
    /// Creates a problem details for connection loss.
    /// </summary>
    public static ProblemDetails ConnectionLost(string? reason = null) =>
        new(
            Type: "urn:grpc:duplex:connection-lost",
            Title: "Connection Lost",
            Status: (int)StatusCode.Unavailable,
            Detail: reason ?? "The connection to the remote endpoint was lost",
            Code: "CONNECTION_LOST");

    /// <summary>
    /// Creates a problem details for transport/gRPC errors.
    /// </summary>
    public static ProblemDetails TransportError(string grpcStatus, string? detail = null) =>
        new(
            Type: "urn:grpc:duplex:transport-error",
            Title: "Transport Error",
            Status: (int)StatusCode.Unavailable,
            Detail: detail ?? $"Transport error: {grpcStatus}",
            Code: $"GRPC_{grpcStatus.ToUpperInvariant()}");

    /// <summary>
    /// Creates a problem details for serialization errors.
    /// </summary>
    public static ProblemDetails SerializationError(string message, bool isSerialize = true) =>
        new(
            Type: "urn:grpc:duplex:serialization-error",
            Title: isSerialize ? "Serialization Error" : "Deserialization Error",
            Status: (int)StatusCode.InvalidRequest,
            Detail: message,
            Code: isSerialize ? "SERIALIZATION_FAILED" : "DESERIALIZATION_FAILED");

    /// <summary>
    /// Creates a problem details for invalid/malformed messages.
    /// </summary>
    public static ProblemDetails InvalidMessage(string reason) =>
        new(
            Type: "urn:grpc:duplex:invalid-message",
            Title: "Invalid Message",
            Status: (int)StatusCode.InvalidRequest,
            Detail: reason,
            Code: "INVALID_MESSAGE");

    /// <summary>
    /// Creates a problem details for channel not connected errors.
    /// </summary>
    public static ProblemDetails NotConnected() =>
        new(
            Type: "urn:grpc:duplex:not-connected",
            Title: "Not Connected",
            Status: (int)StatusCode.Unavailable,
            Detail: "The channel is not connected",
            Code: "NOT_CONNECTED");
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
    InvalidRequest = 7,
    Unavailable = 8,
    Internal = 9
}

/// <summary>
/// Extension methods for StatusCode.
/// </summary>
public static class StatusCodeExtensions
{
    /// <summary>
    /// Gets a human-readable title for the status code.
    /// </summary>
    public static string ToTitle(this StatusCode status) => status switch
    {
        StatusCode.Ok => "OK",
        StatusCode.Error => "Error",
        StatusCode.NotFound => "Not Found",
        StatusCode.Timeout => "Timeout",
        StatusCode.Cancelled => "Cancelled",
        StatusCode.Unauthorized => "Unauthorized",
        StatusCode.InvalidRequest => "Invalid Request",
        StatusCode.Unavailable => "Service Unavailable",
        StatusCode.Internal => "Internal Error",
        _ => "Unknown"
    };
}

/// <summary>
/// Context for request handling.
/// </summary>
/// <param name="CorrelationId">The correlation ID of the request.</param>
/// <param name="Method">The method being invoked.</param>
/// <param name="Headers">Request headers.</param>
/// <param name="RemoteId">Remote endpoint identifier.</param>
public sealed record RequestContext(
    string CorrelationId,
    string Method,
    IReadOnlyDictionary<string, string> Headers,
    string? RemoteId = null);

/// <summary>
/// Context for notification handling.
/// </summary>
/// <param name="Topic">The notification topic.</param>
/// <param name="Headers">Notification headers.</param>
/// <param name="RemoteId">Remote endpoint identifier.</param>
public sealed record NotificationContext(
    string Topic,
    IReadOnlyDictionary<string, string> Headers,
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
/// Event arguments for channel errors.
/// </summary>
/// <param name="Problem">The problem details describing the error.</param>
/// <param name="Exception">The original exception, if any.</param>
/// <param name="IsFatal">Whether this error is fatal and the channel should be closed.</param>
public sealed record ChannelErrorEventArgs(
    ProblemDetails Problem,
    Exception? Exception = null,
    bool IsFatal = false);

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
/// <param name="problem">The problem details.</param>
public sealed class DuplexException(StatusCode status, ProblemDetails problem)
    : Exception(problem.Detail ?? problem.Title ?? "Unknown error")
{
    /// <summary>
    /// The status code of the failure.
    /// </summary>
    public StatusCode Status { get; } = status;

    /// <summary>
    /// The problem details.
    /// </summary>
    public ProblemDetails Problem { get; } = problem;
}
