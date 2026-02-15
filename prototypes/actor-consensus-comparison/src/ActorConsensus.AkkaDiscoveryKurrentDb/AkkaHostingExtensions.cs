using Akka.Hosting;

namespace ActorConsensus.AkkaDiscoveryKurrentDb;

/// <summary>
/// Options for configuring KurrentDB-backed service discovery.
/// </summary>
public sealed class KurrentDbDiscoveryOptions
{
    /// <summary>KurrentDB connection string.</summary>
    public string ConnectionString { get; set; } = "esdb://localhost:2113?tls=false";

    /// <summary>Logical service name — used in the discovery stream name.</summary>
    public string ServiceName { get; set; } = "default";

    /// <summary>How often to write heartbeat events.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long before a node without heartbeats is considered dead.</summary>
    public TimeSpan HeartbeatTtl { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>This node's advertised hostname. Falls back to <c>Dns.GetHostName()</c> if empty.</summary>
    public string PublicHostname { get; set; } = "";

    /// <summary>This node's advertised port (typically the Akka.Remote port).</summary>
    public int PublicPort { get; set; }
}

/// <summary>
/// Akka.Hosting extension methods for KurrentDB service discovery.
/// </summary>
public static class AkkaHostingExtensions
{
    /// <summary>
    /// Configures KurrentDB-backed service discovery for Akka.Cluster Bootstrap.
    ///
    /// Each node writes heartbeat events to a shared KurrentDB stream.
    /// Discovery returns all nodes with recent heartbeats as contact points.
    ///
    /// <para><b>Usage:</b></para>
    /// <code>
    /// services.AddAkka("my-system", builder =>
    /// {
    ///     builder
    ///         .WithRemoting("0.0.0.0", 4053)
    ///         .WithClustering()
    ///         .WithKurrentDbDiscovery(options =>
    ///         {
    ///             options.ConnectionString = "esdb://localhost:2113?tls=false";
    ///             options.PublicHostname = "127.0.0.1";
    ///             options.PublicPort = 4053;
    ///         });
    /// });
    /// </code>
    /// </summary>
    public static AkkaConfigurationBuilder WithKurrentDbDiscovery(
        this AkkaConfigurationBuilder builder,
        Action<KurrentDbDiscoveryOptions>? configure = null)
    {
        var options = new KurrentDbDiscoveryOptions();
        configure?.Invoke(options);

        return WithKurrentDbDiscovery(builder, options);
    }

    /// <summary>
    /// Configures KurrentDB-backed service discovery with the provided options.
    /// </summary>
    public static AkkaConfigurationBuilder WithKurrentDbDiscovery(
        this AkkaConfigurationBuilder builder,
        KurrentDbDiscoveryOptions options)
    {
        // Build strongly-typed settings and register them in the static registry.
        // The discovery provider constructor reads from the registry — not from config.
        var settings = KurrentDbDiscoverySettings.FromOptions(options);
        var settingsKey = KurrentDbDiscoverySetup.Register(settings);

        var fqcn = typeof(KurrentDbServiceDiscovery).AssemblyQualifiedName!
            .Split(',')
            .Take(2)
            .Select(s => s.Trim())
            .Aggregate((a, b) => $"{a}, {b}");

        // Minimal internal wiring — only the class reference and the registry key.
        // All actual configuration flows through the strongly-typed options above.
        builder.AddHocon(
            $$"""
            akka.discovery {
                method = kurrentdb
                kurrentdb {
                    class = "{{fqcn}}"
                    settings-key = "{{settingsKey}}"
                }
            }
            """,
            HoconAddMode.Prepend);

        return builder;
    }
}
