using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Raging.Grpc.LoadBalancing.Utilities;

namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// Extension methods for configuring load balancing with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions {
    /// <summary>
    /// Add gRPC load balancing.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="address">The primary endpoint address. Format: "host:port"</param>
    /// <returns>A builder for configuring load balancing.</returns>
    public static LoadBalancingServiceBuilder AddGrpcLoadBalancing(
        this IServiceCollection services,
        string address) =>
        new(services, address);

    /// <summary>
    /// Add gRPC load balancing from configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="address">The primary endpoint address. Format: "host:port"</param>
    /// <param name="configuration">Configuration section containing LoadBalancingOptions.</param>
    /// <returns>A builder for configuring load balancing.</returns>
    public static LoadBalancingServiceBuilder AddGrpcLoadBalancing(
        this IServiceCollection services,
        string address,
        IConfiguration configuration) {

        var builder = new LoadBalancingServiceBuilder(services, address);
        var options = configuration.Get<LoadBalancingOptions>();

        if (options?.Seeds is { Length: > 0 }) {
            builder.WithSeeds(options.Seeds);
        }

        if (options?.Resilience is not null) {
            builder.WithResilience(r => {
                r.Timeout = options.Resilience.Timeout;
                r.MaxDiscoveryAttempts = options.Resilience.MaxDiscoveryAttempts;
                r.InitialBackoff = options.Resilience.InitialBackoff;
                r.MaxBackoff = options.Resilience.MaxBackoff;
                r.RefreshOnStatusCodes = options.Resilience.RefreshOnStatusCodes;
            });
        }

        return builder;
    }
}
