namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Calculates exponential backoff with jitter.
/// </summary>
internal static class BackoffCalculator {
    /// <summary>
    /// Calculate the backoff duration for a given attempt.
    /// </summary>
    /// <param name="attempt">The current attempt number (1-based).</param>
    /// <param name="initial">The initial backoff duration.</param>
    /// <param name="max">The maximum backoff duration.</param>
    /// <returns>The calculated backoff duration with jitter.</returns>
    public static TimeSpan Calculate(int attempt, TimeSpan initial, TimeSpan max) {
        // Exponential: initial * 2^(attempt-1)
        // Cap the exponent to avoid overflow
        var exponent = Math.Min(attempt - 1, 30);
        var exponentialTicks = initial.Ticks * (1L << exponent);

        // Cap at max
        var cappedTicks = Math.Min(exponentialTicks, max.Ticks);

        // Add jitter: ±10%
        var jitter = Random.Shared.NextDouble() * 0.2 - 0.1;
        var jitteredTicks = (long)(cappedTicks * (1.0 + jitter));

        // Ensure non-negative
        return TimeSpan.FromTicks(Math.Max(0, jitteredTicks));
    }
}
