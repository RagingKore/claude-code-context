using System.Collections.Immutable;
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
    readonly ImmutableArray<DnsEndPoint> _seeds;
    readonly SeedChannelPool _channelPool;
    readonly ResilienceOptions _resilience;
    readonly ILogger _logger;

    CancellationTokenSource? _cts;
    Task? _subscriptionTask;
    ClusterTopology<TNode> _lastTopology;

    public ClusterResolver(
        IStreamingTopologySource<TNode> topologySource,
        ImmutableArray<DnsEndPoint> seeds,
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
        var seedIndex = 0;

        while (!ct.IsCancellationRequested) {
            var seed = _seeds[seedIndex];
            seedIndex = (seedIndex + 1) % _seeds.Count;

            try {
                await SubscribeToSeedAsync(seed, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            }
            catch (Exception ex) {
                _logger.TopologyCallFailed(seed, ex);
            }
        }
    }

    async Task SubscribeToSeedAsync(DnsEndPoint seed, CancellationToken ct) {
        _logger.DiscoveringCluster(seed);

        var channel = _channelPool.GetChannel(seed);
        var context = new TopologyContext {
            Channel = channel,
            CancellationToken = ct,
            Timeout = _resilience.Timeout,
            Endpoint = seed
        };

        var receivedAny = false;

        await foreach (var topology in _topologySource.SubscribeAsync(context, ct).ConfigureAwait(false)) {
            receivedAny = true;

            if (_lastTopology != topology) {
                ValidateTopology(topology);

                if (_lastTopology != default) {
                    var (added, removed) = _lastTopology.ComputeDiff(topology);
                    _logger.TopologyChanged(added, removed);
                }

                _lastTopology = topology;

                _logger.DiscoveredNodes(topology.Count, topology.EligibleCount);
                Listener(ResolverResult.ForResult(BuildAddresses(topology)));
            }
        }

        if (!receivedAny)
            _logger.TopologyStreamEmpty(seed);
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
        // Filter eligible nodes
        var eligible = new List<TNode>();
        foreach (var node in topology.Nodes)
            if (node.IsEligible)
                eligible.Add(node);

        // Sort using topology source comparer - THIS IS THE PICKING ALGORITHM
        eligible.Sort(_topologySource);

        // Convert to addresses with order index from comparer
        var addresses = new List<BalancerAddress>(eligible.Count);
        for (var i = 0; i < eligible.Count; i++) {
            var node = eligible[i];
            var attributes = new BalancerAttributes {
                { ClusterPicker.OrderIndexKey, i }
            };
            addresses.Add(new BalancerAddress(node.EndPoint.Host, node.EndPoint.Port, attributes));
        }
        return addresses;
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
