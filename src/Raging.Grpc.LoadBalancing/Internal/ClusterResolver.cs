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
        TimeSpan refreshDelay,
        ILogger logger) {

        _discoveryEngine = discoveryEngine;
        _refreshDelay = refreshDelay;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override void OnStarted() {
        _refreshCts = new CancellationTokenSource();
        _refreshTask = RefreshLoopAsync(_refreshCts.Token);
    }

    /// <inheritdoc />
    public override void Refresh() {
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

                if (isFirst || !TopologyEquals(_lastTopology, topology)) {
                    if (!isFirst) {
                        var (added, removed) = ComputeTopologyDiff(_lastTopology, topology);
                        _logger.TopologyChanged(added, removed);
                    }

                    _lastTopology = topology;
                    Listener(ResolverResult.ForResult(BuildAddresses(topology)));
                }

                isFirst = false;

                // Always wait for refresh delay (polling interval)
                // For "on-demand" refresh, call Refresh() method
                if (_refreshDelay > TimeSpan.Zero) {
                    _logger.StartingPeriodicRefresh(_refreshDelay.TotalSeconds);
                    await Task.Delay(_refreshDelay, ct).ConfigureAwait(false);
                }
                else {
                    // Zero delay = one-shot mode, wait for Refresh() call
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                _logger.StoppingRefresh();
                return;
            }
            catch (Exception ex) {
                Listener(ResolverResult.ForFailure(
                    new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, ex.Message, ex)));

                try {
                    var retryDelay = _refreshDelay > TimeSpan.Zero ? _refreshDelay : TimeSpan.FromSeconds(5);
                    await Task.Delay(retryDelay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    return;
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

        var aEndpoints = a.Nodes.Select(n => (n.EndPoint.Host, n.EndPoint.Port, n.IsEligible, n.Priority)).ToHashSet();
        var bEndpoints = b.Nodes.Select(n => (n.EndPoint.Host, n.EndPoint.Port, n.IsEligible, n.Priority)).ToHashSet();

        return aEndpoints.SetEquals(bEndpoints);
    }

    static (int Added, int Removed) ComputeTopologyDiff(ClusterTopology<TNode> old, ClusterTopology<TNode> current) {
        var oldEndpoints = old.Nodes.Select(n => (n.EndPoint.Host, n.EndPoint.Port)).ToHashSet();
        var currentEndpoints = current.Nodes.Select(n => (n.EndPoint.Host, n.EndPoint.Port)).ToHashSet();

        var added = currentEndpoints.Count(e => !oldEndpoints.Contains(e));
        var removed = oldEndpoints.Count(e => !currentEndpoints.Contains(e));

        return (added, removed);
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
