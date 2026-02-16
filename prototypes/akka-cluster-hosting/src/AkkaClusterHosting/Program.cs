using Akka.Actor;
using Akka.Cluster;
using Akka.Cluster.Hosting;
using Akka.Cluster.Hosting.SBR;
using Akka.Event;
using Akka.Hosting;
using Akka.Remote.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// ---------------------------------------------------------------------------
//  Akka.Cluster.Hosting — standalone demo
//
//  Spins up 3 Akka.NET cluster nodes in one process using the Akka.Hosting
//  fluent API (zero HOCON). A ClusterSingleton runs on the oldest node.
//  We kill the leader and watch the singleton migrate automatically.
// ---------------------------------------------------------------------------

const int basePort = 9551;
const string systemName = "demo";
const int nodeCount = 3;

var seedNodes = Enumerable.Range(0, nodeCount)
    .Select(i => $"akka.tcp://{systemName}@127.0.0.1:{basePort + i}")
    .ToArray();

var hosts = new Dictionary<int, IHost>();

// ── Start 3 cluster nodes ────────────────────────────────────────────────

Console.WriteLine("Starting 3 cluster nodes...\n");

for (var i = 1; i <= nodeCount; i++)
{
    var nodeId = i;
    var port = basePort + i - 1;

    var host = new HostBuilder()
        .ConfigureServices(services =>
        {
            services.AddAkka(systemName, builder =>
            {
                builder
                    .ConfigureLoggers(setup =>
                    {
                        setup.LogLevel = LogLevel.WarningLevel;
                    })

                    .WithRemoting("127.0.0.1", port)

                    .WithClustering(new ClusterOptions
                    {
                        SeedNodes = seedNodes,
                        Roles = ["node"],
                        SplitBrainResolver = SplitBrainResolverOption.Default
                    })

                    // The singleton — exactly one instance across the entire cluster.
                    // WithSingleton creates both the ClusterSingletonManager and
                    // a proxy, registered in ActorRegistry as CoordinatorActor.
                    .WithSingleton<CoordinatorActor>(
                        "coordinator",
                        (_, _, _) => Props.Create(() => new CoordinatorActor()))

                    // Per-node actor that subscribes to cluster events
                    .WithActors((system, registry) =>
                    {
                        var nodeActor = system.ActorOf(
                            Props.Create(() => new NodeActor(nodeId)),
                            $"node-{nodeId}");
                        registry.TryRegister<NodeActor>(nodeActor);
                    });
            });
        })
        .Build();

    await host.StartAsync();
    hosts[i] = host;

    Console.WriteLine($"  Node {i} started on port {port}");
}

// ── Wait for cluster convergence ─────────────────────────────────────────

Console.WriteLine("\nWaiting for cluster to converge...\n");
await Task.Delay(5_000);

PrintClusterState(hosts);

// ── Kill the leader ──────────────────────────────────────────────────────

var leaderId = FindLeader(hosts);
if (leaderId.HasValue)
{
    Console.WriteLine($"\n*** Killing node {leaderId} (current leader) ***\n");
    await hosts[leaderId.Value].StopAsync();
    hosts.Remove(leaderId.Value);

    Console.WriteLine("Waiting for singleton migration...\n");
    await Task.Delay(5_000);

    PrintClusterState(hosts);
}

// ── Cleanup ──────────────────────────────────────────────────────────────

Console.WriteLine("\nShutting down remaining nodes...");
foreach (var host in hosts.Values)
    await host.StopAsync();

Console.WriteLine("Done.");

// ── Helpers ──────────────────────────────────────────────────────────────

static int? FindLeader(Dictionary<int, IHost> hosts)
{
    foreach (var (nodeId, host) in hosts)
    {
        var system = host.Services.GetRequiredService<ActorSystem>();
        var leader = Cluster.Get(system).State.Leader;
        if (leader is not null)
        {
            // Match leader address back to a node id by port
            var port = leader.Port ?? 0;
            return hosts.FirstOrDefault(kvp =>
            {
                var s = kvp.Value.Services.GetRequiredService<ActorSystem>();
                return Cluster.Get(s).SelfAddress.Port == port;
            }).Key;
        }
    }

    return null;
}

static void PrintClusterState(Dictionary<int, IHost> hosts)
{
    Console.WriteLine("── Cluster state ──────────────────────────────────────");
    foreach (var (nodeId, host) in hosts.OrderBy(kvp => kvp.Key))
    {
        var system = host.Services.GetRequiredService<ActorSystem>();
        var cluster = Cluster.Get(system);
        var isLeader = cluster.State.Leader == cluster.SelfAddress;
        var members = cluster.State.Members.Count;
        var role = isLeader ? "LEADER" : "follower";

        Console.WriteLine($"  Node {nodeId}  [{role}]  members={members}  address={cluster.SelfAddress}");
    }

    Console.WriteLine("──────────────────────────────────────────────────────\n");
}

// ═══════════════════════════════════════════════════════════════════════════
//  Actors — defined here to keep the example self-contained
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Cluster singleton — exactly one instance runs across the cluster at any time.
/// When the hosting node leaves, the framework migrates it to the next oldest node.
/// </summary>
sealed class CoordinatorActor : ReceiveActor, IWithTimers
{
    private int _ticks;

    public ITimerScheduler Timers { get; set; } = null!;

    public CoordinatorActor()
    {
        Receive<Tick>(_ =>
        {
            _ticks++;
            if (_ticks % 3 == 0)
                Console.WriteLine($"  [singleton] tick #{_ticks} on {Cluster.Get(Context.System).SelfAddress}");
        });
    }

    protected override void PreStart()
    {
        var addr = Cluster.Get(Context.System).SelfAddress;
        Console.WriteLine($"\n  >>> Singleton STARTED on {addr}\n");
        Timers.StartPeriodicTimer("tick", Tick.Instance, TimeSpan.FromSeconds(1));
    }

    protected override void PostStop()
    {
        Console.WriteLine("\n  >>> Singleton STOPPED — migrating...\n");
    }

    sealed class Tick
    {
        public static readonly Tick Instance = new();
        private Tick() { }
    }
}

/// <summary>
/// Per-node actor that subscribes to cluster membership events and logs changes.
/// </summary>
sealed class NodeActor : ReceiveActor
{
    public NodeActor(int nodeId)
    {
        Receive<ClusterEvent.LeaderChanged>(msg =>
        {
            if (msg.Leader is null) return;
            var self = Cluster.Get(Context.System).SelfAddress;
            var role = msg.Leader == self ? "LEADER" : "follower";
            Console.WriteLine($"  Node {nodeId}: leader changed → {msg.Leader} (I am {role})");
        });

        Receive<ClusterEvent.MemberUp>(msg =>
            Console.WriteLine($"  Node {nodeId}: member UP — {msg.Member.Address}"));

        Receive<ClusterEvent.MemberRemoved>(msg =>
            Console.WriteLine($"  Node {nodeId}: member REMOVED — {msg.Member.Address}"));

        // Swallow other cluster events
        Receive<ClusterEvent.IClusterDomainEvent>(_ => { });
    }

    protected override void PreStart()
    {
        Cluster.Get(Context.System).Subscribe(
            Self,
            ClusterEvent.SubscriptionInitialStateMode.InitialStateAsEvents,
            typeof(ClusterEvent.IMemberEvent),
            typeof(ClusterEvent.LeaderChanged));
    }

    protected override void PostStop() =>
        Cluster.Get(Context.System).Unsubscribe(Self);
}
