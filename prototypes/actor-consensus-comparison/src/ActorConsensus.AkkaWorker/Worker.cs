using Akka.Actor;
using Akka.Cluster.Hosting;
using Akka.Cluster.Hosting.SBR;
using Akka.Hosting;
using Akka.Remote.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ActorConsensus.AkkaWorker;

/// <summary>
/// A cluster-aware partition worker that automatically distributes partitions
/// across all members using Akka.NET's gossip protocol.
///
/// Each worker joins a cluster, subscribes to membership changes, and
/// deterministically computes its own partition assignment. Since all workers
/// run the same pure function over the same converged membership, they all
/// arrive at the same global partition map — no leader required.
///
/// The oldest worker (lowest UpNumber) is identified as the implicit leader,
/// available via <see cref="PartitionState.IsOldest"/>.
/// </summary>
public sealed class Worker : IAsyncDisposable
{
    private readonly WorkerOptions _options;

    private IHost? _host;
    private ActorSystem? _system;
    private IActorRef? _partitionActor;

    internal Worker(WorkerOptions options) => _options = options;

    /// <summary>
    /// Entry point for the fluent builder API.
    /// </summary>
    public static WorkerBuilder Create(string systemName) => new(systemName);

    /// <summary>
    /// Builds the underlying Akka.NET host and joins the cluster.
    /// The worker begins receiving partition assignments once gossip converges.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _host = BuildHost();
        await _host.StartAsync(ct);
        _system = _host.Services.GetRequiredService<ActorSystem>();
        _partitionActor = _host.Services.GetRequiredService<ActorRegistry>().Get<PartitionWorkerActor>();
    }

    /// <summary>
    /// Gracefully leaves the cluster and shuts down. Other workers will
    /// detect the departure and recalculate their partition assignments.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_host is not null)
            await _host.StopAsync(ct);
    }

    /// <summary>
    /// Returns a snapshot of this worker's current partition ownership.
    /// </summary>
    public async Task<PartitionState> GetStateAsync(CancellationToken ct = default)
    {
        if (_partitionActor is null)
            throw new InvalidOperationException("Worker has not been started.");

        return await _partitionActor.Ask<PartitionState>(
            PartitionWorkerActor.GetState.Instance,
            TimeSpan.FromSeconds(5),
            ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        _host = null;
        _system = null;
        _partitionActor = null;
    }

    private IHost BuildHost()
    {
        var options = _options;

        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddAkka(options.SystemName, builder =>
                {
                    builder
                        .AddHocon("akka.loglevel = WARNING", HoconAddMode.Prepend)

                        .WithRemoting(options.Host, options.Port)

                        .WithClustering(new ClusterOptions
                        {
                            SeedNodes = options.SeedNodes,
                            Roles = [options.Role],
                            SplitBrainResolver = SplitBrainResolverOption.Default
                        })

                        .AddHocon("""
                            akka.cluster.failure-detector {
                                heartbeat-interval = 1s
                                acceptable-heartbeat-pause = 3s
                                threshold = 8
                            }
                            """, HoconAddMode.Prepend)

                        .WithActors((system, registry) =>
                        {
                            var actor = system.ActorOf(
                                PartitionWorkerActor.CreateProps(
                                    options.PartitionCount,
                                    options.Role,
                                    options.OnPartitionsChanged),
                                "partition-worker");
                            registry.TryRegister<PartitionWorkerActor>(actor);
                        });
                });
            })
            .Build();
    }
}
