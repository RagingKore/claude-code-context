using Grpc.Core;
using Grpc.Net.Client.Balancer;

namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Zero-allocation subchannel picker that implements round-robin within the top priority tier.
/// </summary>
internal sealed class ClusterPicker : SubchannelPicker {
    /// <summary>
    /// Attribute key for storing order index (from topology source comparer).
    /// </summary>
    public const string OrderIndexKey = "cluster-node-order";

    static readonly Status NoReadyNodesStatus =
        new(StatusCode.Unavailable, "No ready nodes available in cluster.");

    readonly Subchannel[] _readySubchannels;
    int _roundRobinIndex;

    /// <summary>
    /// Creates a new picker with the specified subchannels.
    /// Filters to Ready subchannels and sorts by order index (from topology source comparer).
    /// </summary>
    /// <param name="subchannels">All subchannels from the load balancer.</param>
    public ClusterPicker(IReadOnlyList<Subchannel> subchannels) {
        // Filter to Ready subchannels
        var ready = new List<Subchannel>();
        foreach (var s in subchannels)
            if (s.State == ConnectivityState.Ready)
                ready.Add(s);

        if (ready.Count == 0) {
            _readySubchannels = [];
            return;
        }

        // Sort by order index (assigned by Resolver using topology source comparer)
        ready.Sort((a, b) => GetOrderIndex(a).CompareTo(GetOrderIndex(b)));

        _readySubchannels = [.. ready];
    }

    /// <summary>
    /// Pick a subchannel for the request.
    /// This method is called on every gRPC call - ZERO ALLOCATIONS.
    /// </summary>
    public override PickResult Pick(PickContext context) {
        // HOT PATH - NO ALLOCATIONS

        if (_readySubchannels.Length == 0)
            return PickResult.ForFailure(NoReadyNodesStatus);

        // Round-robin across all ready subchannels (ordered by topology source comparer)
        var index = Interlocked.Increment(ref _roundRobinIndex);
        var count = _readySubchannels.Length;
        var selectedIndex = ((index % count) + count) % count;

        return PickResult.ForSubchannel(_readySubchannels[selectedIndex]);
    }

    static int GetOrderIndex(Subchannel subchannel) {
        if (subchannel.Attributes.TryGetValue(OrderIndexKey, out var value) && value is int order)
            return order;

        return int.MaxValue;
    }
}
