using System.Collections.Immutable;
using Akka.Actor;
using Akka.Cluster;

namespace ActorConsensus.AkkaWorker;

/// <summary>
/// Internal actor that subscribes to cluster membership events and
/// recalculates partition ownership using a deterministic function.
///
/// On each membership change:
///   1. Reads the converged member list from cluster state
///   2. Computes new partition assignment (pure function over sorted members)
///   3. Diffs against previous assignment
///   4. Fires the user callback with assigned/revoked partitions
///
/// Since every node runs the same function over the same converged state,
/// all nodes independently arrive at the same global partition map.
/// </summary>
internal sealed class PartitionWorkerActor : ReceiveActor
{
    private readonly Cluster _cluster;
    private readonly int _partitionCount;
    private readonly string _role;
    private readonly Func<PartitionChange, CancellationToken, Task>? _onChanged;

    private ImmutableHashSet<int> _currentPartitions = [];
    private bool _isOldest;
    private int _memberCount;
    private int _workItemsProcessed;
    private ICancelable? _workSchedule;

    public static Props CreateProps(int partitionCount, string role, Func<PartitionChange, CancellationToken, Task>? onChanged) =>
        Props.Create(() => new PartitionWorkerActor(partitionCount, role, onChanged));

    public PartitionWorkerActor(int partitionCount, string role, Func<PartitionChange, CancellationToken, Task>? onChanged)
    {
        _partitionCount = partitionCount;
        _role = role;
        _onChanged = onChanged;
        _cluster = Cluster.Get(Context.System);

        // Membership changes — recalculate on any member event.
        // The assignment function filters by Status == Up, so events for
        // non-Up members (Joining, Leaving, etc.) produce correct no-op diffs.
        ReceiveAsync<ClusterEvent.IMemberEvent>(async _ => await RecalculateAsync());

        // Work simulation — proves the worker is alive and processing.
        Receive<Tick>(_ =>
        {
            if (_currentPartitions.Count > 0)
                _workItemsProcessed++;
        });

        // State queries from Worker.GetStateAsync()
        Receive<GetState>(_ => Sender.Tell(
            new PartitionState(_currentPartitions, _isOldest, _memberCount, _workItemsProcessed)));
    }

    protected override void PreStart()
    {
        base.PreStart();

        _cluster.Subscribe(Self,
            ClusterEvent.SubscriptionInitialStateMode.InitialStateAsEvents,
            typeof(ClusterEvent.IMemberEvent));

        _workSchedule = Context.System.Scheduler.ScheduleTellRepeatedlyCancelable(
            TimeSpan.FromMilliseconds(800),
            TimeSpan.FromMilliseconds(800),
            Self, Tick.Instance, Self);
    }

    protected override void PostStop()
    {
        _cluster.Unsubscribe(Self);
        _workSchedule?.Cancel();
        base.PostStop();
    }

    private async Task RecalculateAsync()
    {
        var members = _cluster.State.Members;
        var self = _cluster.SelfUniqueAddress;

        var newPartitions = PartitionAssignment.Compute(members, self, _partitionCount, _role);
        var isOldest = PartitionAssignment.IsOldest(members, self, _role);
        var memberCount = members.Count(m => m.Status == MemberStatus.Up && m.Roles.Contains(_role));

        var assigned = newPartitions.Except(_currentPartitions);
        var revoked = _currentPartitions.Except(newPartitions);

        if (assigned.Count == 0 && revoked.Count == 0 && isOldest == _isOldest)
            return;

        _currentPartitions = newPartitions;
        _isOldest = isOldest;
        _memberCount = memberCount;

        if (_onChanged is not null)
        {
            var change = new PartitionChange(
                Assigned: assigned,
                Revoked: revoked,
                All: newPartitions,
                IsOldest: isOldest,
                MemberCount: memberCount);

            await _onChanged(change, CancellationToken.None);
        }
    }

    // Internal messages — not visible outside the actor
    internal sealed class Tick
    {
        public static readonly Tick Instance = new();
        private Tick() { }
    }

    internal sealed class GetState
    {
        public static readonly GetState Instance = new();
        private GetState() { }
    }
}
