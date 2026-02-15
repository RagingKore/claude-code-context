namespace ActorConsensus.AkkaWorker;

/// <summary>
/// Immutable configuration produced by <see cref="WorkerBuilder"/>.
/// </summary>
internal sealed record WorkerOptions
{
    public required string SystemName { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string[] SeedNodes { get; init; }
    public required string Role { get; init; }
    public required int PartitionCount { get; init; }
    public Func<PartitionChange, CancellationToken, Task>? OnPartitionsChanged { get; init; }
}
