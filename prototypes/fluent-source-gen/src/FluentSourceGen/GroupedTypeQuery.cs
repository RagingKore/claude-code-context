using Microsoft.CodeAnalysis;

namespace FluentSourceGen;

/// <summary>
/// Represents types grouped by a key selector.
/// Enables processing types in groups for generating aggregate outputs per group.
/// </summary>
/// <typeparam name="TKey">The type of the grouping key.</typeparam>
public sealed class GroupedTypeQuery<TKey> where TKey : notnull
{
    readonly IncrementalGeneratorInitializationContext _context;
    readonly IncrementalValuesProvider<TypeQuery.QueryResult> _provider;
    readonly Func<INamedTypeSymbol, TKey> _keySelector;
    readonly IEqualityComparer<TKey> _comparer;

    internal GroupedTypeQuery(
        IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<TypeQuery.QueryResult> provider,
        Func<INamedTypeSymbol, TKey> keySelector,
        IEqualityComparer<TKey>? comparer = null)
    {
        _context = context;
        _provider = provider;
        _keySelector = keySelector;
        _comparer = comparer ?? EqualityComparer<TKey>.Default;
    }

    /// <summary>
    /// Process each group of types. Each group shares the same key value.
    /// </summary>
    /// <param name="action">Action to execute for each group with key, types, and emitter.</param>
    public void ForEachGroup(Action<TKey, IReadOnlyList<INamedTypeSymbol>, CollectionEmitter> action)
    {
        var keySelector = _keySelector;
        var comparer = _comparer;

        var collected = _provider.Collect();

        _context.RegisterSourceOutput(collected, (spc, results) =>
        {
            var symbols = results
                .Where(r => r.Symbol is not null)
                .Select(r => r.Symbol!)
                .ToList();

            if (symbols.Count == 0) return;

            var groups = symbols
                .GroupBy(keySelector, comparer)
                .ToList();

            var emitter = new CollectionEmitter(spc);

            foreach (var group in groups)
            {
                try
                {
                    action(group.Key, group.ToList(), emitter);
                }
                catch (Exception ex)
                {
                    emitter.ReportError("FLUENTGEN501", $"Generator failed for group '{group.Key}'", ex.ToString());
                }
            }
        });
    }

    /// <summary>
    /// Process each group with attribute data.
    /// </summary>
    public void ForEachGroup(Action<TKey, IReadOnlyList<(INamedTypeSymbol Symbol, AttributeMatch Attribute)>, CollectionEmitter> action)
    {
        var keySelector = _keySelector;
        var comparer = _comparer;

        var collected = _provider.Collect();

        _context.RegisterSourceOutput(collected, (spc, results) =>
        {
            var items = results
                .Where(r => r.Symbol is not null && r.Attributes.Count > 0)
                .Select(r => (Symbol: r.Symbol!, Attribute: new AttributeMatch(r.Attributes[0])))
                .ToList();

            if (items.Count == 0) return;

            var groups = items
                .GroupBy(x => keySelector(x.Symbol), comparer)
                .ToList();

            var emitter = new CollectionEmitter(spc);

            foreach (var group in groups)
            {
                try
                {
                    action(group.Key, group.ToList(), emitter);
                }
                catch (Exception ex)
                {
                    emitter.ReportError("FLUENTGEN501", $"Generator failed for group '{group.Key}'", ex.ToString());
                }
            }
        });
    }

    /// <summary>
    /// Process each group with interface data.
    /// </summary>
    public void ForEachGroup(Action<TKey, IReadOnlyList<(INamedTypeSymbol Symbol, InterfaceMatch Interface)>, CollectionEmitter> action)
    {
        var keySelector = _keySelector;
        var comparer = _comparer;

        var collected = _provider.Collect();

        _context.RegisterSourceOutput(collected, (spc, results) =>
        {
            var items = results
                .Where(r => r.Symbol is not null && r.Interfaces.Count > 0)
                .Select(r => (Symbol: r.Symbol!, Interface: new InterfaceMatch(r.Interfaces[0])))
                .ToList();

            if (items.Count == 0) return;

            var groups = items
                .GroupBy(x => keySelector(x.Symbol), comparer)
                .ToList();

            var emitter = new CollectionEmitter(spc);

            foreach (var group in groups)
            {
                try
                {
                    action(group.Key, group.ToList(), emitter);
                }
                catch (Exception ex)
                {
                    emitter.ReportError("FLUENTGEN501", $"Generator failed for group '{group.Key}'", ex.ToString());
                }
            }
        });
    }

    /// <summary>
    /// Filter groups by a predicate on the key.
    /// </summary>
    public GroupedTypeQuery<TKey> WhereGroup(Func<TKey, bool> predicate)
    {
        // Return a new grouped query that filters groups
        return new FilteredGroupedTypeQuery<TKey>(_context, _provider, _keySelector, _comparer, predicate);
    }

    /// <summary>
    /// Order groups by key.
    /// </summary>
    public OrderedGroupedTypeQuery<TKey> OrderByKey()
    {
        return new OrderedGroupedTypeQuery<TKey>(_context, _provider, _keySelector, _comparer, ascending: true);
    }

    /// <summary>
    /// Order groups by key descending.
    /// </summary>
    public OrderedGroupedTypeQuery<TKey> OrderByKeyDescending()
    {
        return new OrderedGroupedTypeQuery<TKey>(_context, _provider, _keySelector, _comparer, ascending: false);
    }
}

/// <summary>
/// A grouped query with a filter on groups.
/// </summary>
internal sealed class FilteredGroupedTypeQuery<TKey> : GroupedTypeQuery<TKey> where TKey : notnull
{
    readonly Func<TKey, bool> _groupPredicate;
    readonly IncrementalGeneratorInitializationContext _context;
    readonly IncrementalValuesProvider<TypeQuery.QueryResult> _provider;
    readonly Func<INamedTypeSymbol, TKey> _keySelector;
    readonly IEqualityComparer<TKey> _comparer;

    internal FilteredGroupedTypeQuery(
        IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<TypeQuery.QueryResult> provider,
        Func<INamedTypeSymbol, TKey> keySelector,
        IEqualityComparer<TKey> comparer,
        Func<TKey, bool> groupPredicate)
        : base(context, provider, keySelector, comparer)
    {
        _context = context;
        _provider = provider;
        _keySelector = keySelector;
        _comparer = comparer;
        _groupPredicate = groupPredicate;
    }

    /// <summary>
    /// Process each group that passes the filter.
    /// </summary>
    public new void ForEachGroup(Action<TKey, IReadOnlyList<INamedTypeSymbol>, CollectionEmitter> action)
    {
        var keySelector = _keySelector;
        var comparer = _comparer;
        var groupPredicate = _groupPredicate;

        var collected = _provider.Collect();

        _context.RegisterSourceOutput(collected, (spc, results) =>
        {
            var symbols = results
                .Where(r => r.Symbol is not null)
                .Select(r => r.Symbol!)
                .ToList();

            if (symbols.Count == 0) return;

            var groups = symbols
                .GroupBy(keySelector, comparer)
                .Where(g => groupPredicate(g.Key))
                .ToList();

            var emitter = new CollectionEmitter(spc);

            foreach (var group in groups)
            {
                try
                {
                    action(group.Key, group.ToList(), emitter);
                }
                catch (Exception ex)
                {
                    emitter.ReportError("FLUENTGEN501", $"Generator failed for group '{group.Key}'", ex.ToString());
                }
            }
        });
    }
}

/// <summary>
/// A grouped query with ordered groups.
/// </summary>
public sealed class OrderedGroupedTypeQuery<TKey> where TKey : notnull
{
    readonly IncrementalGeneratorInitializationContext _context;
    readonly IncrementalValuesProvider<TypeQuery.QueryResult> _provider;
    readonly Func<INamedTypeSymbol, TKey> _keySelector;
    readonly IEqualityComparer<TKey> _comparer;
    readonly bool _ascending;

    internal OrderedGroupedTypeQuery(
        IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<TypeQuery.QueryResult> provider,
        Func<INamedTypeSymbol, TKey> keySelector,
        IEqualityComparer<TKey> comparer,
        bool ascending)
    {
        _context = context;
        _provider = provider;
        _keySelector = keySelector;
        _comparer = comparer;
        _ascending = ascending;
    }

    /// <summary>
    /// Process each group in order.
    /// </summary>
    public void ForEachGroup(Action<TKey, IReadOnlyList<INamedTypeSymbol>, CollectionEmitter> action)
    {
        var keySelector = _keySelector;
        var comparer = _comparer;
        var ascending = _ascending;

        var collected = _provider.Collect();

        _context.RegisterSourceOutput(collected, (spc, results) =>
        {
            var symbols = results
                .Where(r => r.Symbol is not null)
                .Select(r => r.Symbol!)
                .ToList();

            if (symbols.Count == 0) return;

            var rawGroups = symbols.GroupBy(keySelector, comparer);

            var groups = ascending
                ? rawGroups.OrderBy(g => g.Key).ToList()
                : rawGroups.OrderByDescending(g => g.Key).ToList();

            var emitter = new CollectionEmitter(spc);

            foreach (var group in groups)
            {
                try
                {
                    action(group.Key, group.ToList(), emitter);
                }
                catch (Exception ex)
                {
                    emitter.ReportError("FLUENTGEN501", $"Generator failed for group '{group.Key}'", ex.ToString());
                }
            }
        });
    }
}
