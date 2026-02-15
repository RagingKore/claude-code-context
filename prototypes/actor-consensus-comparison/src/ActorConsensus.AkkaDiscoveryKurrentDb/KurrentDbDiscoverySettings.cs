using Akka.Configuration;

namespace ActorConsensus.AkkaDiscoveryKurrentDb;

/// <summary>
/// Configuration for KurrentDB-backed service discovery.
/// Read from the HOCON path <c>akka.discovery.kurrentdb</c>.
/// </summary>
public sealed class KurrentDbDiscoverySettings
{
    /// <summary>KurrentDB connection string (esdb:// or kurrentdb://).</summary>
    public string ConnectionString { get; init; } = "esdb://localhost:2113?tls=false";

    /// <summary>Logical service name used as stream suffix and in Resolved responses.</summary>
    public string ServiceName { get; init; } = "default";

    /// <summary>How often each node writes a heartbeat event.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long since the last heartbeat before a node is considered dead.</summary>
    public TimeSpan HeartbeatTtl { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Stream name prefix. Full stream name = {prefix}{service-name}.</summary>
    public string StreamPrefix { get; init; } = "discovery-";

    /// <summary>This node's advertised hostname.</summary>
    public string PublicHostname { get; init; } = "";

    /// <summary>This node's advertised port (typically Akka.Remote port).</summary>
    public int PublicPort { get; init; }

    /// <summary>Computed stream name for the discovery stream.</summary>
    public string StreamName => $"{StreamPrefix}{ServiceName}";

    /// <summary>Computed node identifier.</summary>
    public string NodeId => $"{PublicHostname}:{PublicPort}";

    /// <summary>
    /// Read settings from the method-specific HOCON config section
    /// (i.e., the <c>akka.discovery.kurrentdb</c> block).
    /// </summary>
    public static KurrentDbDiscoverySettings FromConfig(Config config) => new()
    {
        ConnectionString = config.GetString("connection-string", "esdb://localhost:2113?tls=false"),
        ServiceName = config.GetString("service-name", "default"),
        HeartbeatInterval = GetTimeSpanOrDefault(config, "heartbeat-interval", TimeSpan.FromSeconds(2)),
        HeartbeatTtl = GetTimeSpanOrDefault(config, "heartbeat-ttl", TimeSpan.FromSeconds(10)),
        StreamPrefix = config.GetString("stream-prefix", "discovery-"),
        PublicHostname = config.GetString("public-hostname", ""),
        PublicPort = config.GetInt("public-port", 0),
    };

    private static TimeSpan GetTimeSpanOrDefault(Config config, string path, TimeSpan defaultValue)
    {
        try
        {
            return config.HasPath(path) ? config.GetTimeSpan(path) : defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }
}
