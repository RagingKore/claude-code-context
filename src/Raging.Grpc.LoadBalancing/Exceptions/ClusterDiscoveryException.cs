using System.Collections.Immutable;
using System.Net;

namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// Failed to discover cluster topology after all attempts.
/// </summary>
public sealed class ClusterDiscoveryException : LoadBalancingException {
    /// <summary>
    /// The number of discovery attempts made.
    /// </summary>
    public int Attempts { get; }

    /// <summary>
    /// The endpoints that were tried.
    /// </summary>
    public ImmutableArray<DnsEndPoint> TriedEndpoints { get; }

    /// <summary>
    /// The exceptions that occurred during discovery attempts.
    /// </summary>
    public ImmutableArray<Exception> Exceptions { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterDiscoveryException"/> class.
    /// </summary>
    /// <param name="attempts">The number of discovery attempts made.</param>
    /// <param name="triedEndpoints">The endpoints that were tried.</param>
    /// <param name="exceptions">The exceptions that occurred.</param>
    public ClusterDiscoveryException(
        int attempts,
        ImmutableArray<DnsEndPoint> triedEndpoints,
        ImmutableArray<Exception> exceptions)
        : base($"Failed to discover cluster after {attempts} attempts across {triedEndpoints.Length} endpoints.") {
        Attempts = attempts;
        TriedEndpoints = triedEndpoints;
        Exceptions = exceptions;
    }
}
