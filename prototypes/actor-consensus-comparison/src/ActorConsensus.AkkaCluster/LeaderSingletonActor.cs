using ActorConsensus.Contracts;
using Akka.Actor;
using Akka.Cluster;

namespace ActorConsensus.AkkaCluster;

/// <summary>
/// The cluster singleton actor — guaranteed to have exactly ONE instance running
/// across the entire cluster at any time.
///
/// Managed by <see cref="Akka.Cluster.Tools.Singleton.ClusterSingletonManager"/>:
///   - Automatically placed on the oldest (leader) node
///   - When the hosting node leaves or crashes, migrates to the next oldest node
///   - No manual election, heartbeat, or failover logic required
///
/// This replaces the entire Bully election algorithm (~170 lines) with
/// a framework-managed lifecycle.
/// </summary>
public sealed class LeaderSingletonActor : ReceiveActor, IWithTimers
{
    private readonly ConsensusLog _log;
    private int _coordinationTicks;

    public ITimerScheduler Timers { get; set; } = null!;

    public static Props CreateProps(ConsensusLog log) =>
        Props.Create(() => new LeaderSingletonActor(log));

    public LeaderSingletonActor(ConsensusLog log)
    {
        _log = log;

        // The singleton could handle leader-only coordination work.
        // In this prototype we just tick to prove it's alive.
        Receive<SingletonTick>(_ =>
        {
            _coordinationTicks++;
            if (_coordinationTicks % 5 == 0)
                _log.Leader(0, $"[singleton] Coordination tick #{_coordinationTicks}");
        });
    }

    protected override void PreStart()
    {
        base.PreStart();

        var address = Cluster.Get(Context.System).SelfAddress;
        _log.Leader(0, $"*** Cluster singleton STARTED on {address} ***");

        // IWithTimers automatically cancels timers on PostStop — no manual ICancelable needed
        Timers.StartPeriodicTimer(
            "coordination-tick",
            SingletonTick.Instance,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    protected override void PostStop()
    {
        _log.Leader(0, "*** Cluster singleton STOPPED — migrating to next oldest node ***");
        base.PostStop();
    }

    /// <summary>Internal tick for the singleton's coordination work.</summary>
    internal sealed class SingletonTick
    {
        public static readonly SingletonTick Instance = new();
        private SingletonTick() { }
    }
}
