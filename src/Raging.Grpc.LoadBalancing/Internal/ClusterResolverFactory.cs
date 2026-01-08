using System.Net;
using Grpc.Net.Client.Balancer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Factory for creating cluster resolvers.
/// </summary>
internal sealed class ClusterResolverFactory<TNode> : ResolverFactory
    where TNode : struct, IClusterNode {

    public const string SchemeName = "cluster";

    readonly IStreamingTopologySource<TNode> _topologySource;
    readonly IReadOnlyList<DnsEndPoint> _seeds;
    readonly ResilienceOptions _resilience;
    readonly SeedChannelPool _channelPool;
    readonly ILoggerFactory _loggerFactory;

    public ClusterResolverFactory(
        IStreamingTopologySource<TNode> topologySource,
        IReadOnlyList<DnsEndPoint> seeds,
        ResilienceOptions resilience,
        SeedChannelPool channelPool,
        ILoggerFactory? loggerFactory) {

        _topologySource = topologySource;
        _seeds = seeds;
        _resilience = resilience;
        _channelPool = channelPool;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public override string Name => SchemeName;

    public override Resolver Create(ResolverOptions options) {
        var logger = _loggerFactory.CreateLogger<ClusterResolver<TNode>>();

        return new ClusterResolver<TNode>(
            _topologySource,
            _seeds,
            _channelPool,
            _resilience,
            logger);
    }
}
