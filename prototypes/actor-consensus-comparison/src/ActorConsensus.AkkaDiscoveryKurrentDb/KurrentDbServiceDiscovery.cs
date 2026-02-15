using System.Collections.Immutable;
using Akka.Actor;
using Akka.Configuration;
using Akka.Discovery;
using Akka.Event;

namespace ActorConsensus.AkkaDiscoveryKurrentDb;

/// <summary>
/// Akka.Discovery implementation backed by KurrentDB (EventStoreDB).
///
/// Each node writes periodic <see cref="NodeHeartbeat"/> events to a shared discovery stream.
/// <see cref="Lookup"/> returns all nodes whose last heartbeat is within the configured TTL.
///
/// <para><b>HOCON configuration:</b></para>
/// <code>
/// akka.discovery {
///   method = kurrentdb
///   kurrentdb {
///     class = "ActorConsensus.AkkaDiscoveryKurrentDb.KurrentDbServiceDiscovery, ActorConsensus.AkkaDiscoveryKurrentDb"
///     connection-string = "esdb://localhost:2113?tls=false"
///     service-name = "my-service"
///     heartbeat-interval = 2s
///     heartbeat-ttl = 10s
///     stream-prefix = "discovery-"
///     public-hostname = "127.0.0.1"
///     public-port = 4053
///   }
/// }
/// </code>
///
/// <para><b>Akka.Hosting:</b></para>
/// <code>
/// builder.WithKurrentDbDiscovery(options => {
///     options.ConnectionString = "esdb://localhost:2113?tls=false";
///     options.PublicHostname = "127.0.0.1";
///     options.PublicPort = 4053;
/// });
/// </code>
/// </summary>
public sealed class KurrentDbServiceDiscovery : ServiceDiscovery
{
    private readonly KurrentDbDiscoverySettings _settings;
    private readonly IActorRef _membershipActor;
    private readonly ILoggingAdapter _log;

    /// <summary>
    /// Constructor called by the Akka.Discovery loader.
    /// The loader reads <c>akka.discovery.kurrentdb.class</c> and tries
    /// <c>ctor(ExtendedActorSystem, Config)</c> first.
    /// </summary>
    public KurrentDbServiceDiscovery(ExtendedActorSystem system, Config config)
    {
        _log = Logging.GetLogger(system, typeof(KurrentDbServiceDiscovery));

        _settings = KurrentDbDiscoverySettings.FromConfig(config);

        // Resolve hostname if not explicitly configured
        if (string.IsNullOrEmpty(_settings.PublicHostname))
        {
            _settings = new KurrentDbDiscoverySettings
            {
                ConnectionString = _settings.ConnectionString,
                ServiceName = _settings.ServiceName,
                HeartbeatInterval = _settings.HeartbeatInterval,
                HeartbeatTtl = _settings.HeartbeatTtl,
                StreamPrefix = _settings.StreamPrefix,
                PublicHostname = System.Net.Dns.GetHostName(),
                PublicPort = _settings.PublicPort,
            };
        }

        _log.Info(
            "KurrentDB Discovery starting — stream [{0}], node [{1}], heartbeat every {2}s, TTL {3}s",
            _settings.StreamName,
            _settings.NodeId,
            _settings.HeartbeatInterval.TotalSeconds,
            _settings.HeartbeatTtl.TotalSeconds);

        // Spawn the membership actor under /system so it's managed by the ActorSystem lifecycle
        _membershipActor = system.SystemActorOf(
            MembershipActor.CreateProps(_settings),
            "kurrentdb-discovery-membership");

        // Register coordinated shutdown to write a NodeLeft event
        var coordinatedShutdown = CoordinatedShutdown.Get(system);
        coordinatedShutdown.AddTask(
            CoordinatedShutdown.PhaseClusterExiting,
            "kurrentdb-discovery-leave",
            async () =>
            {
                try
                {
                    // Give the actor a chance to write the leave event
                    await _membershipActor.GracefulStop(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // Best-effort on shutdown
                }

                return Akka.Done.Instance;
            });
    }

    /// <summary>
    /// Returns all nodes with a recent heartbeat in the KurrentDB discovery stream.
    /// Called periodically by Akka.Management Cluster Bootstrap.
    /// </summary>
    public override async Task<Resolved> Lookup(Lookup lookup, TimeSpan resolveTimeout)
    {
        try
        {
            var result = await _membershipActor.Ask<MembershipActor.MembersResult>(
                new MembershipActor.GetMembers(),
                resolveTimeout);

            var targets = result.Entries
                .Select(m => new ResolvedTarget(
                    host: m.Host,
                    port: m.Port,
                    address: null))
                .ToImmutableList();

            _log.Debug("KurrentDB Discovery lookup [{0}]: {1} node(s) alive", lookup.ServiceName, targets.Count);

            return new Resolved(lookup.ServiceName, targets);
        }
        catch (Exception ex) when (ex is TaskCanceledException or TimeoutException or AskTimeoutException)
        {
            _log.Warning("KurrentDB Discovery lookup timed out after {0}s", resolveTimeout.TotalSeconds);
            return new Resolved(lookup.ServiceName);
        }
    }
}
