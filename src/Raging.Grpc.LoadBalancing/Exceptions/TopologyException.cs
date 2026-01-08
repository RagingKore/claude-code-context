using System.Net;

namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// Topology operation failed.
/// </summary>
public sealed class TopologyException : LoadBalancingException {
    /// <summary>
    /// The endpoint where the topology operation failed.
    /// </summary>
    public DnsEndPoint Endpoint { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="TopologyException"/> class.
    /// </summary>
    /// <param name="endpoint">The endpoint where the operation failed.</param>
    /// <param name="innerException">The inner exception.</param>
    public TopologyException(DnsEndPoint endpoint, Exception innerException)
        : base($"Topology call to {endpoint.Host}:{endpoint.Port} failed.", innerException) {
        Endpoint = endpoint;
    }
}
