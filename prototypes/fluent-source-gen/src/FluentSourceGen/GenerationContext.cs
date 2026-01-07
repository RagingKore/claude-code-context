using Microsoft.CodeAnalysis;

namespace FluentSourceGen;

/// <summary>
/// Context provided to Generate callbacks containing the type and logger.
/// Use extension methods on Type to access attribute/interface data.
/// </summary>
public sealed class GenerationContext
{
    internal GenerationContext(INamedTypeSymbol type, ScopedLogger log)
    {
        Type = type;
        Log = log;
    }

    /// <summary>
    /// The type being processed.
    /// </summary>
    public INamedTypeSymbol Type { get; }

    /// <summary>
    /// The diagnostic logger for reporting errors, warnings, and info.
    /// </summary>
    public ScopedLogger Log { get; }

    /// <summary>
    /// Gets the type's first source location, useful for diagnostics.
    /// </summary>
    public Location? Location => Type.Locations.FirstOrDefault();
}

/// <summary>
/// Represents a single item in a batch context.
/// </summary>
public sealed class GenerationItem
{
    internal GenerationItem(INamedTypeSymbol type)
    {
        Type = type;
    }

    /// <summary>
    /// The type being processed.
    /// </summary>
    public INamedTypeSymbol Type { get; }
}

/// <summary>
/// Context provided to GenerateAll and grouped Generate callbacks.
/// </summary>
/// <typeparam name="TKey">The type of the grouping key.</typeparam>
public sealed class BatchContext<TKey>
{
    internal BatchContext(TKey? key, IReadOnlyList<GenerationItem> items, ScopedLogger log)
    {
        Key = key;
        Items = items;
        Log = log;
    }

    /// <summary>
    /// The grouping key.
    /// </summary>
    public TKey? Key { get; }

    /// <summary>
    /// All items in this batch/group.
    /// </summary>
    public IReadOnlyList<GenerationItem> Items { get; }

    /// <summary>
    /// The diagnostic logger.
    /// </summary>
    public ScopedLogger Log { get; }

    /// <summary>
    /// Convenience property to get just the type symbols.
    /// </summary>
    public IEnumerable<INamedTypeSymbol> Types => Items.Select(i => i.Type);

    /// <summary>
    /// Number of items.
    /// </summary>
    public int Count => Items.Count;
}

/// <summary>
/// Non-generic batch context for GenerateAll.
/// </summary>
public sealed class BatchContext
{
    internal BatchContext(IReadOnlyList<GenerationItem> items, ScopedLogger log)
    {
        Items = items;
        Log = log;
    }

    /// <summary>
    /// All items in this batch.
    /// </summary>
    public IReadOnlyList<GenerationItem> Items { get; }

    /// <summary>
    /// The diagnostic logger.
    /// </summary>
    public ScopedLogger Log { get; }

    /// <summary>
    /// Convenience property to get just the type symbols.
    /// </summary>
    public IEnumerable<INamedTypeSymbol> Types => Items.Select(i => i.Type);

    /// <summary>
    /// Number of items.
    /// </summary>
    public int Count => Items.Count;
}

/// <summary>
/// Context for generating from a single projected item.
/// </summary>
/// <typeparam name="T">The projected type.</typeparam>
public sealed class ProjectedGenerationContext<T>
{
    internal ProjectedGenerationContext(T value, INamedTypeSymbol sourceType, ScopedLogger log)
    {
        Value = value;
        SourceType = sourceType;
        Log = log;
    }

    /// <summary>
    /// The projected value.
    /// </summary>
    public T Value { get; }

    /// <summary>
    /// The original source type symbol.
    /// </summary>
    public INamedTypeSymbol SourceType { get; }

    /// <summary>
    /// The diagnostic logger.
    /// </summary>
    public ScopedLogger Log { get; }
}

/// <summary>
/// Context for generating from a collection of projected items.
/// </summary>
/// <typeparam name="T">The projected type.</typeparam>
public sealed class ProjectedCollectionContext<T>
{
    internal ProjectedCollectionContext(IReadOnlyList<(T Value, INamedTypeSymbol SourceType)> items, ScopedLogger log)
    {
        Items = items;
        Log = log;
    }

    /// <summary>
    /// All projected items with their source types.
    /// </summary>
    public IReadOnlyList<(T Value, INamedTypeSymbol SourceType)> Items { get; }

    /// <summary>
    /// The diagnostic logger.
    /// </summary>
    public ScopedLogger Log { get; }

    /// <summary>
    /// Convenience property to get just the values.
    /// </summary>
    public IEnumerable<T> Values => Items.Select(i => i.Value);

    /// <summary>
    /// Number of items.
    /// </summary>
    public int Count => Items.Count;
}

/// <summary>
/// Context for generating from a group of projected items.
/// </summary>
/// <typeparam name="TKey">The grouping key type.</typeparam>
/// <typeparam name="T">The projected type.</typeparam>
public sealed class ProjectedGroupContext<TKey, T>
{
    internal ProjectedGroupContext(TKey key, IReadOnlyList<(T Value, INamedTypeSymbol SourceType)> items, ScopedLogger log)
    {
        Key = key;
        Items = items;
        Log = log;
    }

    /// <summary>
    /// The grouping key.
    /// </summary>
    public TKey Key { get; }

    /// <summary>
    /// All projected items in this group with their source types.
    /// </summary>
    public IReadOnlyList<(T Value, INamedTypeSymbol SourceType)> Items { get; }

    /// <summary>
    /// The diagnostic logger.
    /// </summary>
    public ScopedLogger Log { get; }

    /// <summary>
    /// Convenience property to get just the values.
    /// </summary>
    public IEnumerable<T> Values => Items.Select(i => i.Value);

    /// <summary>
    /// Number of items.
    /// </summary>
    public int Count => Items.Count;
}

/// <summary>
/// Context for generating from a flattened collection of items.
/// </summary>
/// <typeparam name="T">The flattened item type.</typeparam>
public sealed class FlattenedCollectionContext<T>
{
    internal FlattenedCollectionContext(IReadOnlyList<(T Value, INamedTypeSymbol? SourceType)> items, ScopedLogger log)
    {
        Items = items;
        Log = log;
    }

    /// <summary>
    /// All flattened items with their source types.
    /// </summary>
    public IReadOnlyList<(T Value, INamedTypeSymbol? SourceType)> Items { get; }

    /// <summary>
    /// The diagnostic logger.
    /// </summary>
    public ScopedLogger Log { get; }

    /// <summary>
    /// Convenience property to get just the values.
    /// </summary>
    public IEnumerable<T> Values => Items.Select(i => i.Value);

    /// <summary>
    /// Number of items.
    /// </summary>
    public int Count => Items.Count;
}
