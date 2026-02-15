using System.Collections.Concurrent;

namespace ActorConsensus.AkkaDiscoveryKurrentDb;

/// <summary>
/// Resolved settings for KurrentDB-backed service discovery.
/// Built from <see cref="KurrentDbDiscoveryOptions"/> — no external config needed.
/// </summary>
internal sealed record KurrentDbDiscoverySettings(
    string ConnectionString,
    string ServiceName,
    TimeSpan HeartbeatInterval,
    TimeSpan HeartbeatTtl,
    string StreamPrefix,
    string PublicHostname,
    int PublicPort)
{
    /// <summary>Computed stream name for the discovery stream.</summary>
    public string StreamName => $"{StreamPrefix}{ServiceName}";

    /// <summary>Computed node identifier.</summary>
    public string NodeId => $"{PublicHostname}:{PublicPort}";

    internal static KurrentDbDiscoverySettings FromOptions(KurrentDbDiscoveryOptions options)
    {
        var hostname = string.IsNullOrEmpty(options.PublicHostname)
            ? System.Net.Dns.GetHostName()
            : options.PublicHostname;

        return new KurrentDbDiscoverySettings(
            ConnectionString: options.ConnectionString,
            ServiceName: options.ServiceName,
            HeartbeatInterval: options.HeartbeatInterval,
            HeartbeatTtl: options.HeartbeatTtl,
            StreamPrefix: "discovery-",
            PublicHostname: hostname,
            PublicPort: options.PublicPort);
    }
}

/// <summary>
/// Static registry that passes <see cref="KurrentDbDiscoverySettings"/> from
/// the Akka.Hosting extension to the <see cref="KurrentDbServiceDiscovery"/> constructor
/// without using config files. Keyed by a unique ID generated per registration.
/// </summary>
internal static class KurrentDbDiscoverySetup
{
    private static readonly ConcurrentDictionary<string, KurrentDbDiscoverySettings> Registry = new();

    internal static string Register(KurrentDbDiscoverySettings settings)
    {
        var key = Guid.NewGuid().ToString("N");
        Registry[key] = settings;
        return key;
    }

    internal static KurrentDbDiscoverySettings Resolve(string key)
        => Registry.TryGetValue(key, out var settings)
            ? settings
            : throw new InvalidOperationException(
                $"KurrentDB Discovery settings not found for key '{key}'. " +
                "Use builder.WithKurrentDbDiscovery() to configure.");
}
