using Grpc.Core;
using Grpc.Net.Client.Balancer;

namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Zero-allocation subchannel picker that implements round-robin within the top priority tier.
/// </summary>
internal sealed class ClusterPicker : SubchannelPicker {
    /// <summary>
    /// Attribute key for storing node priority on subchannels.
    /// </summary>
    public const string PriorityAttributeKey = "cluster-node-priority";

    static readonly Status NoReadyNodesStatus =
        new(StatusCode.Unavailable, "No ready nodes available in cluster.");

    readonly Subchannel[] _readySubchannels;
    readonly int _topTierCount;
    int _roundRobinIndex;

    /// <summary>
    /// Creates a new picker with the specified subchannels.
    /// Filters to Ready subchannels and sorts by priority.
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
            _topTierCount = 0;
            return;
        }

        // Sort by priority
        ready.Sort((a, b) => GetPriority(a).CompareTo(GetPriority(b)));

        _readySubchannels = [.. ready];
        _topTierCount = CountTopTier(_readySubchannels);
    }

    /// <summary>
    /// Pick a subchannel for the request.
    /// This method is called on every gRPC call - ZERO ALLOCATIONS.
    /// </summary>
    public override PickResult Pick(PickContext context) {
        // HOT PATH - NO ALLOCATIONS

        if (_readySubchannels.Length == 0)
            return PickResult.ForFailure(NoReadyNodesStatus);

        // Atomic increment and round-robin within top tier
        var index = Interlocked.Increment(ref _roundRobinIndex);

        // Handle potential overflow and ensure positive index within top tier
        var selectedIndex = ((index % _topTierCount) + _topTierCount) % _topTierCount;

        return PickResult.ForSubchannel(_readySubchannels[selectedIndex]);
    }

    static int CountTopTier(Subchannel[] sorted) {
        if (sorted.Length == 0)
            return 0;

        var topPriority = GetPriority(sorted[0]);
        var count = 1;

        for (var i = 1; i < sorted.Length; i++) {
            if (GetPriority(sorted[i]) != topPriority)
                break;
            count++;
        }

        return count;
    }

    static int GetPriority(Subchannel subchannel) {
        if (subchannel.Attributes.TryGetValue(PriorityAttributeKey, out var value) && value is int priority)
            return priority;

        return int.MaxValue;
    }
}
