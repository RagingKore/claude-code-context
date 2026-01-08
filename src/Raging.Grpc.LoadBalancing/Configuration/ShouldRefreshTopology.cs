using Grpc.Core;

namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// Determines if an RPC exception should trigger topology refresh.
/// </summary>
/// <param name="exception">The RPC exception that occurred.</param>
/// <returns>True if topology should be refreshed, false otherwise.</returns>
public delegate bool ShouldRefreshTopology(RpcException exception);
