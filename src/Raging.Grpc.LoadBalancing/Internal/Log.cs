using System.Net;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Source-generated logging for load balancing operations.
/// </summary>
internal static partial class Log {
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Discovering cluster from {Endpoint}")]
    public static partial void DiscoveringCluster(this ILogger logger, DnsEndPoint endpoint);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Discovered {NodeCount} nodes, {EligibleCount} eligible")]
    public static partial void DiscoveredNodes(this ILogger logger, int nodeCount, int eligibleCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Topology call to {Endpoint} failed")]
    public static partial void TopologyCallFailed(this ILogger logger, DnsEndPoint endpoint, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Topology refresh triggered by status code {StatusCode}")]
    public static partial void RefreshTriggered(this ILogger logger, StatusCode statusCode);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Topology changed: {AddedCount} added, {RemovedCount} removed")]
    public static partial void TopologyChanged(this ILogger logger, int addedCount, int removedCount);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Picker updated with {SubchannelCount} subchannels, top tier has {TopTierCount} nodes")]
    public static partial void PickerUpdated(this ILogger logger, int subchannelCount, int topTierCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "No eligible nodes in topology, total nodes: {TotalNodes}")]
    public static partial void NoEligibleNodes(this ILogger logger, int totalNodes);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Creating seed channel to {Endpoint}")]
    public static partial void CreatingSeedChannel(this ILogger logger, DnsEndPoint endpoint);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Disposing seed channel pool with {ChannelCount} channels")]
    public static partial void DisposingSeedChannelPool(this ILogger logger, int channelCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Topology stream from {Endpoint} ended without returning any data")]
    public static partial void TopologyStreamEmpty(this ILogger logger, DnsEndPoint endpoint);
}
