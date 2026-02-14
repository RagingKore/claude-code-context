using ActorConsensus.Contracts;
using Akka.Actor;

namespace ActorConsensus.AkkaDotNet;

/// <summary>
/// Akka.NET implementation of a consensus node using the Bully election algorithm.
///
/// Key Akka.NET concepts demonstrated:
///   - ReceiveActor base class with Receive&lt;T&gt; pattern matching
///   - Actor lifecycle hooks (PreStart, PostStop)
///   - Scheduler-based timers via Context.System.Scheduler
///   - IActorRef-based addressing for peer communication
///   - Tell() for fire-and-forget messaging
///   - Props.Create() factory pattern
///   - Stash support (not used here but available)
/// </summary>
public sealed class ConsensusNodeActor : ReceiveActor
{
    private readonly int _nodeId;
    private readonly ConsensusLog _log;

    // Peer references — set after all nodes are spawned
    private readonly Dictionary<int, IActorRef> _peers = [];

    // Election state
    private int _currentLeaderId;
    private long _currentTerm;
    private bool _alive = true;
    private int _workItemsProcessed;

    // Heartbeat tracking
    private readonly Dictionary<int, DateTimeOffset> _lastHeartbeats = [];

    // Timers
    private ICancelable? _heartbeatSchedule;
    private ICancelable? _workSchedule;
    private const int HeartbeatIntervalMs = 500;
    private const int WorkTickIntervalMs = 800;
    private const int HeartbeatTimeoutMs = 2000;

    // Akka.NET: Props.Create is the canonical way to create actor instances
    public static Props CreateProps(int nodeId, ConsensusLog log) =>
        Props.Create(() => new ConsensusNodeActor(nodeId, log));

    public ConsensusNodeActor(int nodeId, ConsensusLog log)
    {
        _nodeId = nodeId;
        _log = log;

        // Akka.NET: Receive<T> handlers — declarative message routing
        // This is fundamentally different from Proto.Actor's ReceiveAsync pattern match
        Receive<Heartbeat>(OnHeartbeat);
        Receive<ElectionCall>(OnElectionCall);
        Receive<ElectionAlive>(OnElectionAlive);
        Receive<LeaderElected>(OnLeaderElected);
        Receive<WorkTick>(_ => OnWorkTick());
        Receive<StopNode>(_ => OnStopNode());
        Receive<PeerDown>(msg => OnPeerDown(msg));
        Receive<GetClusterStatus>(_ => OnGetStatus());
        Receive<RegisterPeer>(msg => _peers[msg.PeerId] = msg.PeerRef);
        Receive<TriggerElection>(_ =>
        {
            if (_alive) StartElection();
        });
        Receive<CheckElectionTimeout>(msg => OnCheckElectionTimeout(msg));
    }

    // ------------------------------------------------------------------
    // Lifecycle — Akka.NET uses PreStart/PostStop hooks (not message-based)
    // ------------------------------------------------------------------

    protected override void PreStart()
    {
        base.PreStart();
        _alive = true;
        _log.Lifecycle(_nodeId, "Started — joining cluster");

        // Akka.NET: Scheduler is accessed via Context.System.Scheduler
        // ScheduleTellRepeatedly sends a message to Self on interval
        var scheduler = Context.System.Scheduler;

        _heartbeatSchedule = scheduler.ScheduleTellRepeatedlyCancelable(
            TimeSpan.FromMilliseconds(HeartbeatIntervalMs),
            TimeSpan.FromMilliseconds(HeartbeatIntervalMs),
            Self,
            new HeartbeatTick(),
            Self
        );

        _workSchedule = scheduler.ScheduleTellRepeatedlyCancelable(
            TimeSpan.FromMilliseconds(WorkTickIntervalMs),
            TimeSpan.FromMilliseconds(WorkTickIntervalMs),
            Self,
            new WorkTick(),
            Self
        );

        // Akka.NET: Handle internal heartbeat ticks
        Receive<HeartbeatTick>(_ => OnHeartbeatTick());

        // Trigger initial election after short delay
        scheduler.ScheduleTellOnce(
            TimeSpan.FromMilliseconds(300 + _nodeId * 100),
            Self,
            new TriggerElection(),
            Self
        );
    }

    protected override void PostStop()
    {
        _alive = false;
        _heartbeatSchedule?.Cancel();
        _workSchedule?.Cancel();
        _log.Lifecycle(_nodeId, "Stopped");
        base.PostStop();
    }

    // ------------------------------------------------------------------
    // Heartbeat
    // ------------------------------------------------------------------

    private void OnHeartbeatTick()
    {
        if (!_alive) return;

        var msg = new Heartbeat(_nodeId, _currentLeaderId == _nodeId);

        // Akka.NET: Tell() is fire-and-forget; second parameter is sender
        foreach (var (_, actorRef) in _peers)
            actorRef.Tell(msg, Self);

        CheckLeaderHealth();
    }

    private void OnHeartbeat(Heartbeat msg)
    {
        if (!_alive) return;
        _lastHeartbeats[msg.NodeId] = DateTimeOffset.UtcNow;
    }

    private void CheckLeaderHealth()
    {
        if (_currentLeaderId == _nodeId || _currentLeaderId == 0) return;

        if (_lastHeartbeats.TryGetValue(_currentLeaderId, out var lastSeen))
        {
            var elapsed = DateTimeOffset.UtcNow - lastSeen;
            if (elapsed.TotalMilliseconds > HeartbeatTimeoutMs)
            {
                _log.Election(_nodeId, $"Leader Node-{_currentLeaderId} heartbeat timeout ({elapsed.TotalMilliseconds:F0}ms) — starting election");
                StartElection();
            }
        }
    }

    private void OnPeerDown(PeerDown msg)
    {
        if (!_alive) return;
        if (msg.NodeId == _currentLeaderId)
        {
            _log.Election(_nodeId, $"Leader Node-{msg.NodeId} reported down — starting election");
            StartElection();
        }
    }

    // ------------------------------------------------------------------
    // Bully Election
    // ------------------------------------------------------------------

    private void StartElection()
    {
        var newTerm = _currentTerm + 1;
        _currentTerm = newTerm;
        _log.Election(_nodeId, $"Starting election for term {newTerm}");

        var higherPeers = _peers.Where(p => p.Key > _nodeId).ToList();

        if (higherPeers.Count == 0)
        {
            DeclareVictory();
            return;
        }

        // Akka.NET: Tell() each higher-ID peer with our election call
        foreach (var (_, actorRef) in higherPeers)
            actorRef.Tell(new ElectionCall(_nodeId, newTerm), Self);

        // Schedule timeout — if no higher node responds, we win
        Context.System.Scheduler.ScheduleTellOnce(
            TimeSpan.FromMilliseconds(1500),
            Self,
            new CheckElectionTimeout(newTerm),
            Self
        );
    }

    private void OnElectionCall(ElectionCall msg)
    {
        if (!_alive) return;

        // We have a higher ID — respond ALIVE and start our own election
        if (_peers.TryGetValue(msg.CandidateId, out var callerRef))
        {
            _log.Election(_nodeId, $"Responding ALIVE to election from Node-{msg.CandidateId} (term {msg.Term})");
            callerRef.Tell(new ElectionAlive(_nodeId, msg.Term), Self);
        }

        StartElection();
    }

    private void OnElectionAlive(ElectionAlive msg)
    {
        // A higher node is alive — stand down, it will declare victory
    }

    private void OnCheckElectionTimeout(CheckElectionTimeout msg)
    {
        if (!_alive || msg.Term != _currentTerm) return;

        // Check if any higher-ID node has heartbeated recently
        if (!HasHigherAliveNode())
            DeclareVictory();
    }

    private bool HasHigherAliveNode()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMilliseconds(-HeartbeatTimeoutMs);
        return _lastHeartbeats
            .Any(kvp => kvp.Key > _nodeId && kvp.Value > cutoff);
    }

    private void DeclareVictory()
    {
        _currentLeaderId = _nodeId;
        _log.Leader(_nodeId, $"*** Elected as LEADER for term {_currentTerm} ***");

        // Akka.NET: Broadcast via Tell to all peers
        foreach (var (_, actorRef) in _peers)
            actorRef.Tell(new LeaderElected(_nodeId, _currentTerm), Self);
    }

    private void OnLeaderElected(LeaderElected msg)
    {
        if (!_alive) return;

        _currentLeaderId = msg.LeaderId;
        _currentTerm = Math.Max(_currentTerm, msg.Term);
        _log.Leader(_nodeId, $"Acknowledged Node-{msg.LeaderId} as leader (term {msg.Term})");
    }

    // ------------------------------------------------------------------
    // Work Simulation
    // ------------------------------------------------------------------

    private void OnWorkTick()
    {
        if (!_alive) return;

        _workItemsProcessed++;
        var role = _currentLeaderId == _nodeId ? "leader" : "follower";
        var item = new WorkItem(
            _nodeId,
            _workItemsProcessed,
            $"Subscription-{_nodeId} batch #{_workItemsProcessed} ({role})",
            DateTimeOffset.UtcNow
        );

        if (_workItemsProcessed % 5 == 0)
            _log.Work(_nodeId, $"[{role}] Processed work item #{item.SequenceNumber}: {item.Payload}");
    }

    // ------------------------------------------------------------------
    // Stop
    // ------------------------------------------------------------------

    private void OnStopNode()
    {
        _log.Lifecycle(_nodeId, "Received kill signal — shutting down");
        _alive = false;
        _heartbeatSchedule?.Cancel();
        _workSchedule?.Cancel();

        // Akka.NET: Notify peers about our departure
        foreach (var (_, actorRef) in _peers)
            actorRef.Tell(new PeerDown(_nodeId), Self);
    }

    // ------------------------------------------------------------------
    // Status
    // ------------------------------------------------------------------

    private void OnGetStatus()
    {
        // Akka.NET: Sender is the implicit sender of the current message
        Sender.Tell(new NodeStatusResponse(new NodeStatus(
            _nodeId,
            _alive,
            _alive && _currentLeaderId == _nodeId,
            _workItemsProcessed,
            _lastHeartbeats.GetValueOrDefault(_nodeId)
        )));
    }

    public NodeStatus GetStatus() => new(
        _nodeId,
        _alive,
        _alive && _currentLeaderId == _nodeId,
        _workItemsProcessed,
        _lastHeartbeats.GetValueOrDefault(_nodeId)
    );

    public int CurrentLeaderId => _currentLeaderId;
    public long CurrentTerm => _currentTerm;

    // ------------------------------------------------------------------
    // Internal messages (Akka.NET-specific timer ticks)
    // ------------------------------------------------------------------

    /// <summary>Internal tick for broadcasting heartbeats.</summary>
    internal sealed record HeartbeatTick;

    /// <summary>Internal message to trigger election.</summary>
    internal sealed record TriggerElection;

    /// <summary>Timeout check for a specific election term.</summary>
    internal sealed record CheckElectionTimeout(long Term);

    /// <summary>Register a peer reference after spawn.</summary>
    public sealed record RegisterPeer(int PeerId, IActorRef PeerRef);

    /// <summary>Wrapper for returning node status via Tell pattern.</summary>
    public sealed record NodeStatusResponse(NodeStatus Status);
}
