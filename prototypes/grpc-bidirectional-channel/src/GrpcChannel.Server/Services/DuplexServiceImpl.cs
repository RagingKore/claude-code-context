using System.Collections.Concurrent;
using Grpc.Core;
using GrpcChannel.Protocol;
using GrpcChannel.Protocol.Contracts;
using GrpcChannel.Protocol.Protos;

namespace GrpcChannel.Server.Services;

/// <summary>
/// gRPC service implementation for the duplex channel.
/// Manages connections and provides bidirectional RPC for both sides.
/// Handles both protobuf messages and arbitrary types (via serializer).
/// </summary>
/// <param name="logger">Logger instance.</param>
/// <param name="connectionRegistry">Connection registry for tracking active channels.</param>
/// <param name="hostEnvironment">Host environment for determining debug mode.</param>
/// <param name="serializer">Optional payload serializer for non-protobuf types. Defaults to JSON.</param>
public sealed class DuplexServiceImpl(
    ILogger<DuplexServiceImpl> logger,
    IConnectionRegistry connectionRegistry,
    IHostEnvironment hostEnvironment,
    IPayloadSerializer? serializer = null) : DuplexService.DuplexServiceBase
{
    /// <summary>
    /// Opens a bidirectional duplex channel.
    /// </summary>
    public override async Task Open(
        IAsyncStreamReader<ProtocolDataUnit> requestStream,
        IServerStreamWriter<ProtocolDataUnit> responseStream,
        ServerCallContext context)
    {
        var channelId = Guid.NewGuid().ToString("N");
        var clientId = context.RequestHeaders.GetValue("x-client-id") ?? channelId;

        logger.LogInformation("Client {ClientId} connected, channel {ChannelId}", clientId, channelId);

        var channel = new DuplexChannel(channelId, serializer);

        // Attach the gRPC stream as the sender
        var writeLock = new SemaphoreSlim(1, 1);
        channel.AttachSender(
            async (message, ct) =>
            {
                await writeLock.WaitAsync(ct);
                try
                {
                    await responseStream.WriteAsync(message, ct);
                }
                finally
                {
                    writeLock.Release();
                }
            },
            clientId,
            includeStackTrace: hostEnvironment.IsDevelopment());

        // Register with the connection registry
        connectionRegistry.Register(channelId, channel, clientId);

        try
        {
            // Process incoming messages
            await foreach (var message in requestStream.ReadAllAsync(context.CancellationToken))
            {
                await channel.ProcessIncomingAsync(message, context.CancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Channel {ChannelId} was cancelled", channelId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing channel {ChannelId}", channelId);
        }
        finally
        {
            connectionRegistry.Unregister(channelId);
            channel.Disconnect("Client disconnected");
            await channel.DisposeAsync();
            writeLock.Dispose();

            logger.LogInformation("Client {ClientId} disconnected from channel {ChannelId}", clientId, channelId);
        }
    }
}

/// <summary>
/// Registry for tracking active duplex channels.
/// </summary>
public interface IConnectionRegistry
{
    /// <summary>
    /// Registers a new channel.
    /// </summary>
    void Register(string channelId, IDuplexChannel channel, string? clientId = null);

    /// <summary>
    /// Unregisters a channel.
    /// </summary>
    void Unregister(string channelId);

    /// <summary>
    /// Gets a channel by ID.
    /// </summary>
    IDuplexChannel? GetChannel(string channelId);

    /// <summary>
    /// Gets a channel by client ID.
    /// </summary>
    IDuplexChannel? GetChannelByClientId(string clientId);

    /// <summary>
    /// Gets all active channels.
    /// </summary>
    IReadOnlyCollection<ChannelInfo> GetAllChannels();

    /// <summary>
    /// Registers handlers on all current and future channels.
    /// </summary>
    IDisposable OnAllChannels(Action<IDuplexChannel> configure);
}

/// <summary>
/// Information about an active channel.
/// </summary>
/// <param name="ChannelId">The channel ID.</param>
/// <param name="ClientId">The client ID.</param>
/// <param name="Channel">The channel instance.</param>
/// <param name="ConnectedAt">When the channel was connected.</param>
public sealed record ChannelInfo(
    string ChannelId,
    string? ClientId,
    IDuplexChannel Channel,
    DateTimeOffset ConnectedAt);

/// <summary>
/// Default implementation of connection registry.
/// </summary>
/// <param name="logger">Logger instance.</param>
public sealed class ConnectionRegistry(ILogger<ConnectionRegistry> logger) : IConnectionRegistry
{
    private readonly ConcurrentDictionary<string, ChannelInfo> _channels = new();
    private readonly ConcurrentDictionary<string, string> _clientToChannel = new();
    private readonly List<Action<IDuplexChannel>> _channelConfigurators = [];
    private readonly object _configuratorsLock = new();

    public void Register(string channelId, IDuplexChannel channel, string? clientId = null)
    {
        var info = new ChannelInfo(channelId, clientId, channel, DateTimeOffset.UtcNow);
        _channels[channelId] = info;

        if (clientId is not null)
        {
            _clientToChannel[clientId] = channelId;
        }

        // Apply all registered configurators
        lock (_configuratorsLock)
        {
            foreach (var configure in _channelConfigurators)
            {
                try
                {
                    configure(channel);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error applying channel configurator");
                }
            }
        }

        logger.LogDebug("Registered channel {ChannelId} for client {ClientId}. Total: {Count}",
            channelId, clientId, _channels.Count);
    }

    public void Unregister(string channelId)
    {
        if (_channels.TryRemove(channelId, out var info) && info.ClientId is not null)
        {
            _clientToChannel.TryRemove(info.ClientId, out _);
        }

        logger.LogDebug("Unregistered channel {ChannelId}. Total: {Count}", channelId, _channels.Count);
    }

    public IDuplexChannel? GetChannel(string channelId) =>
        _channels.TryGetValue(channelId, out var info) ? info.Channel : null;

    public IDuplexChannel? GetChannelByClientId(string clientId) =>
        _clientToChannel.TryGetValue(clientId, out var channelId) ? GetChannel(channelId) : null;

    public IReadOnlyCollection<ChannelInfo> GetAllChannels() =>
        _channels.Values.ToList().AsReadOnly();

    public IDisposable OnAllChannels(Action<IDuplexChannel> configure)
    {
        lock (_configuratorsLock)
        {
            _channelConfigurators.Add(configure);
        }

        // Apply to existing channels
        foreach (var info in _channels.Values)
        {
            try
            {
                configure(info.Channel);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error applying channel configurator to existing channel");
            }
        }

        return new ConfiguratorRegistration(() =>
        {
            lock (_configuratorsLock)
            {
                _channelConfigurators.Remove(configure);
            }
        });
    }

    private sealed class ConfiguratorRegistration(Action unregister) : IDisposable
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
