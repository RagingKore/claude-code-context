using System.Net;
using Grpc.Net.Client.Balancer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Factory for creating cluster resolvers.
/// </summary>
/// <typeparam name="TNode">The node type.</typeparam>
internal sealed class ClusterResolverFactory<TNode> : ResolverFactory
    where TNode : struct, IClusterNode {

    /// <summary>
    /// The URI scheme used by this resolver.
    /// </summary>
    public const string SchemeName = "cluster";

    readonly IStreamingTopologySource<TNode> _topologySource;
    readonly IReadOnlyList<DnsEndPoint> _seeds;
    readonly ResilienceOptions _resilience;
    readonly TimeSpan _refreshDelay;
    readonly SeedChannelPool _channelPool;
    readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Creates a new resolver factory.
    /// </summary>
    public ClusterResolverFactory(
        IStreamingTopologySource<TNode> topologySource,
        IReadOnlyList<DnsEndPoint> seeds,
        ResilienceOptions resilience,
        TimeSpan refreshDelay,
        SeedChannelPool channelPool,
        ILoggerFactory? loggerFactory) {

        _topologySource = topologySource;
        _seeds = seeds;
        _resilience = resilience;
        _refreshDelay = refreshDelay;
        _channelPool = channelPool;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <inheritdoc />
    public override string Name => SchemeName;

    /// <inheritdoc />
    public override Resolver Create(ResolverOptions options) {
        var logger = _loggerFactory.CreateLogger<ClusterResolver<TNode>>();

        var discoveryEngine = new DiscoveryEngine<TNode>(
            _topologySource,
            _channelPool,
            _seeds,
            _resilience,
            logger);

        return new ClusterResolver<TNode>(
            discoveryEngine,
            _topologySource,
            _refreshDelay,
            logger);
    }
}
