using ActorConsensus.Contracts;
using Proto;

namespace ActorConsensus.ProtoActor;

/// <summary>
/// Orchestrates a 3-node Proto.Actor consensus cluster within a single ActorSystem.
///
/// Proto.Actor differences from Akka.NET highlighted here:
///   - ActorSystem is lightweight, no HOCON configuration needed
///   - Props.FromProducer(() => new Actor()) is the idiomatic factory
///   - context.Send(pid, msg) for fire-and-forget (tell)
///   - No built-in cluster singleton — we implement election ourselves
///   - PID (Process ID) is the actor reference type
/// </summary>
public sealed class ProtoActorCluster : IConsensusCluster
{
    private readonly ConsensusLog _log;
    private ActorSystem? _system;

    // Node actors and their backing instances (for status queries)
    private readonly Dictionary<int, PID> _nodePids = [];
    private readonly Dictionary<int, ConsensusNodeActor> _nodeActors = [];

    private const int NodeCount = 3;

    public string FrameworkName => "Proto.Actor";

    public ProtoActorCluster(ConsensusLog? log = null)
    {
        _log = log ?? new ConsensusLog("Proto.Actor");
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _log.Info("Initializing Proto.Actor system...");

        // Proto.Actor: Create a lightweight actor system — no config files needed
        _system = new ActorSystem();
        var context = _system.Root;

        // Phase 1: Spawn all node actors
        for (var i = 1; i <= NodeCount; i++)
        {
            var nodeId = i;
            var actor = new ConsensusNodeActor(nodeId, _log);
            _nodeActors[nodeId] = actor;

            // Proto.Actor: Props.FromProducer is the standard way to define actor creation
            var props = Props.FromProducer(() => actor);
            var pid = context.Spawn(props);
            _nodePids[nodeId] = pid;
        }

        // Phase 2: Wire up peer references (each node knows about all others)
        foreach (var (nodeId, actor) in _nodeActors)
        {
            foreach (var (peerId, pid) in _nodePids)
            {
                if (peerId != nodeId)
                    actor.AddPeer(peerId, pid);
            }
        }

        _log.Info($"Spawned {NodeCount} nodes — election will start automatically");
        return Task.CompletedTask;
    }

    public async Task KillLeaderAsync(CancellationToken ct = default)
    {
        var leaderId = _nodeActors.Values
            .FirstOrDefault(a => a.GetStatus().IsLeader)
            ?.GetStatus().NodeId;

        if (leaderId is null or 0)
        {
            _log.Info("No current leader to kill");
            return;
        }

        _log.Info($">>> KILLING LEADER Node-{leaderId} <<<");

        if (_nodePids.TryGetValue(leaderId.Value, out var pid))
        {
            // Proto.Actor: Send a message to trigger graceful shutdown
            _system!.Root.Send(pid, new StopNode());

            // Give time for peers to detect the failure via heartbeat timeout
            await Task.Delay(500, ct);
        }
    }

    public Task<ClusterStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var nodes = _nodeActors.Values
            .Select(a => a.GetStatus())
            .OrderBy(n => n.NodeId)
            .ToList();

        var leaderActor = _nodeActors.Values.FirstOrDefault(a => a.GetStatus().IsLeader);

        var status = new ClusterStatus(
            leaderActor?.GetStatus().NodeId,
            leaderActor?.CurrentTerm ?? 0,
            nodes
        );

        return Task.FromResult(status);
    }

    public async ValueTask DisposeAsync()
    {
        if (_system is not null)
        {
            // Proto.Actor: Shutdown the entire actor system
            await _system.ShutdownAsync();
            _system = null;
        }

        _nodePids.Clear();
        _nodeActors.Clear();
    }
}
