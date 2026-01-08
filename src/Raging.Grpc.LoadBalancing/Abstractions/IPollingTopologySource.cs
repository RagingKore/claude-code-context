namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// Polling topology source. Implement for request/response discovery protocols.
/// The library polls at the configured delay interval.
/// </summary>
/// <typeparam name="TNode">The node type implementing <see cref="IClusterNode"/>.</typeparam>
public interface IPollingTopologySource<TNode> : IComparer<TNode>
    where TNode : struct, IClusterNode {

    /// <summary>
    /// Fetch current cluster topology.
    /// </summary>
    /// <param name="context">The topology context containing channel and cancellation info.</param>
    /// <returns>The current cluster topology.</returns>
    ValueTask<ClusterTopology<TNode>> GetClusterAsync(TopologyContext context);

    /// <summary>
    /// Default comparison: sort by Priority ascending.
    /// Override to customize node selection order.
    /// </summary>
    int IComparer<TNode>.Compare(TNode x, TNode y) => x.Priority.CompareTo(y.Priority);
}
