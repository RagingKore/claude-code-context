using System.Net;
using Grpc.Net.Client.Balancer;
using Microsoft.Extensions.Logging;

namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Custom resolver that discovers cluster topology and reports addresses to the load balancer.
/// </summary>
/// <typeparam name="TNode">The node type.</typeparam>
internal sealed class ClusterResolver<TNode> : Resolver, IAsyncDisposable
    where TNode : struct, IClusterNode {

    readonly DiscoveryEngine<TNode> _discoveryEngine;
    readonly IStreamingTopologySource<TNode> _topologySource;
    readonly TimeSpan _refreshDelay;
    readonly ILogger _logger;

    CancellationTokenSource? _refreshCts;
    Task? _refreshTask;
    ClusterTopology<TNode> _lastTopology;

    /// <summary>
    /// Creates a new cluster resolver.
    /// </summary>
    public ClusterResolver(
        DiscoveryEngine<TNode> discoveryEngine,
        IStreamingTopologySource<TNode> topologySource,
        TimeSpan refreshDelay,
        ILogger logger) {

        _discoveryEngine = discoveryEngine;
        _topologySource = topologySource;
        _refreshDelay = refreshDelay;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override void OnStarted() {
        // Start initial resolution
        _refreshCts = new CancellationTokenSource();
        _refreshTask = RefreshLoopAsync(_refreshCts.Token);
    }

    /// <inheritdoc />
    public override void Refresh() {
        // Trigger immediate refresh
        // Cancel current refresh loop and restart
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        _refreshTask = RefreshLoopAsync(_refreshCts.Token);
    }

    async Task RefreshLoopAsync(CancellationToken ct) {
        var isFirst = true;

        while (!ct.IsCancellationRequested) {
            try {
                var topology = await _discoveryEngine.DiscoverAsync(ct).ConfigureAwait(false);

                // Check if topology changed
                if (isFirst || !TopologyEquals(_lastTopology, topology)) {
                    var added = topology.Count - _lastTopology.Count;
                    var removed = _lastTopology.Count - topology.Count;

                    if (!isFirst) {
                        _logger.TopologyChanged(Math.Max(0, added), Math.Max(0, -added));
                    }

                    _lastTopology = topology;

                    // Report addresses to load balancer
                    var addresses = BuildAddresses(topology);
                    Listener(ResolverResult.ForResult(addresses));
                }

                isFirst = false;

                // Wait for next refresh (streaming sources handle their own timing)
                if (_refreshDelay > TimeSpan.Zero) {
                    _logger.StartingPeriodicRefresh(_refreshDelay.TotalSeconds);
                    await Task.Delay(_refreshDelay, ct).ConfigureAwait(false);
                }
                else {
                    // No delay means we rely on Refresh() being called
                    break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                _logger.StoppingRefresh();
                break;
            }
            catch (Exception ex) {
                // Report error to load balancer
                Listener(ResolverResult.ForFailure(
                    new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, ex.Message, ex)));

                // Wait before retry
                try {
                    await Task.Delay(_refreshDelay > TimeSpan.Zero ? _refreshDelay : TimeSpan.FromSeconds(5), ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    break;
                }
            }
        }
    }

    IReadOnlyList<BalancerAddress> BuildAddresses(ClusterTopology<TNode> topology) {
        var addresses = new List<BalancerAddress>();

        foreach (var node in topology.Nodes) {
            if (!node.IsEligible)
                continue;

            var attributes = new BalancerAttributes {
                { ClusterPicker.PriorityAttributeKey, node.Priority }
            };

            addresses.Add(new BalancerAddress(node.EndPoint.Host, node.EndPoint.Port, attributes));
        }

        // Sort by priority using the topology source's comparer
        addresses.Sort((a, b) => {
            var aPriority = a.Attributes.TryGetValue(ClusterPicker.PriorityAttributeKey, out var ap) ? (int)ap! : int.MaxValue;
            var bPriority = b.Attributes.TryGetValue(ClusterPicker.PriorityAttributeKey, out var bp) ? (int)bp! : int.MaxValue;
            return aPriority.CompareTo(bPriority);
        });

        return addresses;
    }

    static bool TopologyEquals(ClusterTopology<TNode> a, ClusterTopology<TNode> b) {
        if (a.Count != b.Count)
            return false;

        // Simple check - compare endpoints
        var aEndpoints = a.Nodes.Select(n => (n.EndPoint.Host, n.EndPoint.Port, n.IsEligible, n.Priority)).ToHashSet();
        var bEndpoints = b.Nodes.Select(n => (n.EndPoint.Host, n.EndPoint.Port, n.IsEligible, n.Priority)).ToHashSet();

        return aEndpoints.SetEquals(bEndpoints);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing) {
        if (disposing) {
            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        _refreshCts?.Cancel();

        if (_refreshTask is not null) {
            try {
                await _refreshTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
                // Expected
            }
        }

        _refreshCts?.Dispose();
        Dispose(true);
    }
}
