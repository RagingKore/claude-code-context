namespace ActorConsensus.KurrentDbMembership;

/// <summary>
/// Options for a single membership node.
/// </summary>
public sealed class MembershipOptions
{
    /// <summary>KurrentDB connection string.</summary>
    public string ConnectionString { get; set; } = "esdb://localhost:2113?tls=false";

    /// <summary>Logical service/cluster name — used as stream suffix.</summary>
    public string ServiceName { get; set; } = "default";

    /// <summary>Unique node identifier within the cluster.</summary>
    public int NodeId { get; set; }

    /// <summary>This node's advertised hostname.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>This node's advertised port.</summary>
    public int Port { get; set; }

    /// <summary>How often this node writes a heartbeat event.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long since the last heartbeat before a node is considered dead.</summary>
    public TimeSpan HeartbeatTtl { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait before attempting leader election after detecting no leader.</summary>
    public TimeSpan ElectionDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Stream name prefix. Full name = {prefix}{service-name}.</summary>
    public string StreamPrefix { get; set; } = "membership-";
}
