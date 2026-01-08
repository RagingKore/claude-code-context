using Grpc.Net.Client.Balancer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Factory for creating cluster load balancers.
/// </summary>
internal sealed class ClusterLoadBalancerFactory : LoadBalancerFactory {
    /// <summary>
    /// The policy name used by this load balancer.
    /// </summary>
    public const string PolicyName = "cluster";

    readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Creates a new load balancer factory.
    /// </summary>
    /// <param name="loggerFactory">Logger factory instance.</param>
    public ClusterLoadBalancerFactory(ILoggerFactory? loggerFactory = null) {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <inheritdoc />
    public override string Name => PolicyName;

    /// <inheritdoc />
    public override LoadBalancer Create(LoadBalancerOptions options) {
        var logger = _loggerFactory.CreateLogger<ClusterLoadBalancer>();
        return new ClusterLoadBalancer(options.Controller, logger);
    }
}
