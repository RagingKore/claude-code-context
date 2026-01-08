namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// No eligible nodes available in the cluster.
/// </summary>
public sealed class NoEligibleNodesException : LoadBalancingException {
    /// <summary>
    /// The total number of nodes in the cluster.
    /// </summary>
    public int TotalNodes { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="NoEligibleNodesException"/> class.
    /// </summary>
    /// <param name="totalNodes">The total number of nodes in the cluster.</param>
    public NoEligibleNodesException(int totalNodes)
        : base($"No eligible nodes available. Cluster has {totalNodes} nodes but none are eligible.") {
        TotalNodes = totalNodes;
    }
}
