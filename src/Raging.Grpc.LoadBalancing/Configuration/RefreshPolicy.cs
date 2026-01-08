using Grpc.Core;

namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// Built-in refresh policies for determining when to refresh topology.
/// </summary>
public static class RefreshPolicy {
    /// <summary>
    /// Refresh on Unavailable status (connection issues).
    /// </summary>
    public static readonly ShouldRefreshTopology Default =
        static ex => ex.StatusCode == StatusCode.Unavailable;

    /// <summary>
    /// Refresh on any of the specified status codes.
    /// </summary>
    /// <param name="codes">The status codes that should trigger a refresh.</param>
    /// <returns>A refresh policy that matches the specified status codes.</returns>
    public static ShouldRefreshTopology OnStatusCodes(params StatusCode[] codes) {
        var set = codes.ToHashSet();
        return ex => set.Contains(ex.StatusCode);
    }

    /// <summary>
    /// Refresh when exception message contains any of the specified strings.
    /// </summary>
    /// <param name="triggers">The strings to search for in the exception message.</param>
    /// <returns>A refresh policy that matches messages containing the triggers.</returns>
    public static ShouldRefreshTopology OnMessageContains(params string[] triggers) =>
        ex => triggers.Any(t => ex.Message.Contains(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Combine multiple policies (refresh if ANY match).
    /// </summary>
    /// <param name="policies">The policies to combine.</param>
    /// <returns>A refresh policy that matches if any of the provided policies match.</returns>
    public static ShouldRefreshTopology Any(params ShouldRefreshTopology[] policies) =>
        ex => policies.Any(p => p(ex));

    /// <summary>
    /// Combine multiple policies (refresh if ALL match).
    /// </summary>
    /// <param name="policies">The policies to combine.</param>
    /// <returns>A refresh policy that matches only if all provided policies match.</returns>
    public static ShouldRefreshTopology All(params ShouldRefreshTopology[] policies) =>
        ex => policies.All(p => p(ex));

    /// <summary>
    /// Create a policy from status code integers (useful for configuration).
    /// </summary>
    /// <param name="statusCodes">The status codes as integers.</param>
    /// <returns>A refresh policy that matches the specified status codes.</returns>
    public static ShouldRefreshTopology FromStatusCodeInts(params int[] statusCodes) =>
        OnStatusCodes(statusCodes.Select(c => (StatusCode)c).ToArray());
}
