using System.Collections.Immutable;

namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// A snapshot of the cluster topology.
/// </summary>
/// <typeparam name="TNode">The node type implementing <see cref="IClusterNode"/>.</typeparam>
public readonly record struct ClusterTopology<TNode>(
    ImmutableArray<TNode> Nodes
) where TNode : struct, IClusterNode {

    readonly int _cachedHashCode;
    readonly int _eligibleCount;

    public ClusterTopology(ImmutableArray<TNode> nodes) : this() {
        Nodes = nodes;
        _cachedHashCode = ComputeHashCode(nodes);
        _eligibleCount = CountEligible(nodes);
    }

    public static ClusterTopology<TNode> Empty => new(ImmutableArray<TNode>.Empty);
    public bool IsEmpty => Nodes.IsDefaultOrEmpty;
    public int Count => Nodes.IsDefaultOrEmpty ? 0 : Nodes.Length;
    public int EligibleCount => _eligibleCount;

    public bool Equals(ClusterTopology<TNode> other) {
        if (Nodes.Equals(other.Nodes)) return true;
        if (Count != other.Count) return false;
        if (IsEmpty) return true;

        if (_cachedHashCode != 0 && other._cachedHashCode != 0 && _cachedHashCode != other._cachedHashCode)
            return false;

        var otherSet = new HashSet<TNode>(other.Nodes);
        foreach (var n in Nodes)
            if (!otherSet.Contains(n)) return false;
        return true;
    }

    public override int GetHashCode() => _cachedHashCode;

    static int ComputeHashCode(ImmutableArray<TNode> nodes) {
        if (nodes.IsDefaultOrEmpty) return 0;
        int hashSum = 0;
        foreach (var n in nodes)
            unchecked { hashSum += n.GetHashCode(); }
        return HashCode.Combine(nodes.Length, hashSum);
    }

    static int CountEligible(ImmutableArray<TNode> nodes) {
        if (nodes.IsDefaultOrEmpty) return 0;
        int count = 0;
        foreach (var n in nodes)
            if (n.IsEligible) count++;
        return count;
    }

    public (int Added, int Removed) ComputeDiff(ClusterTopology<TNode> other) {
        if (Nodes.Equals(other.Nodes)) return (0, 0);

        var thisSet = new HashSet<TNode>(Nodes);
        var otherSet = new HashSet<TNode>(other.Nodes);

        int added = 0, removed = 0;
        foreach (var n in other.Nodes) if (!thisSet.Contains(n)) added++;
        foreach (var n in Nodes) if (!otherSet.Contains(n)) removed++;

        return (added, removed);
    }
}
