using ActorConsensus.Contracts;
using Akka.Actor;
using Akka.Cluster;
using Akka.Cluster.Tools.Singleton;
using Akka.Configuration;

namespace ActorConsensus.AkkaCluster;

/// <summary>
/// Orchestrator that creates 3 Akka.NET ActorSystems forming a real cluster.
///
/// Instead of the Bully algorithm, this uses:
///   - Akka.Cluster gossip protocol for membership and failure detection
///   - ClusterSingletonManager for automatic leader placement and failover
///   - Cluster events for leader tracking (no manual heartbeat/timeout)
///
/// Each ActorSystem runs on its own TCP port (simulating 3 separate processes).
/// </summary>
public sealed class AkkaClusterSingleton : IConsensusCluster
{
    private readonly ConsensusLog _log;
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
        // Seed nodes — all 3 nodes know about each other at startup
        var seedNodes = string.Join(",",
            Enumerable.Range(0, 3).Select(i =>
                $"\"akka.tcp://{SystemName}@127.0.0.1:{BasePort + i}\""));

        for (var i = 1; i <= 3; i++)
        {
            var port = BasePort + i - 1;

            var config = ConfigurationFactory.ParseString($$"""
                akka {
                    # Suppress verbose remote/cluster logging for clean demo output
                    loglevel = WARNING

                    actor.provider = cluster

                    remote.dot-netty.tcp {
                        hostname = "127.0.0.1"
                        port = {{port}}
                    }

                    cluster {
                        seed-nodes = [{{seedNodes}}]
                        roles = ["node"]

                        # Split-brain resolver — required for auto-downing unreachable nodes
                        downing-provider-class = "Akka.Cluster.SBR.SplitBrainResolverProvider, Akka.Cluster"
                        split-brain-resolver {
                            active-strategy = keep-majority
                            stable-after = 5s
                        }

                        # Tuned for fast demo — production values would be higher
                        failure-detector {
                            heartbeat-interval = 1s
                            acceptable-heartbeat-pause = 3s
                            threshold = 8
                        }
                    }
                }
                """);

            var system = ActorSystem.Create(SystemName, config);
            _systems[i] = system;
            _aliveNodes.Add(i);

            var cluster = Cluster.Get(system);
            _nodeAddresses[i] = cluster.SelfAddress;

            // ClusterSingletonManager — the framework handles leader placement and failover.
            // Replaces the entire Bully election algorithm.
            var singletonProps = ClusterSingletonManager.Props(
                singletonProps: LeaderSingletonActor.CreateProps(_log),
                terminationMessage: PoisonPill.Instance,
                settings: ClusterSingletonManagerSettings.Create(system));

            system.ActorOf(singletonProps, "leader-singleton");

            // Local node actor — subscribes to cluster events, processes work
            var nodeActor = system.ActorOf(
                ClusterNodeActor.CreateProps(i, _log), $"node-{i}");
            _nodeActors[i] = nodeActor;

            _log.Lifecycle(i, $"ActorSystem created on port {port}");
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

        if (leaderId == 0 || !_systems.TryGetValue(leaderId, out var leaderSystem))
            return;

        _log.Lifecycle(leaderId, "Killing leader node — leaving cluster gracefully");
        _aliveNodes.Remove(leaderId);

        // Graceful leave triggers fast handover (~1s) vs abrupt crash (~3-10s for failure detector).
        // The ClusterSingletonManager on the remaining oldest node will start a new singleton.
        var cluster = Cluster.Get(leaderSystem);
        cluster.Leave(cluster.SelfAddress);

        // Wait for leave to propagate and singleton to migrate
        await Task.Delay(3000, ct);

        await leaderSystem.Terminate();
        _log.Lifecycle(leaderId, "ActorSystem terminated");
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
        foreach (var (nodeId, system) in _systems)
        {
            try
            {
                if (_aliveNodes.Contains(nodeId))
                {
                    var cluster = Cluster.Get(system);
                    cluster.Leave(cluster.SelfAddress);
                    await Task.Delay(500);
                }

                await system.Terminate();
            }
            catch
            {
                // Best-effort cleanup
            }
        }

        _systems.Clear();
        _nodeActors.Clear();
        _aliveNodes.Clear();
    }
}
