namespace ActorConsensus.AkkaWorker;

/// <summary>
/// Fluent builder for configuring a cluster-aware partition worker.
/// Hides all Akka.NET complexity (remoting, clustering, gossip, HOCON)
/// behind a simple API.
///
/// <example>
/// <code>
/// await using var worker = Worker.Create("my-system")
///     .ListenOn("127.0.0.1", 7551)
///     .WithSeedNodes(("127.0.0.1", 7551), ("127.0.0.1", 7552))
///     .WithPartitionCount(128)
///     .OnPartitionsChanged(async (change, ct) =>
///     {
///         foreach (var p in change.Assigned) StartProcessing(p);
///         foreach (var p in change.Revoked) StopProcessing(p);
///     })
///     .Build();
///
/// await worker.StartAsync();
/// </code>
/// </example>
/// </summary>
public sealed class WorkerBuilder
{
    private readonly string _systemName;

    private string _host = "127.0.0.1";
    private int _port;
    private string[] _seedNodes = [];
    private string _role = "worker";
    private int _partitionCount = 128;
    private Func<PartitionChange, CancellationToken, Task>? _onPartitionsChanged;

    internal WorkerBuilder(string systemName) => _systemName = systemName;

    /// <summary>
    /// Sets the network address this worker listens on for cluster communication.
    /// </summary>
    public WorkerBuilder ListenOn(string host, int port)
    {
        _host = host;
        _port = port;
        return this;
    }

    /// <summary>
    /// Seed nodes for cluster discovery. At least one seed node must be reachable
    /// for the worker to join the cluster. The builder constructs full Akka addresses
    /// from the system name and host/port pairs.
    /// </summary>
    public WorkerBuilder WithSeedNodes(params (string Host, int Port)[] seeds)
    {
        _seedNodes = seeds
            .Select(s => $"akka.tcp://{_systemName}@{s.Host}:{s.Port}")
            .ToArray();
        return this;
    }

    /// <summary>
    /// Role tag for this worker. Only members with the same role participate
    /// in partition assignment. Defaults to "worker".
    /// </summary>
    public WorkerBuilder WithRole(string role)
    {
        _role = role;
        return this;
    }

    /// <summary>
    /// Total number of partitions to distribute across all workers.
    /// Defaults to 128. Must be >= 1.
    /// </summary>
    public WorkerBuilder WithPartitionCount(int partitionCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partitionCount, 1);
        _partitionCount = partitionCount;
        return this;
    }

    /// <summary>
    /// Callback invoked when this worker's partition assignment changes.
    /// Fires on membership changes (member join, leave, crash) with the diff
    /// of newly assigned and revoked partitions.
    /// </summary>
    public WorkerBuilder OnPartitionsChanged(Func<PartitionChange, CancellationToken, Task> handler)
    {
        _onPartitionsChanged = handler;
        return this;
    }

    /// <summary>
    /// Builds the worker. Call <see cref="Worker.StartAsync"/> to join the cluster.
    /// </summary>
    public Worker Build()
    {
        if (_port == 0)
            throw new InvalidOperationException("Port must be set via ListenOn().");

        if (_seedNodes.Length == 0)
            throw new InvalidOperationException("At least one seed node must be set via WithSeedNodes().");

        var options = new WorkerOptions
        {
            SystemName = _systemName,
            Host = _host,
            Port = _port,
            SeedNodes = _seedNodes,
            Role = _role,
            PartitionCount = _partitionCount,
            OnPartitionsChanged = _onPartitionsChanged
        };

        return new Worker(options);
    }
}
