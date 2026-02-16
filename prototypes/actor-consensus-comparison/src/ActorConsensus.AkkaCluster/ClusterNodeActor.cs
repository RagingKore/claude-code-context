using ActorConsensus.Contracts;
using Akka.Actor;
using Akka.Cluster;

namespace ActorConsensus.AkkaCluster;

/// <summary>
/// Akka.NET Cluster-aware node actor that uses cluster membership events
/// instead of a hand-rolled Bully election algorithm.
///
/// Key Akka.NET Cluster concepts demonstrated:
///   - Cluster event subscriptions (LeaderChanged, MemberUp, MemberRemoved)
///   - Become() behavior switching for leader/follower states
///   - No manual heartbeat, no election messages, no term tracking
///   - The cluster gossip protocol handles all failure detection and leader election
/// </summary>
public sealed class ClusterNodeActor : ReceiveActor
{
    private readonly int _nodeId;
    private readonly ConsensusLog _log;
    private readonly Cluster _cluster;

    private int _workItemsProcessed;
    private bool _isLeader;
    private long _leadershipChanges;
    private ICancelable? _workSchedule;

    private const int WorkTickIntervalMs = 800;

    public static Props CreateProps(int nodeId, ConsensusLog log) =>
        Props.Create(() => new ClusterNodeActor(nodeId, log));

    public ClusterNodeActor(int nodeId, ConsensusLog log)
    {
        _nodeId = nodeId;
        _log = log;
        _cluster = Cluster.Get(Context.System);

        // Start in follower behavior — Become(Leader) when cluster says so
        Follower();
    }

    // ------------------------------------------------------------------
    // Behavior: Follower — waiting for cluster to promote us
    // ------------------------------------------------------------------

    private void Follower()
    {
        Receive<ClusterEvent.LeaderChanged>(msg =>
        {
            if (msg.Leader is null) return;

            _isLeader = msg.Leader == _cluster.SelfAddress;

            if (_isLeader)
            {
                _leadershipChanges++;
                _log.Leader(_nodeId, $"*** Promoted to LEADER (change #{_leadershipChanges}) ***");
                Become(Leader); // Swap entire message handler set
            }
            else
            {
                _log.Leader(_nodeId, $"Leader is on {msg.Leader}");
            }
        });

        Receive<ClusterEvent.MemberUp>(msg =>
            _log.Lifecycle(_nodeId, $"Cluster member joined: {msg.Member.Address}"));

        Receive<ClusterEvent.MemberRemoved>(msg =>
            _log.Lifecycle(_nodeId, $"Cluster member removed: {msg.Member.Address}"));

        // Catch-all for other cluster events (avoids dead letters)
        Receive<ClusterEvent.IClusterDomainEvent>(_ => { });

        Receive<WorkTick>(_ => OnWorkTick("follower"));
        Receive<GetClusterStatus>(_ => OnGetStatus());
    }

    // ------------------------------------------------------------------
    // Behavior: Leader — no boolean checks needed, behavior IS the state
    // ------------------------------------------------------------------

    private void Leader()
    {
        Receive<ClusterEvent.LeaderChanged>(msg =>
        {
            if (msg.Leader is null) return;

            _isLeader = msg.Leader == _cluster.SelfAddress;

            if (!_isLeader)
            {
                _log.Leader(_nodeId, $"Demoted from leader — new leader is on {msg.Leader}");
                Become(Follower); // Switch back
            }
        });

        Receive<ClusterEvent.MemberUp>(msg =>
            _log.Lifecycle(_nodeId, $"Cluster member joined: {msg.Member.Address}"));

        Receive<ClusterEvent.MemberRemoved>(msg =>
            _log.Lifecycle(_nodeId, $"Cluster member removed: {msg.Member.Address}"));

        Receive<ClusterEvent.IClusterDomainEvent>(_ => { });

        Receive<WorkTick>(_ => OnWorkTick("leader"));
        Receive<GetClusterStatus>(_ => OnGetStatus());
    }

    // ------------------------------------------------------------------
    // Lifecycle — Cluster subscription replaces manual heartbeat/election
    // ------------------------------------------------------------------

    protected override void PreStart()
    {
        base.PreStart();
        _log.Lifecycle(_nodeId, $"Started — joining cluster at {_cluster.SelfAddress}");

        // Subscribe to cluster events — InitialStateAsEvents replays current state
        _cluster.Subscribe(Self,
            ClusterEvent.SubscriptionInitialStateMode.InitialStateAsEvents,
            typeof(ClusterEvent.IMemberEvent),
            typeof(ClusterEvent.LeaderChanged));

        // Schedule work ticks (same as other implementations)
        _workSchedule = Context.System.Scheduler.ScheduleTellRepeatedlyCancelable(
            TimeSpan.FromMilliseconds(WorkTickIntervalMs),
            TimeSpan.FromMilliseconds(WorkTickIntervalMs),
            Self, new WorkTick(), Self);
    }

    protected override void PostStop()
    {
        _cluster.Unsubscribe(Self);
        _workSchedule?.Cancel();
        _log.Lifecycle(_nodeId, "Stopped");
        base.PostStop();
    }

    // ------------------------------------------------------------------
    // Work Simulation
    // ------------------------------------------------------------------

    private void OnWorkTick(string role)
    {
        _workItemsProcessed++;

        if (_workItemsProcessed % 5 == 0)
            _log.Work(_nodeId, $"[{role}] Processed work item #{_workItemsProcessed}");
    }

    // ------------------------------------------------------------------
    // Status
    // ------------------------------------------------------------------

    private void OnGetStatus()
    {
        Sender.Tell(new NodeStatusResponse(
            new NodeStatus(
                _nodeId,
                true,
                _isLeader,
                _workItemsProcessed,
                DateTimeOffset.UtcNow),
            _leadershipChanges));
    }

    /// <summary>Response wrapper including leadership change count as pseudo-term.</summary>
    public sealed record NodeStatusResponse(NodeStatus Status, long Term);
}
