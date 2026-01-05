using Microsoft.CodeAnalysis;

namespace FluentSourceGen;

/// <summary>
/// Represents a projected query where types have been transformed to another type.
/// Chain filter methods and call Generate() to emit source code.
/// </summary>
/// <typeparam name="T">The projected type.</typeparam>
public sealed class ProjectedTypeQuery<T>
{
    readonly IncrementalValuesProvider<ProjectedItem<T>> _provider;
    readonly GeneratorContext _context;

    internal ProjectedTypeQuery(IncrementalValuesProvider<ProjectedItem<T>> provider, GeneratorContext context)
    {
        _provider = provider;
        _context = context;
    }

    /// <summary>
    /// Filter projected items.
    /// </summary>
    public ProjectedTypeQuery<T> Where(Func<T, bool> predicate)
    {
        var filtered = _provider.Where(item => item.Value is not null && predicate(item.Value));
        return new ProjectedTypeQuery<T>(filtered, _context);
    }

    /// <summary>
    /// Further project the items to another type.
    /// </summary>
    public ProjectedTypeQuery<TResult> Select<TResult>(Func<T, TResult> selector)
    {
        var projected = _provider.Select((item, _) =>
            item.Value is not null
                ? new ProjectedItem<TResult>(item.Symbol, selector(item.Value))
                : new ProjectedItem<TResult>(item.Symbol, default));

        return new ProjectedTypeQuery<TResult>(projected, _context);
    }

    /// <summary>
    /// Group projected items by a key.
    /// </summary>
    public ProjectedGroupedQuery<TKey, T> GroupBy<TKey>(Func<T, TKey> keySelector) where TKey : notnull
    {
        return new ProjectedGroupedQuery<TKey, T>(_provider, keySelector, _context);
    }

    /// <summary>
    /// Get distinct projected items.
    /// </summary>
    public ProjectedTypeQuery<T> Distinct()
    {
        var collected = _provider.Collect();
        var distinctProvider = collected.SelectMany((items, _) =>
            items.Where(i => i.Value is not null).DistinctBy(i => i.Value));
        return new ProjectedTypeQuery<T>(distinctProvider, _context);
    }

    /// <summary>
    /// Get distinct projected items using a custom comparer.
    /// </summary>
    public ProjectedTypeQuery<T> Distinct(IEqualityComparer<T> comparer)
    {
        var collected = _provider.Collect();
        var distinctProvider = collected.SelectMany((items, _) =>
            items.Where(i => i.Value is not null).DistinctBy(i => i.Value, comparer));
        return new ProjectedTypeQuery<T>(distinctProvider, _context);
    }

    #region Generate Methods (Terminal Operations)

    /// <summary>
    /// Generate source code for each projected item.
    /// </summary>
    public void Generate(Func<T, INamedTypeSymbol, string?> generator, string? suffix = null)
    {
        var provider = Build();
        _context.EnqueueRegistration(() =>
            _context.RegisterProjectedTypeQueryOutput(provider, generator, suffix));
    }

    /// <summary>
    /// Generate source code from all projected items collected together.
    /// </summary>
    public void GenerateAll(Func<IReadOnlyList<T>, (string HintName, string Source)?> generator)
    {
        var provider = BuildCollected();
        _context.EnqueueRegistration(() =>
            _context.RegisterCollectedProjectedOutput(provider, generator));
    }

    /// <summary>
    /// Generate source code from all projected items with their source symbols.
    /// </summary>
    public void GenerateAll(Func<IReadOnlyList<(T Value, INamedTypeSymbol Symbol)>, (string HintName, string Source)?> generator)
    {
        var provider = BuildCollected();
        _context.EnqueueRegistration(() =>
            _context.RegisterCollectedProjectedWithSymbolOutput(provider, generator));
    }

    #endregion

    #region Build Methods

    /// <summary>
    /// Builds the query and returns the incremental values provider.
    /// For advanced scenarios only - prefer using Generate() methods.
    /// </summary>
    public IncrementalValuesProvider<ProjectedItem<T>> Build() => _provider;

    /// <summary>
    /// Builds the query and returns a collected provider for processing all items together.
    /// For advanced scenarios only - prefer using GenerateAll() methods.
    /// </summary>
    public IncrementalValueProvider<IReadOnlyList<ProjectedItem<T>>> BuildCollected()
    {
        return _provider.Collect().Select((items, _) =>
            (IReadOnlyList<ProjectedItem<T>>)items.Where(i => i.Value is not null).ToList());
    }

    #endregion
}

/// <summary>
/// Represents a flattened projected query from SelectMany.
/// Chain filter methods and call Generate() to emit source code.
/// </summary>
/// <typeparam name="T">The projected element type.</typeparam>
public sealed class FlattenedTypeQuery<T>
{
    readonly IncrementalValuesProvider<FlattenedItem<T>> _provider;
    readonly GeneratorContext _context;

    internal FlattenedTypeQuery(IncrementalValuesProvider<FlattenedItem<T>> provider, GeneratorContext context)
    {
        _provider = provider;
        _context = context;
    }

    /// <summary>
    /// Filter flattened items.
    /// </summary>
    public FlattenedTypeQuery<T> Where(Func<T, bool> predicate)
    {
        var filtered = _provider.Where(item => item.Value is not null && predicate(item.Value));
        return new FlattenedTypeQuery<T>(filtered, _context);
    }

    /// <summary>
    /// Get distinct items.
    /// </summary>
    public FlattenedTypeQuery<T> Distinct()
    {
        var collected = _provider.Collect();
        var distinctProvider = collected.SelectMany((items, _) =>
            items.Where(i => i.Value is not null).DistinctBy(i => i.Value));
        return new FlattenedTypeQuery<T>(distinctProvider, _context);
    }

    #region Generate Methods (Terminal Operations)

    /// <summary>
    /// Generate source code from all flattened items collected together.
    /// </summary>
    public void GenerateAll(Func<IReadOnlyList<FlattenedItem<T>>, (string HintName, string Source)?> generator)
    {
        var provider = BuildCollected();
        _context.EnqueueRegistration(() =>
            _context.RegisterFlattenedTypeQueryOutput(provider, generator));
    }

    #endregion

    #region Build Methods

    /// <summary>
    /// Builds the query and returns the incremental values provider.
    /// For advanced scenarios only.
    /// </summary>
    public IncrementalValuesProvider<FlattenedItem<T>> Build() => _provider;

    /// <summary>
    /// Builds the query and returns a collected provider for processing all items together.
    /// For advanced scenarios only - prefer using GenerateAll() method.
    /// </summary>
    public IncrementalValueProvider<IReadOnlyList<FlattenedItem<T>>> BuildCollected()
    {
        return _provider.Collect().Select((items, _) =>
            (IReadOnlyList<FlattenedItem<T>>)items.Where(i => i.Value is not null).ToList());
    }

    #endregion
}

/// <summary>
/// Represents grouped projected items.
/// Chain filter methods and call Generate() to emit source code.
/// </summary>
public sealed class ProjectedGroupedQuery<TKey, T> where TKey : notnull
{
    readonly IncrementalValuesProvider<ProjectedItem<T>> _provider;
    readonly Func<T, TKey> _keySelector;
    readonly GeneratorContext _context;
    readonly IEqualityComparer<TKey> _comparer;

    internal ProjectedGroupedQuery(
        IncrementalValuesProvider<ProjectedItem<T>> provider,
        Func<T, TKey> keySelector,
        GeneratorContext context,
        IEqualityComparer<TKey>? comparer = null)
    {
        _provider = provider;
        _keySelector = keySelector;
        _context = context;
        _comparer = comparer ?? EqualityComparer<TKey>.Default;
    }

    #region Generate Methods (Terminal Operations)

    /// <summary>
    /// Generate source code for each projected group.
    /// </summary>
    public void Generate(Func<TKey, IReadOnlyList<T>, (string HintName, string Source)?> generator)
    {
        var provider = Build();
        _context.EnqueueRegistration(() =>
            _context.RegisterProjectedGroupedOutput(provider, generator));
    }

    #endregion

    #region Build Method

    /// <summary>
    /// Builds the query and returns a collected provider with grouped items.
    /// For advanced scenarios only - prefer using Generate() method.
    /// </summary>
    public IncrementalValueProvider<ProjectedGroupedResult<TKey, T>> Build()
    {
        var keySelector = _keySelector;
        var comparer = _comparer;

        return _provider.Collect().Select((items, _) =>
        {
            var values = items
                .Where(i => i.Value is not null)
                .ToList();

            return new ProjectedGroupedResult<TKey, T>(values, keySelector, comparer);
        });
    }

    #endregion
}

/// <summary>
/// Result of a projected grouped query.
/// </summary>
public readonly struct ProjectedGroupedResult<TKey, T> where TKey : notnull
{
    readonly IReadOnlyList<ProjectedItem<T>> _items;
    readonly Func<T, TKey> _keySelector;
    readonly IEqualityComparer<TKey> _comparer;

    internal ProjectedGroupedResult(
        IReadOnlyList<ProjectedItem<T>> items,
        Func<T, TKey> keySelector,
        IEqualityComparer<TKey> comparer)
    {
        _items = items;
        _keySelector = keySelector;
        _comparer = comparer;
    }

    /// <summary>
    /// Gets all groups from the query result.
    /// </summary>
    public IEnumerable<ProjectedGroup<TKey, T>> GetGroups()
    {
        if (_items.Count == 0)
            yield break;

        var groups = _items
            .Where(i => i.Value is not null)
            .GroupBy(i => _keySelector(i.Value!), _comparer);

        foreach (var group in groups)
        {
            yield return new ProjectedGroup<TKey, T>(group.Key, group.ToList());
        }
    }
}

/// <summary>
/// Represents a group of projected items with a common key.
/// </summary>
public readonly struct ProjectedGroup<TKey, T>
{
    /// <summary>
    /// The grouping key.
    /// </summary>
    public TKey Key { get; }

    /// <summary>
    /// The items in this group.
    /// </summary>
    public IReadOnlyList<ProjectedItem<T>> Items { get; }

    /// <summary>
    /// The values in this group.
    /// </summary>
    public IReadOnlyList<T> Values => Items
        .Where(i => i.Value is not null)
        .Select(i => i.Value!)
        .ToList();

    internal ProjectedGroup(TKey key, IReadOnlyList<ProjectedItem<T>> items)
    {
        Key = key;
        Items = items;
    }
}

/// <summary>
/// Represents a projected item with its source symbol.
/// </summary>
public readonly record struct ProjectedItem<T>(INamedTypeSymbol? Symbol, T? Value);

/// <summary>
/// Represents a flattened item from SelectMany with its source symbol.
/// </summary>
public readonly record struct FlattenedItem<T>(INamedTypeSymbol? SourceSymbol, T? Value);
