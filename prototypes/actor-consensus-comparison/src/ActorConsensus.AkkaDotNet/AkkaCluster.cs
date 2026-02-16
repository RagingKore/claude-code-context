using ActorConsensus.Contracts;
using Akka.Actor;

namespace ActorConsensus.AkkaDotNet;

/// <summary>
/// Orchestrates a 3-node Akka.NET consensus cluster within a single ActorSystem.
///
/// Akka.NET differences from Proto.Actor highlighted here:
///   - ActorSystem.Create() with optional HOCON configuration
///   - Props.Create(() => new Actor()) for typed actor creation
///   - IActorRef is the actor reference type (vs PID in Proto.Actor)
///   - Tell() for fire-and-forget (vs Send() in Proto.Actor)
///   - Ask() for request-response with Future (not used here)
///   - Built-in cluster features (singleton, sharding) available via Akka.Cluster
///   - Richer lifecycle (PreStart, PostStop, PreRestart, PostRestart)
///   - Supervision strategies are first-class
/// </summary>
public sealed class AkkaCluster : IConsensusCluster
{
    private readonly ConsensusLog _log;
    private ActorSystem? _system;

    // Actor references and backing instances
    private readonly Dictionary<int, IActorRef> _nodeRefs = [];
    private readonly Dictionary<int, ConsensusNodeActor> _nodeInstances = [];

    private const int NodeCount = 3;

    public string FrameworkName => "Akka.NET";

    public AkkaCluster(ConsensusLog? log = null)
    {
        _log = log ?? new ConsensusLog("Akka.NET");
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _log.Info("Initializing Akka.NET actor system...");

        // Akka.NET: Create actor system — can optionally take HOCON config
        // For local-only actors, no config is needed
        _system = ActorSystem.Create("consensus-cluster");

        // Phase 1: Spawn all node actors
        for (var i = 1; i <= NodeCount; i++)
        {
            var nodeId = i;

            // Akka.NET: Props.Create with a factory lambda
            // Contrast with Proto.Actor's Props.FromProducer
            var props = ConsensusNodeActor.CreateProps(nodeId, _log);
            var actorRef = _system.ActorOf(props, $"node-{nodeId}");
            _nodeRefs[nodeId] = actorRef;
        }

        // Small delay to let PreStart complete
        await Task.Delay(100, ct);

        // Phase 2: Wire up peer references via messages
        // Akka.NET: We send RegisterPeer messages (vs direct method calls in Proto.Actor)
        foreach (var (nodeId, actorRef) in _nodeRefs)
        {
            foreach (var (peerId, peerRef) in _nodeRefs)
            {
                if (peerId != nodeId)
                    actorRef.Tell(new ConsensusNodeActor.RegisterPeer(peerId, peerRef));
            }
        }

        _log.Info($"Spawned {NodeCount} nodes — election will start automatically");
    }

    public async Task KillLeaderAsync(CancellationToken ct = default)
    {
        // To determine the leader, we ask each node for status
        int? leaderId = null;

        foreach (var (nodeId, actorRef) in _nodeRefs)
        {
            try
            {
                // Akka.NET: Ask pattern — sends a message and returns a Task<T>
                var response = await actorRef.Ask<ConsensusNodeActor.NodeStatusResponse>(
                    new GetClusterStatus(),
                    TimeSpan.FromSeconds(1)
                );

                if (response.Status.IsLeader)
                {
                    leaderId = nodeId;
                    break;
                }
            }
            catch
            {
                // Node might be stopped
            }
        }

        if (leaderId is null)
        {
            _log.Info("No current leader to kill");
            return;
        }

        _log.Info($">>> KILLING LEADER Node-{leaderId} <<<");

        if (_nodeRefs.TryGetValue(leaderId.Value, out var leaderRef))
        {
            // Akka.NET: Tell the actor to stop
            leaderRef.Tell(new StopNode());
            await Task.Delay(500, ct);
        }
    }

    public async Task<ClusterStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var nodes = new List<NodeStatus>();
        int? currentLeaderId = null;
        long currentTerm = 0;

        foreach (var (nodeId, actorRef) in _nodeRefs)
        {
            try
            {
                var response = await actorRef.Ask<ConsensusNodeActor.NodeStatusResponse>(
                    new GetClusterStatus(),
                    TimeSpan.FromSeconds(1)
                );

                nodes.Add(response.Status);

                if (response.Status.IsLeader)
                    currentLeaderId = nodeId;
            }
            catch
            {
                nodes.Add(new NodeStatus(nodeId, false, false, 0, null));
            }
        }

        return new ClusterStatus(
            currentLeaderId,
            currentTerm,
            nodes.OrderBy(n => n.NodeId).ToList()
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (_system is not null)
        {
            // Akka.NET: CoordinatedShutdown is the recommended way to terminate
            await _system.Terminate();
            _system = null;
        }

        _nodeRefs.Clear();
    }
}
