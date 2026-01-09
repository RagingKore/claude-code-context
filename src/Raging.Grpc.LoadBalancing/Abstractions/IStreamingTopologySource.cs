namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// Streaming topology source. Implement if server pushes topology changes.
/// </summary>
public interface IStreamingTopologySource : IComparer<ClusterNode> {
    /// <summary>
    /// Subscribe to cluster topology changes.
    /// Each yielded value is the complete current topology (snapshot model).
    /// </summary>
    /// <param name="context">The topology context containing channel and cancellation info.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of topology snapshots.</returns>
    IAsyncEnumerable<ClusterTopology> SubscribeAsync(TopologyContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Default comparison: sort by Priority ascending.
    /// Override to customize node selection order.
    /// </summary>
    int IComparer<ClusterNode>.Compare(ClusterNode x, ClusterNode y) => x.Priority.CompareTo(y.Priority);
}
