using GrpcChannel.Protocol.Messages;

namespace GrpcChannel.Protocol.Contracts;

/// <summary>
/// Represents a bidirectional channel connection.
/// </summary>
public interface IChannelConnection : IAsyncDisposable
{
    /// <summary>
    /// Unique connection identifier.
    /// </summary>
    string ConnectionId { get; }

    /// <summary>
    /// Indicates whether the connection is active.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Gets an async enumerable of incoming messages.
    /// </summary>
    IAsyncEnumerable<ChannelEnvelope> IncomingMessages { get; }

    /// <summary>
    /// Sends a message through the channel.
    /// </summary>
    /// <param name="envelope">The message envelope to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask SendAsync(ChannelEnvelope envelope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a text message through the channel.
    /// </summary>
    ValueTask SendTextAsync(string content, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a JSON message through the channel.
    /// </summary>
    ValueTask SendJsonAsync(string jsonData, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a binary message through the channel.
    /// </summary>
    ValueTask SendBinaryAsync(byte[] data, string contentType, string? fileName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a heartbeat message.
    /// </summary>
    ValueTask SendHeartbeatAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects the channel.
    /// </summary>
    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Event raised when the connection state changes.
    /// </summary>
    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>
    /// Event raised when a message is received.
    /// </summary>
    event EventHandler<MessageReceivedEventArgs>? MessageReceived;
}

/// <summary>
/// Event arguments for connection state changes.
/// </summary>
/// <param name="PreviousState">The previous connection state.</param>
/// <param name="CurrentState">The current connection state.</param>
/// <param name="Reason">Optional reason for the state change.</param>
public sealed record ConnectionStateChangedEventArgs(
    ConnectionState PreviousState,
    ConnectionState CurrentState,
    string? Reason = null);

/// <summary>
/// Event arguments for message received events.
/// </summary>
/// <param name="Envelope">The received message envelope.</param>
public sealed record MessageReceivedEventArgs(ChannelEnvelope Envelope);

/// <summary>
/// Connection state enumeration.
/// </summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Failed
}
