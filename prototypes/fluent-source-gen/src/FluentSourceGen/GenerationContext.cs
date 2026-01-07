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
}

/// <summary>
/// Context provided to GenerateAll and grouped Generate callbacks.
/// </summary>
public sealed class BatchContext
{
    internal BatchContext(IReadOnlyList<INamedTypeSymbol> types, ScopedLogger log, object? key = null)
    {
        Types = types;
        Log = log;
        Key = key;
    }

    /// <summary>
    /// The grouping key (null for non-grouped batches).
    /// </summary>
    public object? Key { get; }

    /// <summary>
    /// All types in this batch/group.
    /// </summary>
    public IReadOnlyList<INamedTypeSymbol> Types { get; }

    /// <summary>
    /// The diagnostic logger.
    /// </summary>
    public ScopedLogger Log { get; }

    /// <summary>
    /// Number of types.
    /// </summary>
    public int Count => Types.Count;

    /// <summary>
    /// Gets the key as a specific type.
    /// </summary>
    public TKey? GetKey<TKey>() => Key is TKey key ? key : default;
}

/// <summary>
/// Context for generating from a single projected item.
/// </summary>
/// <typeparam name="T">The projected type.</typeparam>
public sealed class ProjectedContext<T>
{
    internal ProjectedContext(T value, INamedTypeSymbol sourceType, ScopedLogger log)
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
public sealed class ProjectedBatchContext<T>
{
    internal ProjectedBatchContext(IReadOnlyList<SourcedValue<T>> items, ScopedLogger log, object? key = null)
    {
        Items = items;
        Log = log;
        Key = key;
    }

    /// <summary>
    /// The grouping key (null for non-grouped batches).
    /// </summary>
    public object? Key { get; }

    /// <summary>
    /// All projected items with their source types.
    /// </summary>
    public IReadOnlyList<SourcedValue<T>> Items { get; }

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

    /// <summary>
    /// Gets the key as a specific type.
    /// </summary>
    public TKey? GetKey<TKey>() => Key is TKey key ? key : default;
}

/// <summary>
/// A value with its source type symbol.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
public readonly record struct SourcedValue<T>(T Value, INamedTypeSymbol? SourceType);
