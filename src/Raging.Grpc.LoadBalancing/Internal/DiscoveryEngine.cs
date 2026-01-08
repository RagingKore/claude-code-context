using System.Net;
using Microsoft.Extensions.Logging;

namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Handles cluster topology discovery with retry and backoff.
/// </summary>
/// <typeparam name="TNode">The node type.</typeparam>
internal sealed class DiscoveryEngine<TNode> where TNode : struct, IClusterNode {
    readonly IStreamingTopologySource<TNode> _topologySource;
    readonly SeedChannelPool _channelPool;
    readonly IReadOnlyList<DnsEndPoint> _seeds;
    readonly ResilienceOptions _resilience;
    readonly ILogger _logger;

    /// <summary>
    /// Creates a new discovery engine.
    /// </summary>
    public DiscoveryEngine(
        IStreamingTopologySource<TNode> topologySource,
        SeedChannelPool channelPool,
        IReadOnlyList<DnsEndPoint> seeds,
        ResilienceOptions resilience,
        ILogger logger) {

        _topologySource = topologySource;
        _channelPool = channelPool;
        _seeds = seeds;
        _resilience = resilience;
        _logger = logger;
    }

    /// <summary>
    /// Discover the cluster topology.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The discovered topology.</returns>
    /// <exception cref="ClusterDiscoveryException">When discovery fails after all attempts.</exception>
    /// <exception cref="NoEligibleNodesException">When no eligible nodes are found.</exception>
    public async ValueTask<ClusterTopology<TNode>> DiscoverAsync(CancellationToken ct) {
        var exceptions = new List<Exception>();
        var attempt = 0;

        while (attempt < _resilience.MaxDiscoveryAttempts) {
            attempt++;
            ct.ThrowIfCancellationRequested();

            // Try to discover from any seed using parallel fan-out
            var result = await TryDiscoverFromSeedsAsync(ct, exceptions).ConfigureAwait(false);

            if (result is not null) {
                // Validate the topology
                if (result.Value.IsEmpty) {
                    throw new ClusterDiscoveryException(
                        attempt,
                        _seeds,
                        [new InvalidOperationException("Topology returned empty node list.")]);
                }

                if (result.Value.EligibleCount == 0) {
                    _logger.NoEligibleNodes(result.Value.Count);
                    throw new NoEligibleNodesException(result.Value.Count);
                }

                _logger.DiscoveredNodes(result.Value.Count, result.Value.EligibleCount);
                return result.Value;
            }

            // All seeds failed, backoff before retry
            if (attempt < _resilience.MaxDiscoveryAttempts) {
                var backoff = BackoffCalculator.Calculate(
                    attempt,
                    _resilience.InitialBackoff,
                    _resilience.MaxBackoff);

                _logger.AllSeedsFailed(attempt, _resilience.MaxDiscoveryAttempts, (int)backoff.TotalMilliseconds);

                try {
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    throw;
                }
            }
        }

        _logger.DiscoveryFailed(attempt, new AggregateException(exceptions));
        throw new ClusterDiscoveryException(attempt, _seeds, exceptions);
    }

    /// <summary>
    /// Try to discover from seeds using parallel fan-out.
    /// </summary>
    async Task<ClusterTopology<TNode>?> TryDiscoverFromSeedsAsync(
        CancellationToken ct,
        List<Exception> exceptions) {

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Create discovery tasks for all seeds
        var tasks = _seeds
            .Select(seed => TryDiscoverFromEndpointAsync(seed, cts.Token))
            .ToList();

        // Wait for any to complete successfully
        while (tasks.Count > 0) {
            var completedTask = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(completedTask);

            try {
                var result = await completedTask.ConfigureAwait(false);

                if (result is not null) {
                    // Cancel remaining tasks
                    await cts.CancelAsync().ConfigureAwait(false);
                    return result;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            }
            catch (Exception ex) {
                exceptions.Add(ex);
            }
        }

        return null;
    }

    /// <summary>
    /// Try to discover from a single endpoint.
    /// </summary>
    async Task<ClusterTopology<TNode>?> TryDiscoverFromEndpointAsync(
        DnsEndPoint endpoint,
        CancellationToken ct) {

        _logger.DiscoveringCluster(endpoint);

        try {
            var channel = _channelPool.GetChannel(endpoint);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_resilience.Timeout);

            var context = new TopologyContext {
                Channel = channel,
                CancellationToken = timeoutCts.Token,
                Timeout = _resilience.Timeout,
                Endpoint = endpoint
            };

            // Get the first topology snapshot from the streaming source
            await foreach (var topology in _topologySource.SubscribeAsync(context).ConfigureAwait(false)) {
                return topology;
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        }
        catch (Exception ex) {
            _logger.TopologyCallFailed(endpoint, ex);
            throw new TopologyException(endpoint, ex);
        }
    }
}
