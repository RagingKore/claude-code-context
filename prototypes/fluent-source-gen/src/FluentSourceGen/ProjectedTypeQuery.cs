using Microsoft.CodeAnalysis;

namespace FluentSourceGen;

/// <summary>
/// Represents a projected query where types have been transformed to another type.
/// Chain filter methods and call Generate() to emit source code.
/// </summary>
/// <typeparam name="T">The projected type.</typeparam>
public sealed class ProjectedTypeQuery<T>
{
    readonly IncrementalValuesProvider<SourcedValue<T>> _provider;
    readonly GeneratorContext _context;

    internal ProjectedTypeQuery(IncrementalValuesProvider<SourcedValue<T>> provider, GeneratorContext context)
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
                ? new SourcedValue<TResult>(selector(item.Value), item.SourceType)
                : new SourcedValue<TResult>(default!, item.SourceType));

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
    /// Generate source code for each projected item using a context.
    /// </summary>
    public void Generate(Func<ProjectedContext<T>, string?> generator, string? suffix = null)
    {
        var provider = Build();
        var ctx = _context;

        _context.EnqueueRegistration(() =>
        {
            ctx.RoslynContext.RegisterSourceOutput(provider, (spc, item) =>
            {
                if (item.Value is null || item.SourceType is null) return;
                var log = ctx.Log.For(spc);
                var genCtx = new ProjectedContext<T>(item.Value, item.SourceType, log);
                try
                {
                    var source = generator(genCtx);
                    if (source is null) return;
                    ctx.AddSource(spc, ctx.GetHintName(item.SourceType, suffix), source, item.SourceType);
                }
                catch (Exception ex)
                {
                    ctx.ReportException(spc, item.SourceType.Name, ex, item.SourceType.Locations.FirstOrDefault());
                }
            });
        });
    }

    /// <summary>
    /// Collect all projected items and generate source file(s) using a batch context.
    /// </summary>
    public void GenerateAll(Func<ProjectedBatchContext<T>, (string HintName, string Source)?> generator)
    {
        var provider = BuildCollected();
        var ctx = _context;

        _context.EnqueueRegistration(() =>
        {
            ctx.RoslynContext.RegisterSourceOutput(provider, (spc, items) =>
            {
                var validItems = items
                    .Where(i => i.Value is not null && i.SourceType is not null)
                    .ToList();
                if (validItems.Count == 0) return;

                var log = ctx.Log.For(spc);
                var genCtx = new ProjectedBatchContext<T>(validItems, log);
                try
                {
                    var result = generator(genCtx);
                    if (result is null) return;
                    ctx.AddSource(spc, result.Value.HintName, result.Value.Source);
                }
                catch (Exception ex)
                {
                    ctx.ReportException(spc, "collection", ex);
                }
            });
        });
    }

    #endregion

    #region Build Methods

    /// <summary>
    /// Builds the query and returns the incremental values provider.
    /// For advanced scenarios only - prefer using Generate() methods.
    /// </summary>
    public IncrementalValuesProvider<SourcedValue<T>> Build() => _provider;

    /// <summary>
    /// Builds the query and returns a collected provider for processing all items together.
    /// For advanced scenarios only - prefer using GenerateAll() methods.
    /// </summary>
    public IncrementalValueProvider<IReadOnlyList<SourcedValue<T>>> BuildCollected()
    {
        return _provider.Collect().Select((items, _) =>
            (IReadOnlyList<SourcedValue<T>>)items.Where(i => i.Value is not null).ToList());
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
    readonly IncrementalValuesProvider<SourcedValue<T>> _provider;
    readonly GeneratorContext _context;

    internal FlattenedTypeQuery(IncrementalValuesProvider<SourcedValue<T>> provider, GeneratorContext context)
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
    /// Generate source code from all flattened items using a batch context.
    /// </summary>
    public void GenerateAll(Func<ProjectedBatchContext<T>, (string HintName, string Source)?> generator)
    {
        var provider = BuildCollected();
        var ctx = _context;

        _context.EnqueueRegistration(() =>
        {
            ctx.RoslynContext.RegisterSourceOutput(provider, (spc, items) =>
            {
                if (items.Count == 0) return;

                var validItems = items
                    .Where(i => i.Value is not null)
                    .ToList();

                var log = ctx.Log.For(spc);
                var genCtx = new ProjectedBatchContext<T>(validItems, log);
                try
                {
                    var result = generator(genCtx);
                    if (result is null) return;
                    ctx.AddSource(spc, result.Value.HintName, result.Value.Source);
                }
                catch (Exception ex)
                {
                    ctx.ReportException(spc, "collection", ex);
                }
            });
        });
    }

    #endregion

    #region Build Methods

    /// <summary>
    /// Builds the query and returns the incremental values provider.
    /// For advanced scenarios only.
    /// </summary>
    public IncrementalValuesProvider<SourcedValue<T>> Build() => _provider;

    /// <summary>
    /// Builds the query and returns a collected provider for processing all items together.
    /// For advanced scenarios only - prefer using GenerateAll() method.
    /// </summary>
    public IncrementalValueProvider<IReadOnlyList<SourcedValue<T>>> BuildCollected()
    {
        return _provider.Collect().Select((items, _) =>
            (IReadOnlyList<SourcedValue<T>>)items.Where(i => i.Value is not null).ToList());
    }

    #endregion
}

/// <summary>
/// Represents grouped projected items.
/// Chain filter methods and call Generate() to emit source code.
/// </summary>
public sealed class ProjectedGroupedQuery<TKey, T> where TKey : notnull
{
    readonly IncrementalValuesProvider<SourcedValue<T>> _provider;
    readonly Func<T, TKey> _keySelector;
    readonly GeneratorContext _context;
    readonly IEqualityComparer<TKey> _comparer;

    internal ProjectedGroupedQuery(
        IncrementalValuesProvider<SourcedValue<T>> provider,
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
    /// Generate source code for each projected group using a batch context.
    /// Access the key via ctx.GetKey&lt;TKey&gt;().
    /// </summary>
    public void Generate(Func<ProjectedBatchContext<T>, (string HintName, string Source)?> generator)
    {
        var provider = Build();
        var ctx = _context;

        _context.EnqueueRegistration(() =>
        {
            ctx.RoslynContext.RegisterSourceOutput(provider, (spc, groupedResult) =>
            {
                var log = ctx.Log.For(spc);
                foreach (var group in groupedResult.GetGroups())
                {
                    var genCtx = new ProjectedBatchContext<T>(group.Items, log, group.Key);
                    try
                    {
                        var result = generator(genCtx);
                        if (result is null) continue;
                        ctx.AddSource(spc, result.Value.HintName, result.Value.Source);
                    }
                    catch (Exception ex)
                    {
                        ctx.ReportException(spc, $"group '{group.Key}'", ex);
                    }
                }
            });
        });
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
    readonly IReadOnlyList<SourcedValue<T>> _items;
    readonly Func<T, TKey> _keySelector;
    readonly IEqualityComparer<TKey> _comparer;

    internal ProjectedGroupedResult(
        IReadOnlyList<SourcedValue<T>> items,
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
    public IReadOnlyList<SourcedValue<T>> Items { get; }

    /// <summary>
    /// The values in this group.
    /// </summary>
    public IEnumerable<T> Values => Items.Where(i => i.Value is not null).Select(i => i.Value!);

    internal ProjectedGroup(TKey key, IReadOnlyList<SourcedValue<T>> items)
    {
        Key = key;
        Items = items;
    }
}
