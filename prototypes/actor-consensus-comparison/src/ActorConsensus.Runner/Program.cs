using ActorConsensus.Contracts;
using ActorConsensus.ProtoActor;
using ActorConsensus.AkkaDotNet;

var separator = new string('═', 80);
var thinSeparator = new string('─', 80);

Console.WriteLine(separator);
Console.WriteLine("  Actor Framework Consensus Comparison");
Console.WriteLine("  Proto.Actor vs Akka.NET — 3-Node Bully Leader Election");
Console.WriteLine(separator);
Console.WriteLine();

// Run Proto.Actor first, then Akka.NET, so output doesn't interleave
await RunClusterScenario(new ProtoActorCluster());

Console.WriteLine();
Console.WriteLine(separator);
Console.WriteLine();

await RunClusterScenario(new AkkaCluster());

Console.WriteLine();
Console.WriteLine(separator);
Console.WriteLine("  Comparison complete. See README.md for analysis.");
Console.WriteLine(separator);

return;

// ------------------------------------------------------------------
// Scenario: Start 3 nodes → let them work → kill leader → observe re-election → work resumes
// ------------------------------------------------------------------
async Task RunClusterScenario(IConsensusCluster cluster)
{
    Console.WriteLine(thinSeparator);
    Console.WriteLine($"  Running scenario with: {cluster.FrameworkName}");
    Console.WriteLine(thinSeparator);
    Console.WriteLine();

    await using (cluster)
    {
        // Step 1: Start the cluster
        Console.WriteLine($"  [{cluster.FrameworkName}] Step 1: Starting 3-node cluster...");
        Console.WriteLine();
        await cluster.StartAsync();

        // Step 2: Wait for election and work to begin
        await Task.Delay(3000);

        Console.WriteLine();
        Console.WriteLine($"  [{cluster.FrameworkName}] Step 2: Cluster status after initial election:");
        await PrintStatus(cluster);

        // Step 3: Let work run for a while
        Console.WriteLine();
        Console.WriteLine($"  [{cluster.FrameworkName}] Step 3: Letting nodes process subscriptions...");
        await Task.Delay(4000);

        Console.WriteLine();
        Console.WriteLine($"  [{cluster.FrameworkName}] Status after work period:");
        await PrintStatus(cluster);

        // Step 4: Kill the leader
        Console.WriteLine();
        Console.WriteLine($"  [{cluster.FrameworkName}] Step 4: Killing the leader node...");
        Console.WriteLine();
        await cluster.KillLeaderAsync();

        // Step 5: Wait for re-election
        Console.WriteLine();
        Console.WriteLine($"  [{cluster.FrameworkName}] Step 5: Waiting for re-election...");
        await Task.Delay(4000);

        Console.WriteLine();
        Console.WriteLine($"  [{cluster.FrameworkName}] Status after re-election:");
        await PrintStatus(cluster);

        // Step 6: Let work continue under new leader
        Console.WriteLine();
        Console.WriteLine($"  [{cluster.FrameworkName}] Step 6: Letting surviving nodes continue work...");
        await Task.Delay(3000);

        Console.WriteLine();
        Console.WriteLine($"  [{cluster.FrameworkName}] Final status:");
        await PrintStatus(cluster);
    }
}

async Task PrintStatus(IConsensusCluster cluster)
{
    var status = await cluster.GetStatusAsync();

    Console.WriteLine($"    Leader: {(status.CurrentLeaderId.HasValue ? $"Node-{status.CurrentLeaderId}" : "NONE")} | Term: {status.CurrentTerm}");
    Console.WriteLine($"    {"Node",-8} {"Alive",-8} {"Role",-10} {"Work Items",-12}");
    Console.WriteLine($"    {"----",-8} {"-----",-8} {"----",-10} {"----------",-12}");

    foreach (var node in status.Nodes)
    {
        var role = node.IsLeader ? "LEADER" : (node.IsAlive ? "follower" : "DOWN");
        Console.WriteLine($"    Node-{node.NodeId,-4} {node.IsAlive,-8} {role,-10} {node.WorkItemsProcessed,-12}");
    }
}
