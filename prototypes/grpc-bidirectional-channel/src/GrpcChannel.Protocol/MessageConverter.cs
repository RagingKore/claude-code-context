using Google.Protobuf;
using GrpcChannel.Protocol.Messages;
using GrpcChannel.Protocol.Protos;

namespace GrpcChannel.Protocol;

/// <summary>
/// Converts between protobuf messages and domain messages.
/// AOT-compatible message conversion utilities.
/// </summary>
public static class MessageConverter
{
    /// <summary>
    /// Converts a domain envelope to a protobuf channel message.
    /// </summary>
    public static ChannelMessage ToProto(ChannelEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var message = new ChannelMessage
        {
            MessageId = envelope.Id,
            CorrelationId = envelope.CorrelationId ?? string.Empty,
            TimestampUtc = (envelope.TimestampUtc ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds(),
            SenderId = envelope.SenderId ?? string.Empty,
            TargetId = envelope.TargetId ?? string.Empty
        };

        if (envelope.Metadata is not null)
        {
            foreach (var kvp in envelope.Metadata)
            {
                message.Metadata[kvp.Key] = kvp.Value;
            }
        }

        SetPayload(message, envelope.Payload);

        return message;
    }

    /// <summary>
    /// Converts a protobuf channel message to a domain envelope.
    /// </summary>
    public static ChannelEnvelope FromProto(ChannelMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var payload = GetPayload(message);
        var metadata = message.Metadata.Count > 0
            ? new Dictionary<string, string>(message.Metadata)
            : null;

        return new ChannelEnvelope(
            Id: message.MessageId,
            CorrelationId: string.IsNullOrEmpty(message.CorrelationId) ? null : message.CorrelationId,
            Payload: payload,
            Metadata: metadata,
            TimestampUtc: DateTimeOffset.FromUnixTimeMilliseconds(message.TimestampUtc),
            SenderId: string.IsNullOrEmpty(message.SenderId) ? null : message.SenderId,
            TargetId: string.IsNullOrEmpty(message.TargetId) ? null : message.TargetId);
    }

    /// <summary>
    /// Converts a domain command request to a protobuf command request.
    /// </summary>
    public static CommandRequest ToProto(CommandRequestEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var request = new CommandRequest
        {
            RequestId = envelope.RequestId,
            CommandName = envelope.CommandName,
            TimestampUtc = (envelope.TimestampUtc ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds(),
            Priority = envelope.Priority switch
            {
                Messages.CommandPriority.Low => Protos.CommandPriority.Low,
                Messages.CommandPriority.Normal => Protos.CommandPriority.Normal,
                Messages.CommandPriority.High => Protos.CommandPriority.High,
                Messages.CommandPriority.Critical => Protos.CommandPriority.Critical,
                _ => Protos.CommandPriority.Unspecified
            }
        };

        if (envelope.Parameters is not null)
        {
            foreach (var kvp in envelope.Parameters)
            {
                request.Parameters[kvp.Key] = kvp.Value;
            }
        }

        if (envelope.Payload is not null)
        {
            request.Payload = ByteString.CopyFrom(envelope.Payload);
        }

        if (envelope.TimeoutMs.HasValue)
        {
            request.TimeoutMs = envelope.TimeoutMs.Value;
        }

        return request;
    }

    /// <summary>
    /// Converts a protobuf command request to a domain command request.
    /// </summary>
    public static CommandRequestEnvelope FromProto(CommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = request.Parameters.Count > 0
            ? new Dictionary<string, string>(request.Parameters)
            : null;

        return new CommandRequestEnvelope(
            RequestId: request.RequestId,
            CommandName: request.CommandName,
            Parameters: parameters,
            Payload: request.Payload?.ToByteArray(),
            TimestampUtc: DateTimeOffset.FromUnixTimeMilliseconds(request.TimestampUtc),
            TimeoutMs: request.HasTimeoutMs ? request.TimeoutMs : null,
            Priority: request.Priority switch
            {
                Protos.CommandPriority.Low => Messages.CommandPriority.Low,
                Protos.CommandPriority.Normal => Messages.CommandPriority.Normal,
                Protos.CommandPriority.High => Messages.CommandPriority.High,
                Protos.CommandPriority.Critical => Messages.CommandPriority.Critical,
                _ => Messages.CommandPriority.Unspecified
            });
    }

    /// <summary>
    /// Converts a domain command response to a protobuf command response.
    /// </summary>
    public static CommandResponse ToProto(CommandResponseEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var response = new CommandResponse
        {
            RequestId = envelope.RequestId,
            TimestampUtc = (envelope.TimestampUtc ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds(),
            DurationMs = envelope.DurationMs,
            Status = envelope.Status switch
            {
                Messages.CommandStatus.Success => Protos.CommandStatus.Success,
                Messages.CommandStatus.Failed => Protos.CommandStatus.Failed,
                Messages.CommandStatus.Timeout => Protos.CommandStatus.Timeout,
                Messages.CommandStatus.Cancelled => Protos.CommandStatus.Cancelled,
                Messages.CommandStatus.NotFound => Protos.CommandStatus.NotFound,
                Messages.CommandStatus.Unauthorized => Protos.CommandStatus.Unauthorized,
                _ => Protos.CommandStatus.Unspecified
            }
        };

        if (envelope.ErrorMessage is not null)
        {
            response.ErrorMessage = envelope.ErrorMessage;
        }

        if (envelope.ErrorCode.HasValue)
        {
            response.ErrorCode = envelope.ErrorCode.Value;
        }

        if (envelope.Result is not null)
        {
            response.Result = ByteString.CopyFrom(envelope.Result);
        }

        if (envelope.Metadata is not null)
        {
            foreach (var kvp in envelope.Metadata)
            {
                response.Metadata[kvp.Key] = kvp.Value;
            }
        }

        return response;
    }

    /// <summary>
    /// Converts a protobuf command response to a domain command response.
    /// </summary>
    public static CommandResponseEnvelope FromProto(CommandResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var metadata = response.Metadata.Count > 0
            ? new Dictionary<string, string>(response.Metadata)
            : null;

        return new CommandResponseEnvelope(
            RequestId: response.RequestId,
            Status: response.Status switch
            {
                Protos.CommandStatus.Success => Messages.CommandStatus.Success,
                Protos.CommandStatus.Failed => Messages.CommandStatus.Failed,
                Protos.CommandStatus.Timeout => Messages.CommandStatus.Timeout,
                Protos.CommandStatus.Cancelled => Messages.CommandStatus.Cancelled,
                Protos.CommandStatus.NotFound => Messages.CommandStatus.NotFound,
                Protos.CommandStatus.Unauthorized => Messages.CommandStatus.Unauthorized,
                _ => Messages.CommandStatus.Unknown
            },
            ErrorMessage: response.HasErrorMessage ? response.ErrorMessage : null,
            ErrorCode: response.HasErrorCode ? response.ErrorCode : null,
            Result: response.Result?.ToByteArray(),
            Metadata: metadata,
            TimestampUtc: DateTimeOffset.FromUnixTimeMilliseconds(response.TimestampUtc),
            DurationMs: response.DurationMs);
    }

    private static void SetPayload(ChannelMessage message, IMessagePayload payload)
    {
        switch (payload)
        {
            case TextMessagePayload text:
                message.Type = MessageType.Text;
                message.Text = new TextPayload
                {
                    Content = text.Content,
                    Encoding = text.Encoding ?? string.Empty
                };
                break;

            case BinaryMessagePayload binary:
                message.Type = MessageType.Binary;
                message.Binary = new BinaryPayload
                {
                    Data = ByteString.CopyFrom(binary.Data),
                    ContentType = binary.ContentType,
                    Filename = binary.FileName ?? string.Empty
                };
                break;

            case JsonMessagePayload json:
                message.Type = MessageType.Json;
                message.Json = new JsonPayload
                {
                    JsonData = json.JsonData,
                    SchemaUri = json.SchemaUri ?? string.Empty
                };
                break;

            case SystemEventPayload system:
                message.Type = MessageType.System;
                message.System = new SystemPayload
                {
                    EventType = system.EventType switch
                    {
                        Messages.SystemEventType.Connected => Protos.SystemEventType.Connected,
                        Messages.SystemEventType.Disconnected => Protos.SystemEventType.Disconnected,
                        Messages.SystemEventType.Reconnecting => Protos.SystemEventType.Reconnecting,
                        Messages.SystemEventType.Error => Protos.SystemEventType.Error,
                        Messages.SystemEventType.Ping => Protos.SystemEventType.Ping,
                        Messages.SystemEventType.Pong => Protos.SystemEventType.Pong,
                        _ => Protos.SystemEventType.Unspecified
                    },
                    Message = system.Message
                };
                if (system.Details is not null)
                {
                    foreach (var kvp in system.Details)
                    {
                        message.System.Details[kvp.Key] = kvp.Value;
                    }
                }
                break;

            case HeartbeatPayload:
                message.Type = MessageType.Heartbeat;
                break;

            case AckPayload ack:
                message.Type = MessageType.Ack;
                message.Text = new TextPayload { Content = ack.AcknowledgedMessageId };
                break;

            case ErrorPayload error:
                message.Type = MessageType.Error;
                message.System = new SystemPayload
                {
                    EventType = Protos.SystemEventType.Error,
                    Message = error.ErrorMessage
                };
                if (error.Details is not null)
                {
                    foreach (var kvp in error.Details)
                    {
                        message.System.Details[kvp.Key] = kvp.Value;
                    }
                }
                message.System.Details["errorCode"] = error.ErrorCode.ToString();
                break;

            default:
                throw new ArgumentException($"Unknown payload type: {payload.GetType().Name}", nameof(payload));
        }
    }

    private static IMessagePayload GetPayload(ChannelMessage message) =>
        message.Type switch
        {
            MessageType.Text => new TextMessagePayload(
                message.Text.Content,
                string.IsNullOrEmpty(message.Text.Encoding) ? null : message.Text.Encoding),

            MessageType.Binary => new BinaryMessagePayload(
                message.Binary.Data.ToByteArray(),
                message.Binary.ContentType,
                string.IsNullOrEmpty(message.Binary.Filename) ? null : message.Binary.Filename),

            MessageType.Json => new JsonMessagePayload(
                message.Json.JsonData,
                string.IsNullOrEmpty(message.Json.SchemaUri) ? null : message.Json.SchemaUri),

            MessageType.System => new SystemEventPayload(
                message.System.EventType switch
                {
                    Protos.SystemEventType.Connected => Messages.SystemEventType.Connected,
                    Protos.SystemEventType.Disconnected => Messages.SystemEventType.Disconnected,
                    Protos.SystemEventType.Reconnecting => Messages.SystemEventType.Reconnecting,
                    Protos.SystemEventType.Error => Messages.SystemEventType.Error,
                    Protos.SystemEventType.Ping => Messages.SystemEventType.Ping,
                    Protos.SystemEventType.Pong => Messages.SystemEventType.Pong,
                    _ => Messages.SystemEventType.Unknown
                },
                message.System.Message,
                message.System.Details.Count > 0
                    ? new Dictionary<string, string>(message.System.Details)
                    : null),

            MessageType.Heartbeat => HeartbeatPayload.Instance,

            MessageType.Ack => new AckPayload(message.Text.Content),

            MessageType.Error => new ErrorPayload(
                int.TryParse(message.System.Details.GetValueOrDefault("errorCode", "0"), out var code) ? code : 0,
                message.System.Message,
                message.System.Details.Count > 0
                    ? new Dictionary<string, string>(message.System.Details)
                    : null),

            _ => throw new ArgumentException($"Unknown message type: {message.Type}", nameof(message))
        };
}
