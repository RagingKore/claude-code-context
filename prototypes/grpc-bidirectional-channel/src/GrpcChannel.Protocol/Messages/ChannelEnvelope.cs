using System.Text.Json.Serialization;

namespace GrpcChannel.Protocol.Messages;

/// <summary>
/// Represents an envelope for channel messages with metadata.
/// </summary>
/// <param name="Id">Unique message identifier.</param>
/// <param name="CorrelationId">Correlation ID for request/response tracking.</param>
/// <param name="Payload">The message payload.</param>
/// <param name="Metadata">Optional metadata dictionary.</param>
/// <param name="TimestampUtc">UTC timestamp when the message was created.</param>
/// <param name="SenderId">Optional sender identifier.</param>
/// <param name="TargetId">Optional target identifier.</param>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed record ChannelEnvelope(
    string Id,
    string? CorrelationId,
    IMessagePayload Payload,
    IReadOnlyDictionary<string, string>? Metadata = null,
    DateTimeOffset? TimestampUtc = null,
    string? SenderId = null,
    string? TargetId = null)
{
    /// <summary>
    /// Creates a new envelope with auto-generated ID and current timestamp.
    /// </summary>
    public static ChannelEnvelope Create(
        IMessagePayload payload,
        string? correlationId = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? senderId = null,
        string? targetId = null) =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            CorrelationId: correlationId,
            Payload: payload,
            Metadata: metadata,
            TimestampUtc: DateTimeOffset.UtcNow,
            SenderId: senderId,
            TargetId: targetId);

    /// <summary>
    /// Creates a reply envelope correlated to this message.
    /// </summary>
    public ChannelEnvelope CreateReply(IMessagePayload payload, IReadOnlyDictionary<string, string>? metadata = null) =>
        Create(
            payload: payload,
            correlationId: Id,
            metadata: metadata,
            senderId: TargetId,
            targetId: SenderId);
}

/// <summary>
/// Marker interface for message payloads.
/// </summary>
public interface IMessagePayload;

/// <summary>
/// Text message payload.
/// </summary>
/// <param name="Content">The text content.</param>
/// <param name="Encoding">Optional encoding specification.</param>
public sealed record TextMessagePayload(string Content, string? Encoding = null) : IMessagePayload;

/// <summary>
/// Binary message payload.
/// </summary>
/// <param name="Data">The binary data.</param>
/// <param name="ContentType">MIME content type.</param>
/// <param name="FileName">Optional file name.</param>
public sealed record BinaryMessagePayload(byte[] Data, string ContentType, string? FileName = null) : IMessagePayload;

/// <summary>
/// JSON message payload.
/// </summary>
/// <param name="JsonData">The JSON string data.</param>
/// <param name="SchemaUri">Optional JSON schema URI.</param>
public sealed record JsonMessagePayload(string JsonData, string? SchemaUri = null) : IMessagePayload;

/// <summary>
/// System event payload.
/// </summary>
/// <param name="EventType">The type of system event.</param>
/// <param name="Message">Event message.</param>
/// <param name="Details">Additional details.</param>
public sealed record SystemEventPayload(
    SystemEventType EventType,
    string Message,
    IReadOnlyDictionary<string, string>? Details = null) : IMessagePayload;

/// <summary>
/// System event types.
/// </summary>
public enum SystemEventType
{
    Unknown = 0,
    Connected,
    Disconnected,
    Reconnecting,
    Error,
    Ping,
    Pong
}

/// <summary>
/// Heartbeat payload for keep-alive.
/// </summary>
public sealed record HeartbeatPayload() : IMessagePayload
{
    public static HeartbeatPayload Instance { get; } = new();
}

/// <summary>
/// Acknowledgment payload.
/// </summary>
/// <param name="AcknowledgedMessageId">The ID of the acknowledged message.</param>
public sealed record AckPayload(string AcknowledgedMessageId) : IMessagePayload;

/// <summary>
/// Error payload.
/// </summary>
/// <param name="ErrorCode">Error code.</param>
/// <param name="ErrorMessage">Error message.</param>
/// <param name="Details">Additional error details.</param>
public sealed record ErrorPayload(int ErrorCode, string ErrorMessage, IReadOnlyDictionary<string, string>? Details = null) : IMessagePayload;
