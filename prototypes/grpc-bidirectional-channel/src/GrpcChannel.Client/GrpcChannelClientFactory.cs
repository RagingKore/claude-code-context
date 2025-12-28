using GrpcChannel.Protocol.Contracts;
using Microsoft.Extensions.Logging;

namespace GrpcChannel.Client;

/// <summary>
/// Factory for creating gRPC channel connections.
/// Uses primary constructor for dependency injection.
/// </summary>
/// <param name="loggerFactory">Optional logger factory.</param>
public sealed class GrpcChannelClientFactory(ILoggerFactory? loggerFactory = null) : IChannelClientFactory
{
    public async ValueTask<IChannelConnection> CreateConnectionAsync(
        ChannelConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var logger = loggerFactory?.CreateLogger<GrpcChannelConnection>();
        var connection = new GrpcChannelConnection(options, logger);

        await connection.ConnectAsync(cancellationToken);

        return connection;
    }

    public async ValueTask<ICommandChannel> CreateCommandChannelAsync(
        ChannelConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var logger = loggerFactory?.CreateLogger<GrpcCommandChannel>();
        var channel = new GrpcCommandChannel(options, logger);

        await channel.OpenAsync(cancellationToken);

        return channel;
    }
}

/// <summary>
/// Extension methods for channel operations.
/// </summary>
public static class ChannelExtensions
{
    /// <summary>
    /// Sends a command and waits for the response.
    /// </summary>
    public static async ValueTask<CommandResponseEnvelope> ExecuteCommandAsync(
        this ICommandChannel channel,
        string commandName,
        IReadOnlyDictionary<string, string>? parameters = null,
        byte[]? payload = null,
        int? timeoutMs = null,
        CommandPriority priority = CommandPriority.Normal,
        CancellationToken cancellationToken = default)
    {
        var request = CommandRequestEnvelope.Create(commandName, parameters, payload, timeoutMs, priority);
        return await channel.SendRequestAsync(request, cancellationToken);
    }

    /// <summary>
    /// Sends a ping command and measures round-trip time.
    /// </summary>
    public static async ValueTask<(bool Success, long RoundTripMs)> PingAsync(
        this ICommandChannel channel,
        CancellationToken cancellationToken = default)
    {
        var start = DateTimeOffset.UtcNow;
        var response = await channel.ExecuteCommandAsync("ping", cancellationToken: cancellationToken);
        var elapsed = (DateTimeOffset.UtcNow - start).TotalMilliseconds;

        return (response.IsSuccess, (long)elapsed);
    }

    /// <summary>
    /// Sends an echo command.
    /// </summary>
    public static async ValueTask<string?> EchoAsync(
        this ICommandChannel channel,
        string message,
        CancellationToken cancellationToken = default)
    {
        var response = await channel.ExecuteCommandAsync(
            "echo",
            new Dictionary<string, string> { ["message"] = message },
            cancellationToken: cancellationToken);

        if (response.IsSuccess && response.Result is not null)
        {
            return System.Text.Encoding.UTF8.GetString(response.Result);
        }

        return null;
    }
}
