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
}
