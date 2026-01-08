using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Raging.Grpc.LoadBalancing.Utilities;

namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// Factory for creating load-balanced gRPC channels.
/// </summary>
public static class GrpcLoadBalancedChannel {
    /// <summary>
    /// Create a load-balanced channel for the specified address.
    /// </summary>
    /// <param name="address">The primary endpoint address. Format: "host:port"</param>
    /// <param name="configure">Configuration delegate.</param>
    /// <returns>A configured GrpcChannel with load balancing.</returns>
    public static GrpcChannel ForAddress(string address, Action<LoadBalancingBuilder> configure) {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(configure);

        var primaryEndpoint = EndpointParser.Parse(address);

        var builder = new LoadBalancingBuilder();
        configure(builder);

        return builder.Build(primaryEndpoint);
    }

    /// <summary>
    /// Create a load-balanced channel from configuration.
    /// </summary>
    /// <param name="configuration">Configuration section containing LoadBalancingOptions.</param>
    /// <param name="configure">Configuration delegate for topology source and other non-serializable options.</param>
    /// <returns>A configured GrpcChannel with load balancing.</returns>
    public static GrpcChannel FromConfiguration(
        IConfiguration configuration,
        Action<LoadBalancingBuilder> configure) {

        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configure);

        var options = configuration.Get<LoadBalancingOptions>();

        if (options is null || options.Seeds.Length == 0) {
            throw new LoadBalancingConfigurationException(
                "Configuration must contain at least one seed endpoint.");
        }

        var primaryEndpoint = EndpointParser.Parse(options.Seeds[0]);

        var builder = new LoadBalancingBuilder();

        // Apply configuration options
        if (options.Seeds.Length > 1) {
            builder.WithSeeds(options.Seeds[1..]);
        }

        builder.WithResilience(r => {
            r.Timeout = options.Resilience.Timeout;
            r.MaxDiscoveryAttempts = options.Resilience.MaxDiscoveryAttempts;
            r.InitialBackoff = options.Resilience.InitialBackoff;
            r.MaxBackoff = options.Resilience.MaxBackoff;
            r.RefreshOnStatusCodes = options.Resilience.RefreshOnStatusCodes;
        });

        // Apply user configuration (for topology source, etc.)
        configure(builder);

        return builder.Build(primaryEndpoint);
    }
}
