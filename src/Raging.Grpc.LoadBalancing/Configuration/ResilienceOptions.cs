namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// Resilience and failure handling configuration.
/// </summary>
public sealed class ResilienceOptions {
    /// <summary>
    /// Timeout for individual topology calls.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum discovery attempts before failing.
    /// Default: 10.
    /// </summary>
    public int MaxDiscoveryAttempts { get; set; } = 10;

    /// <summary>
    /// Initial backoff after all seeds fail.
    /// Default: 100 milliseconds.
    /// </summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Maximum backoff duration.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// gRPC status codes that trigger topology refresh.
    /// Default: [14] (Unavailable).
    /// </summary>
    public int[] RefreshOnStatusCodes { get; set; } = [14];
}
