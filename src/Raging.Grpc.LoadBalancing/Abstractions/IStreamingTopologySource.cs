namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// Streaming topology source. Implement if server pushes topology changes.
/// </summary>
/// <typeparam name="TNode">The node type implementing <see cref="IClusterNode"/>.</typeparam>
public interface IStreamingTopologySource<TNode> : IComparer<TNode>
    where TNode : struct, IClusterNode {

    /// <summary>
    /// Subscribe to cluster topology changes.
    /// Each yielded value is the complete current topology (snapshot model).
    /// </summary>
    /// <param name="context">The topology context containing channel and cancellation info.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of topology snapshots.</returns>
    IAsyncEnumerable<ClusterTopology<TNode>> SubscribeAsync(TopologyContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Default comparison: sort by Priority ascending.
    /// Override to customize node selection order.
    /// </summary>
    int IComparer<TNode>.Compare(TNode x, TNode y) => x.Priority.CompareTo(y.Priority);
}
