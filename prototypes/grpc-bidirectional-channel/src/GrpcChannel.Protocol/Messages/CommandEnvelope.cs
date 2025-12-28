namespace GrpcChannel.Protocol.Messages;

/// <summary>
/// Represents a command request envelope.
/// </summary>
/// <param name="RequestId">Unique request identifier.</param>
/// <param name="CommandName">Name of the command to execute.</param>
/// <param name="Parameters">Command parameters.</param>
/// <param name="Payload">Optional binary payload.</param>
/// <param name="TimestampUtc">UTC timestamp when the request was created.</param>
/// <param name="TimeoutMs">Optional timeout in milliseconds.</param>
/// <param name="Priority">Command execution priority.</param>
public sealed record CommandRequestEnvelope(
    string RequestId,
    string CommandName,
    IReadOnlyDictionary<string, string>? Parameters = null,
    byte[]? Payload = null,
    DateTimeOffset? TimestampUtc = null,
    int? TimeoutMs = null,
    CommandPriority Priority = CommandPriority.Normal)
{
    /// <summary>
    /// Creates a new command request with auto-generated ID and current timestamp.
    /// </summary>
    public static CommandRequestEnvelope Create(
        string commandName,
        IReadOnlyDictionary<string, string>? parameters = null,
        byte[]? payload = null,
        int? timeoutMs = null,
        CommandPriority priority = CommandPriority.Normal) =>
        new(
            RequestId: Guid.NewGuid().ToString("N"),
            CommandName: commandName,
            Parameters: parameters,
            Payload: payload,
            TimestampUtc: DateTimeOffset.UtcNow,
            TimeoutMs: timeoutMs,
            Priority: priority);
}

/// <summary>
/// Represents a command response envelope.
/// </summary>
/// <param name="RequestId">The original request identifier.</param>
/// <param name="Status">Command execution status.</param>
/// <param name="ErrorMessage">Optional error message if status is not Success.</param>
/// <param name="ErrorCode">Optional error code.</param>
/// <param name="Result">Optional result payload.</param>
/// <param name="Metadata">Response metadata.</param>
/// <param name="TimestampUtc">UTC timestamp when the response was created.</param>
/// <param name="DurationMs">Execution duration in milliseconds.</param>
public sealed record CommandResponseEnvelope(
    string RequestId,
    CommandStatus Status,
    string? ErrorMessage = null,
    int? ErrorCode = null,
    byte[]? Result = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    DateTimeOffset? TimestampUtc = null,
    long DurationMs = 0)
{
    /// <summary>
    /// Creates a successful response.
    /// </summary>
    public static CommandResponseEnvelope Success(
        string requestId,
        byte[]? result = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        long durationMs = 0) =>
        new(
            RequestId: requestId,
            Status: CommandStatus.Success,
            Result: result,
            Metadata: metadata,
            TimestampUtc: DateTimeOffset.UtcNow,
            DurationMs: durationMs);

    /// <summary>
    /// Creates a failed response.
    /// </summary>
    public static CommandResponseEnvelope Failed(
        string requestId,
        string errorMessage,
        int? errorCode = null,
        long durationMs = 0) =>
        new(
            RequestId: requestId,
            Status: CommandStatus.Failed,
            ErrorMessage: errorMessage,
            ErrorCode: errorCode,
            TimestampUtc: DateTimeOffset.UtcNow,
            DurationMs: durationMs);

    /// <summary>
    /// Creates a timeout response.
    /// </summary>
    public static CommandResponseEnvelope Timeout(string requestId, long durationMs = 0) =>
        new(
            RequestId: requestId,
            Status: CommandStatus.Timeout,
            ErrorMessage: "Command execution timed out",
            TimestampUtc: DateTimeOffset.UtcNow,
            DurationMs: durationMs);

    /// <summary>
    /// Creates a cancelled response.
    /// </summary>
    public static CommandResponseEnvelope Cancelled(string requestId, long durationMs = 0) =>
        new(
            RequestId: requestId,
            Status: CommandStatus.Cancelled,
            ErrorMessage: "Command execution was cancelled",
            TimestampUtc: DateTimeOffset.UtcNow,
            DurationMs: durationMs);

    /// <summary>
    /// Creates a not found response.
    /// </summary>
    public static CommandResponseEnvelope NotFound(string requestId, string commandName) =>
        new(
            RequestId: requestId,
            Status: CommandStatus.NotFound,
            ErrorMessage: $"Command '{commandName}' was not found",
            TimestampUtc: DateTimeOffset.UtcNow);

    /// <summary>
    /// Creates an unauthorized response.
    /// </summary>
    public static CommandResponseEnvelope Unauthorized(string requestId) =>
        new(
            RequestId: requestId,
            Status: CommandStatus.Unauthorized,
            ErrorMessage: "Unauthorized to execute this command",
            TimestampUtc: DateTimeOffset.UtcNow);

    /// <summary>
    /// Indicates whether the command was successful.
    /// </summary>
    public bool IsSuccess => Status == CommandStatus.Success;
}

/// <summary>
/// Command execution status.
/// </summary>
public enum CommandStatus
{
    Unknown = 0,
    Success,
    Failed,
    Timeout,
    Cancelled,
    NotFound,
    Unauthorized
}

/// <summary>
/// Command execution priority.
/// </summary>
public enum CommandPriority
{
    Unspecified = 0,
    Low,
    Normal,
    High,
    Critical
}
