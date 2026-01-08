namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// Load balancing configuration. JSON-serializable.
/// </summary>
public sealed class LoadBalancingOptions {
    /// <summary>
    /// Seed endpoints for discovery. Format: "host:port"
    /// </summary>
    public required string[] Seeds { get; set; }

    /// <summary>
    /// Delay between topology polls (only for polling source).
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Resilience and failure handling configuration.
    /// </summary>
    public ResilienceOptions Resilience { get; set; } = new();
}
