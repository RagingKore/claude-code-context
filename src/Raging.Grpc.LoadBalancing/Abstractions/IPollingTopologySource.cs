namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// Polling topology source. Implement for request/response discovery protocols.
/// The library polls at the configured delay interval.
/// </summary>
public interface IPollingTopologySource : IComparer<ClusterNode> {
    /// <summary>
    /// Fetch current cluster topology.
    /// </summary>
    /// <param name="context">The topology context containing channel and cancellation info.</param>
    /// <returns>The current cluster topology.</returns>
    ValueTask<ClusterTopology> GetClusterAsync(TopologyContext context);

    /// <summary>
    /// Default comparison: sort by Priority ascending.
    /// Override to customize node selection order.
    /// </summary>
    int IComparer<ClusterNode>.Compare(ClusterNode x, ClusterNode y) => x.Priority.CompareTo(y.Priority);
}
