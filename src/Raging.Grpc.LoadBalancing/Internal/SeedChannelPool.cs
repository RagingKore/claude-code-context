using System.Collections.Concurrent;
using System.Net;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;

namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Manages a pool of channels to seed endpoints for topology discovery.
/// </summary>
internal sealed class SeedChannelPool : IAsyncDisposable {
    readonly ConcurrentDictionary<DnsEndPoint, GrpcChannel> _channels = new();
    readonly GrpcChannelOptions? _channelOptions;
    readonly ILogger _logger;
    readonly bool _useTls;
    bool _disposed;

    /// <summary>
    /// Creates a new seed channel pool.
    /// </summary>
    public SeedChannelPool(GrpcChannelOptions? channelOptions, bool useTls, ILogger logger) {
        _channelOptions = channelOptions;
        _useTls = useTls;
        _logger = logger;
    }

    /// <summary>
    /// Gets or creates a channel to the specified endpoint.
    /// </summary>
    public GrpcChannel GetChannel(DnsEndPoint endpoint) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _channels.GetOrAdd(endpoint, CreateChannel);
    }

    GrpcChannel CreateChannel(DnsEndPoint endpoint) {
        _logger.CreatingSeedChannel(endpoint);

        var scheme = _useTls ? "https" : "http";
        var address = $"{scheme}://{endpoint.Host}:{endpoint.Port}";

        return _channelOptions is not null
            ? GrpcChannel.ForAddress(address, _channelOptions)
            : GrpcChannel.ForAddress(address);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (_disposed)
            return;

        _disposed = true;
        _logger.DisposingSeedChannelPool(_channels.Count);

        var disposeTasks = _channels.Values.Select(async channel => {
            try {
                await channel.ShutdownAsync().ConfigureAwait(false);
                channel.Dispose();
            }
            catch {
                // Ignore disposal errors
            }
        });

        await Task.WhenAll(disposeTasks).ConfigureAwait(false);
        _channels.Clear();
    }
}
