using ActorConsensus.Contracts;
using Akka.Actor;
using Akka.Cluster;
using Akka.Cluster.Hosting;
using Akka.Cluster.Hosting.SBR;
using Akka.Hosting;
using Akka.Remote.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ActorConsensus.AkkaCluster;

/// <summary>
/// Orchestrator that creates 3 Akka.NET ActorSystems forming a real cluster,
/// configured entirely through the Akka.Hosting fluent API (no HOCON).
///
/// Instead of the Bully algorithm, this uses:
///   - Akka.Cluster gossip protocol for membership and failure detection
///   - ClusterSingletonManager via WithSingleton() for automatic leader placement
///   - Cluster events for leader tracking (no manual heartbeat/timeout)
///   - Microsoft.Extensions.Hosting for lifecycle management
///
/// Each IHost runs its own ActorSystem on a separate TCP port (simulating 3 processes).
/// </summary>
public sealed class AkkaClusterSingleton : IConsensusCluster
{
    private readonly ConsensusLog _log;
    private readonly Dictionary<int, IHost> _hosts = [];
    private readonly Dictionary<int, ActorSystem> _systems = [];
    private readonly Dictionary<int, IActorRef> _nodeActors = [];
    private readonly Dictionary<int, Address> _nodeAddresses = [];
    private readonly HashSet<int> _aliveNodes = [];

    // Track leadership changes as pseudo-term (no "term" concept in gossip protocol)
    private long _currentTerm;
    private int? _lastKnownLeaderId;

    private const int BasePort = 7551;
    private const string SystemName = "consensus";

    public string FrameworkName => "Akka.NET Cluster Singleton";

    public AkkaClusterSingleton() : this(new ConsensusLog("AkkaCluster")) { }

    public AkkaClusterSingleton(ConsensusLog log) => _log = log;

    public async Task StartAsync(CancellationToken ct = default)
    {
        // Seed node addresses — all 3 nodes know about each other at startup
        var seedNodes = Enumerable.Range(0, 3)
            .Select(i => $"akka.tcp://{SystemName}@127.0.0.1:{BasePort + i}")
            .ToArray();

        for (var i = 1; i <= 3; i++)
        {
            var nodeId = i;
            var port = BasePort + i - 1;
            var log = _log;

            // Each node is a separate IHost with its own ActorSystem
            var host = new HostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddAkka(SystemName, builder =>
                    {
                        builder
                            // Suppress verbose remote/cluster logging for clean demo output
                            .AddHocon("akka.loglevel = WARNING", HoconAddMode.Prepend)

                            // Akka.Remote — TCP transport on a unique port per node
                            .WithRemoting("127.0.0.1", port)

                            // Akka.Cluster — gossip-based membership replaces manual heartbeat
                            .WithClustering(new ClusterOptions
                            {
                                SeedNodes = seedNodes,
                                Roles = ["node"],
                                SplitBrainResolver = SplitBrainResolverOption.Default
                            })

                            // Failure detector tuned for fast demo — production values would be higher
                            .AddHocon("""
                                akka.cluster.failure-detector {
                                    heartbeat-interval = 1s
                                    acceptable-heartbeat-pause = 3s
                                    threshold = 8
                                }
                                """, HoconAddMode.Prepend)

                            // ClusterSingleton — replaces the entire Bully election algorithm.
                            // WithSingleton creates both the ClusterSingletonManager (on every node)
                            // and a ClusterSingletonProxy (registered in ActorRegistry).
                            .WithSingleton<LeaderSingletonActor>(
                                "leader-singleton",
                                (_, _, _) => LeaderSingletonActor.CreateProps(log))

                            // Local node actor — subscribes to cluster events, processes work
                            .WithActors((system, registry) =>
                            {
                                var nodeActor = system.ActorOf(
                                    ClusterNodeActor.CreateProps(nodeId, log),
                                    $"node-{nodeId}");
                                registry.TryRegister<ClusterNodeActor>(nodeActor);
                            });
                    });
                })
                .Build();

            await host.StartAsync(ct);

            var system = host.Services.GetRequiredService<ActorSystem>();

            _hosts[i] = host;
            _systems[i] = system;
            _aliveNodes.Add(i);
            _nodeAddresses[i] = Cluster.Get(system).SelfAddress;

            // Get node actor from the hosting ActorRegistry
            var registry = host.Services.GetRequiredService<ActorRegistry>();
            _nodeActors[i] = registry.Get<ClusterNodeActor>();

            _log.Lifecycle(i, $"Host started on port {port}");
        }

        // Wait for cluster gossip to converge and leader to be elected
        _log.Info("Waiting for cluster to form (gossip convergence)...");
        await Task.Delay(5000, ct);
    }

    public async Task KillLeaderAsync(CancellationToken ct = default)
    {
        // Find the leader from any alive node's cluster state
        var aliveEntry = _systems.FirstOrDefault(s => _aliveNodes.Contains(s.Key));
        if (aliveEntry.Value is null) return;

        var leaderAddress = Cluster.Get(aliveEntry.Value).State.Leader;
        if (leaderAddress is null) return;

        var leaderId = _nodeAddresses
            .FirstOrDefault(kvp => kvp.Value == leaderAddress).Key;

        if (leaderId == 0 || !_hosts.TryGetValue(leaderId, out var leaderHost))
            return;

        _log.Lifecycle(leaderId, "Killing leader node — stopping host");
        _aliveNodes.Remove(leaderId);

        // Host.StopAsync triggers CoordinatedShutdown → cluster leave → singleton migration.
        // Much cleaner than manual Cluster.Leave() + System.Terminate().
        await leaderHost.StopAsync(ct);

        await Task.Delay(3000, ct);
        _log.Lifecycle(leaderId, "Host stopped");
    }

    public async Task<ClusterStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var nodes = new List<NodeStatus>();
        int? currentLeaderId = null;

        foreach (var (nodeId, _) in _systems)
        {
            if (!_aliveNodes.Contains(nodeId))
            {
                nodes.Add(new NodeStatus(nodeId, false, false, 0, null));
                continue;
            }

            try
            {
                var response = await _nodeActors[nodeId]
                    .Ask<ClusterNodeActor.NodeStatusResponse>(
                        new GetClusterStatus(), TimeSpan.FromSeconds(2));

                nodes.Add(response.Status);

                if (response.Status.IsLeader)
                    currentLeaderId = nodeId;
            }
            catch
            {
                nodes.Add(new NodeStatus(nodeId, false, false, 0, null));
            }
        }

        // Track leadership transitions as pseudo-term
        if (currentLeaderId.HasValue && currentLeaderId != _lastKnownLeaderId)
        {
            _currentTerm++;
            _lastKnownLeaderId = currentLeaderId;
        }

        return new ClusterStatus(currentLeaderId, _currentTerm, nodes);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (nodeId, host) in _hosts)
        {
            try
            {
                if (_aliveNodes.Contains(nodeId))
                    await host.StopAsync();

                host.Dispose();
            }
            catch
            {
                // Best-effort cleanup
            }
        }

        _hosts.Clear();
        _systems.Clear();
        _nodeActors.Clear();
        _aliveNodes.Clear();
    }
}
