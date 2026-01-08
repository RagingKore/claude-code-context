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

    static readonly Status NoEligibleNodesStatus =
        new(StatusCode.Unavailable, "No eligible nodes available in cluster.");

    readonly Subchannel[] _subchannels;
    readonly int _topTierCount;
    int _roundRobinIndex;

    /// <summary>
    /// Creates a new picker with the specified subchannels.
    /// Subchannels should already be sorted by priority.
    /// </summary>
    /// <param name="subchannels">The ready subchannels, sorted by priority.</param>
    public ClusterPicker(IReadOnlyList<Subchannel> subchannels) {
        if (subchannels.Count == 0) {
            _subchannels = [];
            _topTierCount = 0;
            return;
        }

        // Copy to array (subchannels are already sorted by load balancer)
        _subchannels = [.. subchannels];
        _topTierCount = CountTopTier(_subchannels);
    }

    /// <summary>
    /// Pick a subchannel for the request.
    /// This method is called on every gRPC call - ZERO ALLOCATIONS.
    /// </summary>
    public override PickResult Pick(PickContext context) {
        // HOT PATH - NO ALLOCATIONS

        if (_subchannels.Length == 0)
            return PickResult.ForFailure(NoEligibleNodesStatus);

        // Atomic increment and round-robin within top tier
        var index = Interlocked.Increment(ref _roundRobinIndex);

        // Handle potential overflow and ensure positive index within top tier
        var selectedIndex = ((index % _topTierCount) + _topTierCount) % _topTierCount;

        return PickResult.ForSubchannel(_subchannels[selectedIndex]);
    }

    /// <summary>
    /// Count how many subchannels are in the top priority tier.
    /// </summary>
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

    /// <summary>
    /// Get the priority from a subchannel's attributes.
    /// </summary>
    static int GetPriority(Subchannel subchannel) {
        if (subchannel.Attributes.TryGetValue(PriorityAttributeKey, out var value) && value is int priority)
            return priority;

        return int.MaxValue;
    }
}
