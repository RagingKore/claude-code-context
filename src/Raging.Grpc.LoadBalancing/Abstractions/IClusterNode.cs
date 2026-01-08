using System.Net;

namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// Represents a node in the cluster.
/// </summary>
public interface IClusterNode {
    /// <summary>
    /// The endpoint to connect to.
    /// </summary>
    DnsEndPoint EndPoint { get; }

    /// <summary>
    /// Whether this node can accept connections.
    /// </summary>
    bool IsEligible { get; }

    /// <summary>
    /// Selection priority. Lower values are preferred.
    /// Nodes with equal priority are load-balanced via round-robin.
    /// </summary>
    int Priority { get; }
}
