using Microsoft.CodeAnalysis;

namespace FluentSourceGen;

/// <summary>
/// Represents types grouped by a key selector.
/// Chain filter methods and call Generate() to emit source code.
/// </summary>
/// <typeparam name="TKey">The type of the grouping key.</typeparam>
public sealed class GroupedTypeQuery<TKey> where TKey : notnull
{
    readonly IncrementalValuesProvider<TypeQuery.QueryResult> _provider;
    readonly Func<INamedTypeSymbol, TKey> _keySelector;
    readonly GeneratorContext _context;
    readonly IEqualityComparer<TKey> _comparer;
    readonly Func<TKey, bool>? _groupPredicate;
    readonly bool? _orderAscending;

    internal GroupedTypeQuery(
        IncrementalValuesProvider<TypeQuery.QueryResult> provider,
        Func<INamedTypeSymbol, TKey> keySelector,
        GeneratorContext context,
        IEqualityComparer<TKey>? comparer = null,
        Func<TKey, bool>? groupPredicate = null,
        bool? orderAscending = null)
    {
        _provider = provider;
        _keySelector = keySelector;
        _context = context;
        _comparer = comparer ?? EqualityComparer<TKey>.Default;
        _groupPredicate = groupPredicate;
        _orderAscending = orderAscending;
    }

    /// <summary>
    /// Filter groups by a predicate on the key.
    /// </summary>
    public GroupedTypeQuery<TKey> WhereGroup(Func<TKey, bool> predicate)
    {
        return new GroupedTypeQuery<TKey>(_provider, _keySelector, _context, _comparer, predicate, _orderAscending);
    }

    /// <summary>
    /// Order groups by key ascending.
    /// </summary>
    public GroupedTypeQuery<TKey> OrderByKey()
    {
        return new GroupedTypeQuery<TKey>(_provider, _keySelector, _context, _comparer, _groupPredicate, ascending: true);
    }

    /// <summary>
    /// Order groups by key descending.
    /// </summary>
    public GroupedTypeQuery<TKey> OrderByKeyDescending()
    {
        return new GroupedTypeQuery<TKey>(_provider, _keySelector, _context, _comparer, _groupPredicate, ascending: false);
    }

    #region Generate Methods (Terminal Operations)

    /// <summary>
    /// Generate source code for each group using a batch context.
    /// Access the key via ctx.GetKey&lt;TKey&gt;().
    /// </summary>
    /// <param name="generator">Function that receives a BatchContext and returns hint name and source (or null to skip).</param>
    public void Generate(Func<BatchContext, (string HintName, string Source)?> generator)
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
                    var genCtx = new BatchContext(group.Types, log, group.Key);
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
    /// Builds the query and returns the grouped incremental value provider.
    /// For advanced scenarios only - prefer using Generate() methods.
    /// </summary>
    public IncrementalValueProvider<GroupedQueryResult<TKey>> Build()
    {
        var keySelector = _keySelector;
        var comparer = _comparer;
        var groupPredicate = _groupPredicate;
        var orderAscending = _orderAscending;

        return _provider.Collect().Select((results, _) =>
        {
            var symbols = results
                .Where(r => r.Symbol is not null)
                .Select(r => r.Symbol!)
                .ToList();

            return new GroupedQueryResult<TKey>(symbols, keySelector, comparer, groupPredicate, orderAscending);
        });
    }

    #endregion
}

/// <summary>
/// Result of a grouped type query, containing all symbols and grouping logic.
/// </summary>
public readonly struct GroupedQueryResult<TKey> where TKey : notnull
{
    readonly IReadOnlyList<INamedTypeSymbol> _symbols;
    readonly Func<INamedTypeSymbol, TKey> _keySelector;
    readonly IEqualityComparer<TKey> _comparer;
    readonly Func<TKey, bool>? _groupPredicate;
    readonly bool? _orderAscending;

    internal GroupedQueryResult(
        IReadOnlyList<INamedTypeSymbol> symbols,
        Func<INamedTypeSymbol, TKey> keySelector,
        IEqualityComparer<TKey> comparer,
        Func<TKey, bool>? groupPredicate,
        bool? orderAscending)
    {
        _symbols = symbols;
        _keySelector = keySelector;
        _comparer = comparer;
        _groupPredicate = groupPredicate;
        _orderAscending = orderAscending;
    }

    /// <summary>
    /// Gets all groups from the query result.
    /// </summary>
    public IEnumerable<TypeGroup<TKey>> GetGroups()
    {
        if (_symbols.Count == 0)
            yield break;

        var rawGroups = _symbols.GroupBy(_keySelector, _comparer);

        IEnumerable<IGrouping<TKey, INamedTypeSymbol>> groups = rawGroups;

        if (_groupPredicate is not null)
            groups = groups.Where(g => _groupPredicate(g.Key));

        if (_orderAscending.HasValue)
        {
            groups = _orderAscending.Value
                ? groups.OrderBy(g => g.Key)
                : groups.OrderByDescending(g => g.Key);
        }

        foreach (var group in groups)
        {
            yield return new TypeGroup<TKey>(group.Key, group.ToList());
        }
    }
}

/// <summary>
/// Represents a group of types with a common key.
/// </summary>
public readonly struct TypeGroup<TKey>
{
    /// <summary>
    /// The grouping key.
    /// </summary>
    public TKey Key { get; }

    /// <summary>
    /// The types in this group.
    /// </summary>
    public IReadOnlyList<INamedTypeSymbol> Types { get; }

    internal TypeGroup(TKey key, IReadOnlyList<INamedTypeSymbol> types)
    {
        Key = key;
        Types = types;
    }
}
