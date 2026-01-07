using Microsoft.CodeAnalysis;

namespace FluentSourceGen;

/// <summary>
/// Context provided to Generate callbacks containing all matched data and utilities.
/// </summary>
public sealed class GenerationContext
{
    internal GenerationContext(
        INamedTypeSymbol type,
        ScopedLogger log,
        IReadOnlyList<AttributeData> attributes,
        IReadOnlyList<INamedTypeSymbol> interfaces)
    {
        Type = type;
        Log = log;
        Attributes = attributes.Select(a => new AttributeMatch(a)).ToList();
        Interfaces = interfaces.Select(i => new InterfaceMatch(i)).ToList();
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
    /// All matched attributes (from WithAttribute filters).
    /// </summary>
    public IReadOnlyList<AttributeMatch> Attributes { get; }

    /// <summary>
    /// All matched interfaces (from Implementing filters).
    /// </summary>
    public IReadOnlyList<InterfaceMatch> Interfaces { get; }

    /// <summary>
    /// The first matched attribute, or null if none.
    /// </summary>
    public AttributeMatch? Attribute => Attributes.Count > 0 ? Attributes[0] : null;

    /// <summary>
    /// The first matched interface, or null if none.
    /// </summary>
    public InterfaceMatch? Interface => Interfaces.Count > 0 ? Interfaces[0] : null;

    /// <summary>
    /// Whether any attributes were matched.
    /// </summary>
    public bool HasAttribute => Attributes.Count > 0;

    /// <summary>
    /// Whether any interfaces were matched.
    /// </summary>
    public bool HasInterface => Interfaces.Count > 0;

    /// <summary>
    /// Gets the type's first source location, useful for diagnostics.
    /// </summary>
    public Location? Location => Type.Locations.FirstOrDefault();
}

/// <summary>
/// Represents a single item in a collection generation context.
/// </summary>
public sealed class GenerationItem
{
    internal GenerationItem(
        INamedTypeSymbol type,
        IReadOnlyList<AttributeData> attributes,
        IReadOnlyList<INamedTypeSymbol> interfaces)
    {
        Type = type;
        Attributes = attributes.Select(a => new AttributeMatch(a)).ToList();
        Interfaces = interfaces.Select(i => new InterfaceMatch(i)).ToList();
    }

    /// <summary>
    /// The type being processed.
    /// </summary>
    public INamedTypeSymbol Type { get; }

    /// <summary>
    /// All matched attributes.
    /// </summary>
    public IReadOnlyList<AttributeMatch> Attributes { get; }

    /// <summary>
    /// All matched interfaces.
    /// </summary>
    public IReadOnlyList<InterfaceMatch> Interfaces { get; }

    /// <summary>
    /// The first matched attribute, or null if none.
    /// </summary>
    public AttributeMatch? Attribute => Attributes.Count > 0 ? Attributes[0] : null;

    /// <summary>
    /// The first matched interface, or null if none.
    /// </summary>
    public InterfaceMatch? Interface => Interfaces.Count > 0 ? Interfaces[0] : null;
}

/// <summary>
/// Context provided to GenerateAll callbacks for batch processing.
/// </summary>
public sealed class CollectionGenerationContext
{
    internal CollectionGenerationContext(
        IReadOnlyList<GenerationItem> items,
        ScopedLogger log)
    {
        Items = items;
        Log = log;
    }

    /// <summary>
    /// All matched types with their attributes and interfaces.
    /// </summary>
    public IReadOnlyList<GenerationItem> Items { get; }

    /// <summary>
    /// The diagnostic logger for reporting errors, warnings, and info.
    /// </summary>
    public ScopedLogger Log { get; }

    /// <summary>
    /// Convenience property to get just the type symbols.
    /// </summary>
    public IEnumerable<INamedTypeSymbol> Types => Items.Select(i => i.Type);

    /// <summary>
    /// Number of items in the collection.
    /// </summary>
    public int Count => Items.Count;
}

/// <summary>
/// Context provided to grouped Generate callbacks.
/// </summary>
/// <typeparam name="TKey">The type of the grouping key.</typeparam>
public sealed class GroupGenerationContext<TKey>
{
    internal GroupGenerationContext(
        TKey key,
        IReadOnlyList<GenerationItem> items,
        ScopedLogger log)
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
    /// All types in this group with their attributes and interfaces.
    /// </summary>
    public IReadOnlyList<GenerationItem> Items { get; }

    /// <summary>
    /// The diagnostic logger for reporting errors, warnings, and info.
    /// </summary>
    public ScopedLogger Log { get; }

    /// <summary>
    /// Convenience property to get just the type symbols.
    /// </summary>
    public IEnumerable<INamedTypeSymbol> Types => Items.Select(i => i.Type);

    /// <summary>
    /// Number of items in the group.
    /// </summary>
    public int Count => Items.Count;
}

/// <summary>
/// Context for generating from a projected item.
/// </summary>
/// <typeparam name="T">The projected type.</typeparam>
public sealed class ProjectedGenerationContext<T>
{
    internal ProjectedGenerationContext(T value, INamedTypeSymbol symbol, ScopedLogger log)
    {
        Value = value;
        Symbol = symbol;
        Log = log;
    }

    /// <summary>
    /// The projected value.
    /// </summary>
    public T Value { get; }

    /// <summary>
    /// The source type symbol.
    /// </summary>
    public INamedTypeSymbol Symbol { get; }

    /// <summary>
    /// The diagnostic logger.
    /// </summary>
    public ScopedLogger Log { get; }

    /// <summary>
    /// Gets the type's first source location, useful for diagnostics.
    /// </summary>
    public Location? Location => Symbol.Locations.FirstOrDefault();
}

/// <summary>
/// Context for generating from a collection of projected items.
/// </summary>
/// <typeparam name="T">The projected type.</typeparam>
public sealed class ProjectedCollectionContext<T>
{
    internal ProjectedCollectionContext(IReadOnlyList<(T Value, INamedTypeSymbol Symbol)> items, ScopedLogger log)
    {
        Items = items;
        Log = log;
    }

    /// <summary>
    /// All projected items with their source symbols.
    /// </summary>
    public IReadOnlyList<(T Value, INamedTypeSymbol Symbol)> Items { get; }

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
    internal ProjectedGroupContext(TKey key, IReadOnlyList<(T Value, INamedTypeSymbol Symbol)> items, ScopedLogger log)
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
    /// All items in this group with their source symbols.
    /// </summary>
    public IReadOnlyList<(T Value, INamedTypeSymbol Symbol)> Items { get; }

    /// <summary>
    /// The diagnostic logger.
    /// </summary>
    public ScopedLogger Log { get; }

    /// <summary>
    /// Convenience property to get just the values.
    /// </summary>
    public IEnumerable<T> Values => Items.Select(i => i.Value);

    /// <summary>
    /// Number of items in the group.
    /// </summary>
    public int Count => Items.Count;
}

/// <summary>
/// Context for generating from flattened items.
/// </summary>
/// <typeparam name="T">The flattened item type.</typeparam>
public sealed class FlattenedCollectionContext<T>
{
    internal FlattenedCollectionContext(IReadOnlyList<(T Value, INamedTypeSymbol? SourceSymbol)> items, ScopedLogger log)
    {
        Items = items;
        Log = log;
    }

    /// <summary>
    /// All flattened items with their source symbols.
    /// </summary>
    public IReadOnlyList<(T Value, INamedTypeSymbol? SourceSymbol)> Items { get; }

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
