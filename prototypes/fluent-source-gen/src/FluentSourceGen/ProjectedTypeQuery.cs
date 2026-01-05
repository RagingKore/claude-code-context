using Microsoft.CodeAnalysis;

namespace FluentSourceGen;

/// <summary>
/// Represents a projected query where types have been transformed to another type.
/// Enables chaining transformations before terminal operations.
/// </summary>
/// <typeparam name="T">The projected type.</typeparam>
public sealed class ProjectedTypeQuery<T>
{
    readonly IncrementalGeneratorInitializationContext _context;
    readonly IncrementalValuesProvider<ProjectedItem<T>> _provider;

    internal ProjectedTypeQuery(
        IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<ProjectedItem<T>> provider)
    {
        _context = context;
        _provider = provider;
    }

    /// <summary>
    /// Filter projected items.
    /// </summary>
    public ProjectedTypeQuery<T> Where(Func<T, bool> predicate)
    {
        var filtered = _provider.Where(item => item.Value is not null && predicate(item.Value));
        return new ProjectedTypeQuery<T>(_context, filtered);
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

        return new ProjectedTypeQuery<TResult>(_context, projected);
    }

    /// <summary>
    /// Process each projected item.
    /// </summary>
    public void ForEach(Action<T, INamedTypeSymbol, SourceEmitter> action)
    {
        _context.RegisterSourceOutput(_provider, (spc, item) =>
        {
            if (item.Value is null || item.Symbol is null) return;

            var emitter = new SourceEmitter(spc, item.Symbol);
            try
            {
                action(item.Value, item.Symbol, emitter);
            }
            catch (Exception ex)
            {
                emitter.ReportError("FLUENTGEN502", "Generator failed during projection", ex.ToString());
            }
        });
    }

    /// <summary>
    /// Process each projected item (value only).
    /// </summary>
    public void ForEach(Action<T, SourceEmitter> action)
    {
        _context.RegisterSourceOutput(_provider, (spc, item) =>
        {
            if (item.Value is null || item.Symbol is null) return;

            var emitter = new SourceEmitter(spc, item.Symbol);
            try
            {
                action(item.Value, emitter);
            }
            catch (Exception ex)
            {
                emitter.ReportError("FLUENTGEN502", "Generator failed during projection", ex.ToString());
            }
        });
    }

    /// <summary>
    /// Collect all projected items and process them together.
    /// </summary>
    public void ForAll(Action<IReadOnlyList<T>, CollectionEmitter> action)
    {
        var collected = _provider.Collect();

        _context.RegisterSourceOutput(collected, (spc, items) =>
        {
            var values = items
                .Where(i => i.Value is not null)
                .Select(i => i.Value!)
                .ToList();

            if (values.Count == 0) return;

            var emitter = new CollectionEmitter(spc);
            try
            {
                action(values, emitter);
            }
            catch (Exception ex)
            {
                emitter.ReportError("FLUENTGEN502", "Generator failed during collection projection", ex.ToString());
            }
        });
    }

    /// <summary>
    /// Collect all projected items with their source symbols.
    /// </summary>
    public void ForAll(Action<IReadOnlyList<(T Value, INamedTypeSymbol Symbol)>, CollectionEmitter> action)
    {
        var collected = _provider.Collect();

        _context.RegisterSourceOutput(collected, (spc, items) =>
        {
            var pairs = items
                .Where(i => i.Value is not null && i.Symbol is not null)
                .Select(i => (i.Value!, i.Symbol!))
                .ToList();

            if (pairs.Count == 0) return;

            var emitter = new CollectionEmitter(spc);
            try
            {
                action(pairs, emitter);
            }
            catch (Exception ex)
            {
                emitter.ReportError("FLUENTGEN502", "Generator failed during collection projection", ex.ToString());
            }
        });
    }

    /// <summary>
    /// Group projected items by a key.
    /// </summary>
    public ProjectedGroupedQuery<TKey, T> GroupBy<TKey>(Func<T, TKey> keySelector) where TKey : notnull
    {
        return new ProjectedGroupedQuery<TKey, T>(_context, _provider, keySelector);
    }

    /// <summary>
    /// Get distinct projected items.
    /// </summary>
    public ProjectedTypeQuery<T> Distinct()
    {
        var collected = _provider.Collect();
        var distinctProvider = collected.SelectMany((items, _) =>
            items.Where(i => i.Value is not null).DistinctBy(i => i.Value));
        return new ProjectedTypeQuery<T>(_context, distinctProvider);
    }

    /// <summary>
    /// Get distinct projected items using a custom comparer.
    /// </summary>
    public ProjectedTypeQuery<T> Distinct(IEqualityComparer<T> comparer)
    {
        var collected = _provider.Collect();
        var distinctProvider = collected.SelectMany((items, _) =>
            items.Where(i => i.Value is not null).DistinctBy(i => i.Value, comparer));
        return new ProjectedTypeQuery<T>(_context, distinctProvider);
    }
}

/// <summary>
/// Represents a flattened projected query from SelectMany.
/// </summary>
/// <typeparam name="T">The projected element type.</typeparam>
public sealed class FlattenedTypeQuery<T>
{
    readonly IncrementalGeneratorInitializationContext _context;
    readonly IncrementalValuesProvider<FlattenedItem<T>> _provider;

    internal FlattenedTypeQuery(
        IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<FlattenedItem<T>> provider)
    {
        _context = context;
        _provider = provider;
    }

    /// <summary>
    /// Filter flattened items.
    /// </summary>
    public FlattenedTypeQuery<T> Where(Func<T, bool> predicate)
    {
        var filtered = _provider.Where(item => item.Value is not null && predicate(item.Value));
        return new FlattenedTypeQuery<T>(_context, filtered);
    }

    /// <summary>
    /// Process each flattened item.
    /// </summary>
    public void ForEach(Action<T, INamedTypeSymbol, SourceEmitter> action)
    {
        _context.RegisterSourceOutput(_provider, (spc, item) =>
        {
            if (item.Value is null || item.SourceSymbol is null) return;

            var emitter = new SourceEmitter(spc, item.SourceSymbol);
            try
            {
                action(item.Value, item.SourceSymbol, emitter);
            }
            catch (Exception ex)
            {
                emitter.ReportError("FLUENTGEN503", "Generator failed during flattened projection", ex.ToString());
            }
        });
    }

    /// <summary>
    /// Process each flattened item (value only).
    /// </summary>
    public void ForEach(Action<T, SourceEmitter> action)
    {
        _context.RegisterSourceOutput(_provider, (spc, item) =>
        {
            if (item.Value is null || item.SourceSymbol is null) return;

            var emitter = new SourceEmitter(spc, item.SourceSymbol);
            try
            {
                action(item.Value, emitter);
            }
            catch (Exception ex)
            {
                emitter.ReportError("FLUENTGEN503", "Generator failed during flattened projection", ex.ToString());
            }
        });
    }

    /// <summary>
    /// Collect all flattened items.
    /// </summary>
    public void ForAll(Action<IReadOnlyList<T>, CollectionEmitter> action)
    {
        var collected = _provider.Collect();

        _context.RegisterSourceOutput(collected, (spc, items) =>
        {
            var values = items
                .Where(i => i.Value is not null)
                .Select(i => i.Value!)
                .ToList();

            if (values.Count == 0) return;

            var emitter = new CollectionEmitter(spc);
            try
            {
                action(values, emitter);
            }
            catch (Exception ex)
            {
                emitter.ReportError("FLUENTGEN503", "Generator failed during collection", ex.ToString());
            }
        });
    }

    /// <summary>
    /// Get distinct items.
    /// </summary>
    public FlattenedTypeQuery<T> Distinct()
    {
        var collected = _provider.Collect();
        var distinctProvider = collected.SelectMany((items, _) =>
            items.Where(i => i.Value is not null).DistinctBy(i => i.Value));
        return new FlattenedTypeQuery<T>(_context, distinctProvider);
    }
}

/// <summary>
/// Represents grouped projected items.
/// </summary>
public sealed class ProjectedGroupedQuery<TKey, T> where TKey : notnull
{
    readonly IncrementalGeneratorInitializationContext _context;
    readonly IncrementalValuesProvider<ProjectedItem<T>> _provider;
    readonly Func<T, TKey> _keySelector;
    readonly IEqualityComparer<TKey> _comparer;

    internal ProjectedGroupedQuery(
        IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<ProjectedItem<T>> provider,
        Func<T, TKey> keySelector,
        IEqualityComparer<TKey>? comparer = null)
    {
        _context = context;
        _provider = provider;
        _keySelector = keySelector;
        _comparer = comparer ?? EqualityComparer<TKey>.Default;
    }

    /// <summary>
    /// Process each group of projected items.
    /// </summary>
    public void ForEachGroup(Action<TKey, IReadOnlyList<T>, CollectionEmitter> action)
    {
        var keySelector = _keySelector;
        var comparer = _comparer;

        var collected = _provider.Collect();

        _context.RegisterSourceOutput(collected, (spc, items) =>
        {
            var values = items
                .Where(i => i.Value is not null)
                .Select(i => i.Value!)
                .ToList();

            if (values.Count == 0) return;

            var groups = values.GroupBy(keySelector, comparer).ToList();

            var emitter = new CollectionEmitter(spc);

            foreach (var group in groups)
            {
                try
                {
                    action(group.Key, group.ToList(), emitter);
                }
                catch (Exception ex)
                {
                    emitter.ReportError("FLUENTGEN504", $"Generator failed for projected group '{group.Key}'", ex.ToString());
                }
            }
        });
    }
}

/// <summary>
/// Represents a projected item with its source symbol.
/// </summary>
internal readonly record struct ProjectedItem<T>(INamedTypeSymbol? Symbol, T? Value);

/// <summary>
/// Represents a flattened item from SelectMany with its source symbol.
/// </summary>
internal readonly record struct FlattenedItem<T>(INamedTypeSymbol? SourceSymbol, T? Value);
