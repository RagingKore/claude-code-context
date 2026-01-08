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
}
