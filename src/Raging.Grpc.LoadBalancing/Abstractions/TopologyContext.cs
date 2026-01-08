using System.Net;
using Grpc.Core;

namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// Context provided to topology source operations.
/// </summary>
public sealed record TopologyContext {
    /// <summary>
    /// Channel connected to a cluster node.
    /// </summary>
    public required ChannelBase Channel { get; init; }

    /// <summary>
    /// Cancellation token for the operation.
    /// </summary>
    public required CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Timeout for this topology call.
    /// </summary>
    public required TimeSpan Timeout { get; init; }

    /// <summary>
    /// The endpoint this channel is connected to.
    /// </summary>
    public required DnsEndPoint Endpoint { get; init; }
}
