namespace GrpcChannel.Protocol.Contracts;

/// <summary>
/// Contract for channel client factory.
/// </summary>
public interface IChannelClientFactory
{
    /// <summary>
    /// Creates a new channel connection.
    /// </summary>
    /// <param name="options">Connection options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A connected channel connection.</returns>
    ValueTask<IChannelConnection> CreateConnectionAsync(
        ChannelConnectionOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new command channel.
    /// </summary>
    /// <param name="options">Connection options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An active command channel.</returns>
    ValueTask<ICommandChannel> CreateCommandChannelAsync(
        ChannelConnectionOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Options for channel connections.
/// </summary>
/// <param name="ServerAddress">The server address (e.g., "https://localhost:5001").</param>
/// <param name="ClientId">Optional client identifier.</param>
/// <param name="Metadata">Optional connection metadata.</param>
/// <param name="HeartbeatIntervalMs">Heartbeat interval in milliseconds (default: 30000).</param>
/// <param name="ReconnectDelayMs">Initial reconnect delay in milliseconds (default: 1000).</param>
/// <param name="MaxReconnectAttempts">Maximum reconnection attempts (default: 5).</param>
/// <param name="EnableAutoReconnect">Enable automatic reconnection (default: true).</param>
public sealed record ChannelConnectionOptions(
    string ServerAddress,
    string? ClientId = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    int HeartbeatIntervalMs = 30000,
    int ReconnectDelayMs = 1000,
    int MaxReconnectAttempts = 5,
    bool EnableAutoReconnect = true)
{
    /// <summary>
    /// Creates options with default values for local development.
    /// </summary>
    public static ChannelConnectionOptions ForLocalDevelopment(int port = 5001) =>
        new($"https://localhost:{port}");
}
