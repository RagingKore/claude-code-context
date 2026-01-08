namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Default node comparer that sorts by Priority ascending.
/// </summary>
/// <typeparam name="TNode">The node type.</typeparam>
internal sealed class DefaultNodeComparer<TNode> : IComparer<TNode>
    where TNode : struct, IClusterNode {

    /// <summary>
    /// Singleton instance.
    /// </summary>
    public static readonly DefaultNodeComparer<TNode> Instance = new();

    private DefaultNodeComparer() { }

    /// <inheritdoc />
    public int Compare(TNode x, TNode y) => x.Priority.CompareTo(y.Priority);
}
