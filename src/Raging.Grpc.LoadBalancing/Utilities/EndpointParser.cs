using System.Net;

namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// Utility for parsing endpoint strings.
/// </summary>
public static class EndpointParser {
    /// <summary>
    /// Parse a single endpoint string.
    /// </summary>
    /// <param name="input">The endpoint string in "host:port" format.</param>
    /// <returns>A <see cref="DnsEndPoint"/> representing the endpoint.</returns>
    /// <exception cref="LoadBalancingConfigurationException">Thrown when the format is invalid.</exception>
    public static DnsEndPoint Parse(string input) {
        var trimmed = input.Trim();
        var colonIndex = trimmed.LastIndexOf(':');

        if (colonIndex <= 0 || colonIndex == trimmed.Length - 1)
            throw new LoadBalancingConfigurationException(
                $"Invalid endpoint format: '{input}'. Expected 'host:port'.");

        var host = trimmed[..colonIndex];
        var portStr = trimmed[(colonIndex + 1)..];

        if (!int.TryParse(portStr, out var port) || port is <= 0 or > 65535)
            throw new LoadBalancingConfigurationException(
                $"Invalid port in endpoint: '{input}'.");

        return new DnsEndPoint(host, port);
    }

    /// <summary>
    /// Try to parse a single endpoint string.
    /// </summary>
    /// <param name="input">The endpoint string in "host:port" format.</param>
    /// <param name="endpoint">The parsed endpoint, or null if parsing failed.</param>
    /// <returns>True if parsing succeeded, false otherwise.</returns>
    public static bool TryParse(string? input, out DnsEndPoint? endpoint) {
        endpoint = null;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        var trimmed = input.Trim();
        var colonIndex = trimmed.LastIndexOf(':');

        if (colonIndex <= 0 || colonIndex == trimmed.Length - 1)
            return false;

        var host = trimmed[..colonIndex];
        var portStr = trimmed[(colonIndex + 1)..];

        if (!int.TryParse(portStr, out var port) || port is <= 0 or > 65535)
            return false;

        endpoint = new DnsEndPoint(host, port);
        return true;
    }

    /// <summary>
    /// Parse multiple endpoint strings.
    /// </summary>
    /// <param name="inputs">The endpoint strings in "host:port" format.</param>
    /// <returns>An array of <see cref="DnsEndPoint"/> representing the endpoints.</returns>
    /// <exception cref="LoadBalancingConfigurationException">Thrown when any format is invalid.</exception>
    public static DnsEndPoint[] ParseMany(IEnumerable<string> inputs) =>
        inputs.Select(Parse).ToArray();
}
