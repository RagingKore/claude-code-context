using System.Collections.Immutable;
using System.ComponentModel;
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
/// <para><b>Usage:</b></para>
/// <code>
/// builder.WithKurrentDbDiscovery(options =>
/// {
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
    /// Called by the Akka.Discovery framework via reflection.
    /// Use <see cref="AkkaHostingExtensions.WithKurrentDbDiscovery"/> to configure.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public KurrentDbServiceDiscovery(ExtendedActorSystem system, Config config)
    {
        _log = Logging.GetLogger(system, typeof(KurrentDbServiceDiscovery));

        // Settings are passed through the static registry from WithKurrentDbDiscovery(),
        // not through config values. The config only carries the registry key.
        var settingsKey = config.GetString("settings-key", "");

        _settings = string.IsNullOrEmpty(settingsKey)
            ? throw new InvalidOperationException(
                "KurrentDB Discovery must be configured via builder.WithKurrentDbDiscovery(). " +
                "Direct configuration is not supported.")
            : KurrentDbDiscoverySetup.Resolve(settingsKey);

        _log.Info(
            "KurrentDB Discovery starting — stream [{0}], node [{1}], heartbeat every {2}s, TTL {3}s",
            _settings.StreamName,
            _settings.NodeId,
            _settings.HeartbeatInterval.TotalSeconds,
            _settings.HeartbeatTtl.TotalSeconds);

        _membershipActor = system.SystemActorOf(
            MembershipActor.CreateProps(_settings),
            "kurrentdb-discovery-membership");

        var coordinatedShutdown = CoordinatedShutdown.Get(system);
        coordinatedShutdown.AddTask(
            CoordinatedShutdown.PhaseClusterExiting,
            "kurrentdb-discovery-leave",
            async () =>
            {
                try
                {
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
