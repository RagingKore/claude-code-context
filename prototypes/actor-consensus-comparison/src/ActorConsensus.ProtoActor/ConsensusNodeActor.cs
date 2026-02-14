using ActorConsensus.Contracts;
using Proto;

namespace ActorConsensus.ProtoActor;

/// <summary>
/// Proto.Actor implementation of a consensus node using the Bully election algorithm.
///
/// Key Proto.Actor concepts demonstrated:
///   - Actor lifecycle (Started, Stopping, Stopped)
///   - Message handling via ReceiveAsync pattern matching
///   - Scheduling with context.Scheduler()
///   - PID-based addressing for peer communication
///   - CancellationTokenSource for stopping scheduled work
/// </summary>
public sealed class ConsensusNodeActor : IActor
{
    private readonly int _nodeId;
    private readonly ConsensusLog _log;

    // Peer references — set after all nodes are spawned
    private readonly Dictionary<int, PID> _peers = [];

    // Election state
    private int _currentLeaderId;
    private long _currentTerm;
    private bool _alive = true;
    private int _workItemsProcessed;

    // Heartbeat tracking
    private readonly Dictionary<int, DateTimeOffset> _lastHeartbeats = [];

    // Timers
    private CancellationTokenSource? _schedulerCts;
    private const int HeartbeatIntervalMs = 500;
    private const int WorkTickIntervalMs = 800;
    private const int HeartbeatTimeoutMs = 2000;

    public ConsensusNodeActor(int nodeId, ConsensusLog log)
    {
        _nodeId = nodeId;
        _log = log;
    }

    /// <summary>Registers a peer node PID. Called by the orchestrator before starting.</summary>
    public void AddPeer(int peerId, PID pid) => _peers[peerId] = pid;

    public Task ReceiveAsync(IContext context)
    {
        return context.Message switch
        {
            Started          => OnStarted(context),
            Heartbeat msg    => OnHeartbeat(context, msg),
            ElectionCall msg => OnElectionCall(context, msg),
            ElectionAlive _  => OnElectionAlive(),
            LeaderElected msg=> OnLeaderElected(msg),
            WorkTick         => OnWorkTick(context),
            StopNode         => OnStopNode(context),
            Stopping         => OnStopping(),
            _                => Task.CompletedTask
        };
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    private Task OnStarted(IContext context)
    {
        _alive = true;
        _log.Lifecycle(_nodeId, "Started — joining cluster");

        _schedulerCts = new CancellationTokenSource();

        // Schedule periodic heartbeat broadcast
        _ = ScheduleLoop(HeartbeatIntervalMs, () =>
        {
            if (!_alive) return;

            var msg = new Heartbeat(_nodeId, _currentLeaderId == _nodeId);

            foreach (var (_, pid) in _peers)
                context.Send(pid, msg);

            // Check if leader is down
            CheckLeaderHealth(context);

        }, _schedulerCts.Token);

        // Schedule periodic work ticks
        _ = ScheduleLoop(WorkTickIntervalMs, () =>
        {
            if (_alive)
                context.Send(context.Self, new WorkTick());
        }, _schedulerCts.Token);

        // Trigger initial election after short delay
        _ = Task.Delay(300 + _nodeId * 100).ContinueWith(_ =>
        {
            if (_alive)
                StartElection(context);
        });

        return Task.CompletedTask;
    }

    private Task OnStopping()
    {
        _alive = false;
        _schedulerCts?.Cancel();
        _schedulerCts?.Dispose();
        _schedulerCts = null;
        _log.Lifecycle(_nodeId, "Stopped");
        return Task.CompletedTask;
    }

    private Task OnStopNode(IContext context)
    {
        _log.Lifecycle(_nodeId, "Received kill signal — shutting down");
        _alive = false;
        _schedulerCts?.Cancel();
        _schedulerCts?.Dispose();
        _schedulerCts = null;

        // Notify peers about our departure
        foreach (var (_, pid) in _peers)
            context.Send(pid, new PeerDown(_nodeId));

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // Heartbeat
    // ------------------------------------------------------------------

    private Task OnHeartbeat(IContext context, Heartbeat msg)
    {
        if (!_alive) return Task.CompletedTask;

        _lastHeartbeats[msg.NodeId] = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    private void CheckLeaderHealth(IContext context)
    {
        if (_currentLeaderId == _nodeId || _currentLeaderId == 0) return;

        if (_lastHeartbeats.TryGetValue(_currentLeaderId, out var lastSeen))
        {
            var elapsed = DateTimeOffset.UtcNow - lastSeen;
            if (elapsed.TotalMilliseconds > HeartbeatTimeoutMs)
            {
                _log.Election(_nodeId, $"Leader Node-{_currentLeaderId} heartbeat timeout ({elapsed.TotalMilliseconds:F0}ms) — starting election");
                StartElection(context);
            }
        }
    }

    // ------------------------------------------------------------------
    // Bully Election
    // ------------------------------------------------------------------

    private void StartElection(IContext context)
    {
        var newTerm = _currentTerm + 1;
        _currentTerm = newTerm;
        _log.Election(_nodeId, $"Starting election for term {newTerm}");

        var higherPeers = _peers.Where(p => p.Key > _nodeId).ToList();

        if (higherPeers.Count == 0)
        {
            // No higher-ID peers — we win
            DeclareVictory(context);
            return;
        }

        // Send election call to all higher-ID peers
        foreach (var (_, pid) in higherPeers)
            context.Send(pid, new ElectionCall(_nodeId, newTerm));

        // If no alive response within timeout, declare victory
        _ = Task.Delay(1500).ContinueWith(_ =>
        {
            if (_alive && _currentLeaderId != _nodeId && !HasHigherAliveNode())
                DeclareVictory(context);
        });
    }

    private bool HasHigherAliveNode()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMilliseconds(-HeartbeatTimeoutMs);
        return _lastHeartbeats
            .Any(kvp => kvp.Key > _nodeId && kvp.Value > cutoff);
    }

    private Task OnElectionCall(IContext context, ElectionCall msg)
    {
        if (!_alive) return Task.CompletedTask;

        // We have a higher ID — suppress the caller and start our own election
        if (_peers.TryGetValue(msg.CandidateId, out var callerPid))
        {
            _log.Election(_nodeId, $"Responding ALIVE to election from Node-{msg.CandidateId} (term {msg.Term})");
            context.Send(callerPid, new ElectionAlive(_nodeId, msg.Term));
        }

        // Start our own election (we're higher)
        StartElection(context);
        return Task.CompletedTask;
    }

    private Task OnElectionAlive()
    {
        // A higher node is alive — stand down
        return Task.CompletedTask;
    }

    private void DeclareVictory(IContext context)
    {
        _currentLeaderId = _nodeId;
        _log.Leader(_nodeId, $"*** Elected as LEADER for term {_currentTerm} ***");

        // Broadcast to all peers
        foreach (var (_, pid) in _peers)
            context.Send(pid, new LeaderElected(_nodeId, _currentTerm));
    }

    private Task OnLeaderElected(LeaderElected msg)
    {
        if (!_alive) return Task.CompletedTask;

        _currentLeaderId = msg.LeaderId;
        _currentTerm = Math.Max(_currentTerm, msg.Term);
        _log.Leader(_nodeId, $"Acknowledged Node-{msg.LeaderId} as leader (term {msg.Term})");
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // Work Simulation
    // ------------------------------------------------------------------

    private Task OnWorkTick(IContext context)
    {
        if (!_alive) return Task.CompletedTask;

        _workItemsProcessed++;
        var role = _currentLeaderId == _nodeId ? "leader" : "follower";
        var item = new WorkItem(
            _nodeId,
            _workItemsProcessed,
            $"Subscription-{_nodeId} batch #{_workItemsProcessed} ({role})",
            DateTimeOffset.UtcNow
        );

        if (_workItemsProcessed % 5 == 0) // Log every 5th item to reduce noise
            _log.Work(_nodeId, $"[{role}] Processed work item #{item.SequenceNumber}: {item.Payload}");

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // Status
    // ------------------------------------------------------------------

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
    // Helpers
    // ------------------------------------------------------------------

    private static async Task ScheduleLoop(int intervalMs, Action action, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                action();
        }
        catch (OperationCanceledException) { }
    }
}
