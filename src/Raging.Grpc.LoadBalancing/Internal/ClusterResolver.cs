using System.Net;
using Grpc.Net.Client.Balancer;
using Microsoft.Extensions.Logging;

namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Custom resolver that discovers cluster topology and reports addresses to the load balancer.
/// </summary>
internal sealed class ClusterResolver<TNode> : Resolver, IAsyncDisposable
    where TNode : struct, IClusterNode {

    readonly IStreamingTopologySource<TNode> _topologySource;
    readonly IReadOnlyList<DnsEndPoint> _seeds;
    readonly SeedChannelPool _channelPool;
    readonly ResilienceOptions _resilience;
    readonly ILogger _logger;

    CancellationTokenSource? _cts;
    Task? _subscriptionTask;
    ClusterTopology<TNode> _lastTopology;
    bool _isFirst = true;

    public ClusterResolver(
        IStreamingTopologySource<TNode> topologySource,
        IReadOnlyList<DnsEndPoint> seeds,
        SeedChannelPool channelPool,
        ResilienceOptions resilience,
        ILogger logger) {

        _topologySource = topologySource;
        _seeds = seeds;
        _channelPool = channelPool;
        _resilience = resilience;
        _logger = logger;
    }

    protected override void OnStarted() {
        _cts = new CancellationTokenSource();
        _subscriptionTask = SubscribeLoopAsync(_cts.Token);
    }

    public override void Refresh() {
        // Cancel current subscription and restart
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _subscriptionTask = SubscribeLoopAsync(_cts.Token);
    }

    async Task SubscribeLoopAsync(CancellationToken ct) {
        var attempt = 0;

        while (!ct.IsCancellationRequested) {
            attempt++;

            // Try parallel fan-out to all seeds, consume from first success
            var success = await TrySubscribeToAnySeedAsync(ct).ConfigureAwait(false);

            if (success) {
                attempt = 0; // Reset on success
            }
            else {
                // All seeds failed - backoff and retry
                if (attempt < _resilience.MaxDiscoveryAttempts) {
                    var backoff = BackoffCalculator.Calculate(attempt, _resilience.InitialBackoff, _resilience.MaxBackoff);
                    _logger.AllSeedsFailed(attempt, _resilience.MaxDiscoveryAttempts, (int)backoff.TotalMilliseconds);

                    try {
                        await Task.Delay(backoff, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                        return;
                    }
                }
                else {
                    // Max attempts reached, report failure
                    Listener(ResolverResult.ForFailure(
                        new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "All seeds failed")));

                    // Wait before next round
                    try {
                        await Task.Delay(_resilience.MaxBackoff, ct).ConfigureAwait(false);
                        attempt = 0;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                        return;
                    }
                }
            }
        }
    }

    async Task<bool> TrySubscribeToAnySeedAsync(CancellationToken ct) {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Start parallel subscriptions to all seeds
        var tasks = _seeds
            .Select(seed => TrySubscribeToSeedAsync(seed, cts.Token))
            .ToList();

        while (tasks.Count > 0) {
            var completedTask = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(completedTask);

            try {
                var success = await completedTask.ConfigureAwait(false);
                if (success) {
                    // One seed succeeded (consumed stream until it ended or failed)
                    await cts.CancelAsync().ConfigureAwait(false);
                    return true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            }
            catch {
                // This seed failed, continue with others
            }
        }

        return false;
    }

    async Task<bool> TrySubscribeToSeedAsync(DnsEndPoint seed, CancellationToken ct) {
        _logger.DiscoveringCluster(seed);

        var channel = _channelPool.GetChannel(seed);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_resilience.Timeout);

        var context = new TopologyContext {
            Channel = channel,
            CancellationToken = timeoutCts.Token,
            Timeout = _resilience.Timeout,
            Endpoint = seed
        };

        try {
            var receivedAny = false;

            // Consume ALL topology updates from this seed
            await foreach (var topology in _topologySource.SubscribeAsync(context, ct).ConfigureAwait(false)) {
                receivedAny = true;

                // Reset timeout on each successful receive
                timeoutCts.CancelAfter(_resilience.Timeout);

                if (_isFirst || !TopologyEquals(_lastTopology, topology)) {
                    ValidateTopology(topology);

                    if (!_isFirst) {
                        var (added, removed) = ComputeTopologyDiff(_lastTopology, topology);
                        _logger.TopologyChanged(added, removed);
                    }

                    _lastTopology = topology;
                    _isFirst = false;

                    _logger.DiscoveredNodes(topology.Count, topology.EligibleCount);
                    Listener(ResolverResult.ForResult(BuildAddresses(topology)));
                }
            }

            return receivedAny;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        }
        catch (Exception ex) {
            _logger.TopologyCallFailed(seed, ex);
            throw;
        }
    }

    void ValidateTopology(ClusterTopology<TNode> topology) {
        if (topology.IsEmpty)
            throw new ClusterDiscoveryException(1, _seeds, [new InvalidOperationException("Topology returned empty node list.")]);

        if (topology.EligibleCount == 0) {
            _logger.NoEligibleNodes(topology.Count);
            throw new NoEligibleNodesException(topology.Count);
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

        return (
            currentEndpoints.Count(e => !oldEndpoints.Contains(e)),
            oldEndpoints.Count(e => !currentEndpoints.Contains(e))
        );
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }

    public async ValueTask DisposeAsync() {
        _cts?.Cancel();

        if (_subscriptionTask is not null) {
            try {
                await _subscriptionTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
                // Expected
            }
        }

        _cts?.Dispose();
        Dispose(true);
    }
}
