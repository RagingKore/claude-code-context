namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// A snapshot of the cluster topology.
/// </summary>
/// <typeparam name="TNode">The node type implementing <see cref="IClusterNode"/>.</typeparam>
public readonly record struct ClusterTopology<TNode>(
    IReadOnlyList<TNode> Nodes
) where TNode : struct, IClusterNode {

    /// <summary>
    /// An empty topology with no nodes.
    /// </summary>
    public static ClusterTopology<TNode> Empty => new([]);

    /// <summary>
    /// Whether this topology has no nodes.
    /// </summary>
    public bool IsEmpty => Nodes.Count == 0;

    /// <summary>
    /// The number of nodes in this topology.
    /// </summary>
    public int Count => Nodes.Count;

    /// <summary>
    /// Gets the number of eligible nodes.
    /// </summary>
    public int EligibleCount => Nodes.Count(n => n.IsEligible);

    /// <summary>
    /// Computes the difference between this topology and another.
    /// </summary>
    /// <param name="other">The other topology to compare against.</param>
    /// <returns>The number of nodes added and removed.</returns>
    public (int Added, int Removed) ComputeDiff(ClusterTopology<TNode> other) {
        var thisEndpoints = Nodes.Select(n => (n.EndPoint.Host, n.EndPoint.Port)).ToHashSet();
        var otherEndpoints = other.Nodes.Select(n => (n.EndPoint.Host, n.EndPoint.Port)).ToHashSet();

        return (
            otherEndpoints.Count(e => !thisEndpoints.Contains(e)),
            thisEndpoints.Count(e => !otherEndpoints.Contains(e))
        );
    }

    /// <summary>
    /// Value equality based on node endpoints, eligibility, and priority.
    /// </summary>
    public bool Equals(ClusterTopology<TNode> other) {
        if (Nodes is null && other.Nodes is null)
            return true;

        if (Nodes is null || other.Nodes is null)
            return false;

        if (Count != other.Count)
            return false;

        var thisSet = Nodes.Select(n => (n.EndPoint.Host, n.EndPoint.Port, n.IsEligible, n.Priority)).ToHashSet();
        var otherSet = other.Nodes.Select(n => (n.EndPoint.Host, n.EndPoint.Port, n.IsEligible, n.Priority)).ToHashSet();

        return thisSet.SetEquals(otherSet);
    }

    /// <summary>
    /// Hash code consistent with value equality.
    /// </summary>
    public override int GetHashCode() {
        if (Nodes is null)
            return 0;

        var hash = new HashCode();
        hash.Add(Count);

        // Use sorted order for deterministic hash
        foreach (var node in Nodes.OrderBy(n => n.EndPoint.Host).ThenBy(n => n.EndPoint.Port)) {
            hash.Add(node.EndPoint.Host);
            hash.Add(node.EndPoint.Port);
            hash.Add(node.IsEligible);
            hash.Add(node.Priority);
        }

        return hash.ToHashCode();
    }
}
