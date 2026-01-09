using System.Collections.Immutable;
using System.Net;

namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// Represents a node in the cluster.
/// </summary>
public readonly record struct ClusterNode {
    /// <summary>
    /// The endpoint to connect to.
    /// </summary>
    public required DnsEndPoint EndPoint { get; init; }

    /// <summary>
    /// Whether this node can accept connections.
    /// Nodes with IsEligible=false are excluded from load balancing.
    /// </summary>
    public bool IsEligible { get; init; } = true;

    /// <summary>
    /// Selection priority. Lower values are preferred.
    /// Nodes with equal priority are load-balanced via round-robin.
    /// </summary>
    public int Priority { get; init; } = 0;

    /// <summary>
    /// Custom metadata for domain-specific node information.
    /// Use this for datacenter, zone, version, or any other custom attributes.
    /// </summary>
    public ImmutableDictionary<string, object> Metadata { get; init; } = ImmutableDictionary<string, object>.Empty;

    /// <summary>
    /// Gets a metadata value by key, or default if not found.
    /// </summary>
    public T? GetMetadata<T>(string key) =>
        Metadata.TryGetValue(key, out var value) && value is T typed ? typed : default;

    /// <summary>
    /// Creates a new node with additional metadata.
    /// </summary>
    public ClusterNode WithMetadata(string key, object value) =>
        this with { Metadata = Metadata.SetItem(key, value) };
}
