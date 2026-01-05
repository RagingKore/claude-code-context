using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

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
    readonly FileNamingOptions _fileNaming;
    readonly DiagnosticReporter _diagnostics;

    internal GroupedTypeQuery(
        IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<TypeQuery.QueryResult> provider,
        Func<INamedTypeSymbol, TKey> keySelector,
        FileNamingOptions fileNaming,
        DiagnosticReporter diagnostics)
    {
        _context = context;
        _provider = provider;
        _keySelector = keySelector;
        _comparer = EqualityComparer<TKey>.Default;
        _fileNaming = fileNaming;
        _diagnostics = diagnostics;
    }

    internal GroupedTypeQuery(
        IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<TypeQuery.QueryResult> provider,
        Func<INamedTypeSymbol, TKey> keySelector,
        IEqualityComparer<TKey> comparer,
        FileNamingOptions fileNaming,
        DiagnosticReporter diagnostics)
    {
        _context = context;
        _provider = provider;
        _keySelector = keySelector;
        _comparer = comparer;
        _fileNaming = fileNaming;
        _diagnostics = diagnostics;
    }

    #region Generate Methods

    /// <summary>
    /// Generate source code for each group.
    /// Return (hintName, source) tuple, or null to skip generation for that group.
    /// </summary>
    /// <param name="generator">Function that receives the group key, types, and returns (hintName, source) or null</param>
    public void Generate(Func<TKey, IReadOnlyList<INamedTypeSymbol>, (string HintName, string Source)?> generator)
    {
        var keySelector = _keySelector;
        var comparer = _comparer;
        var diagnostics = _diagnostics;

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

            foreach (var group in groups)
            {
                try
                {
                    var result = generator(group.Key, group.ToList());
                    if (result is null) continue;

                    var normalizedSource = NormalizeSource(result.Value.Source);
                    spc.AddSource(result.Value.HintName, SourceText.From(normalizedSource, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        diagnostics.UnhandledException,
                        Location.None,
                        $"group '{group.Key}'",
                        ex.Message));
                }
            }
        });
    }

    /// <summary>
    /// Generate source code for each group with attribute data.
    /// </summary>
    public void Generate(Func<TKey, IReadOnlyList<(INamedTypeSymbol Symbol, AttributeMatch Attribute)>, (string HintName, string Source)?> generator)
    {
        var keySelector = _keySelector;
        var comparer = _comparer;
        var diagnostics = _diagnostics;

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

            foreach (var group in groups)
            {
                try
                {
                    var result = generator(group.Key, group.ToList());
                    if (result is null) continue;

                    var normalizedSource = NormalizeSource(result.Value.Source);
                    spc.AddSource(result.Value.HintName, SourceText.From(normalizedSource, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        diagnostics.UnhandledException,
                        Location.None,
                        $"group '{group.Key}'",
                        ex.Message));
                }
            }
        });
    }

    /// <summary>
    /// Generate source code for each group with interface data.
    /// </summary>
    public void Generate(Func<TKey, IReadOnlyList<(INamedTypeSymbol Symbol, InterfaceMatch Interface)>, (string HintName, string Source)?> generator)
    {
        var keySelector = _keySelector;
        var comparer = _comparer;
        var diagnostics = _diagnostics;

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

            foreach (var group in groups)
            {
                try
                {
                    var result = generator(group.Key, group.ToList());
                    if (result is null) continue;

                    var normalizedSource = NormalizeSource(result.Value.Source);
                    spc.AddSource(result.Value.HintName, SourceText.From(normalizedSource, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        diagnostics.UnhandledException,
                        Location.None,
                        $"group '{group.Key}'",
                        ex.Message));
                }
            }
        });
    }

    #endregion

    #region ForEachGroup Methods (Legacy)

    /// <summary>
    /// Process each group of types. Each group shares the same key value.
    /// </summary>
    /// <param name="action">Action to execute for each group with key, types, and emitter.</param>
    [Obsolete("Use Generate instead")]
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
    [Obsolete("Use Generate instead")]
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
    [Obsolete("Use Generate instead")]
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

    #endregion

    #region Filtering and Ordering

    /// <summary>
    /// Filter groups by a predicate on the key.
    /// </summary>
    public GroupedTypeQuery<TKey> WhereGroup(Func<TKey, bool> predicate)
    {
        // Return a new grouped query that filters groups
        return new FilteredGroupedTypeQuery<TKey>(_context, _provider, _keySelector, _comparer, _fileNaming, _diagnostics, predicate);
    }

    /// <summary>
    /// Order groups by key.
    /// </summary>
    public OrderedGroupedTypeQuery<TKey> OrderByKey()
    {
        return new OrderedGroupedTypeQuery<TKey>(_context, _provider, _keySelector, _comparer, _fileNaming, _diagnostics, ascending: true);
    }

    /// <summary>
    /// Order groups by key descending.
    /// </summary>
    public OrderedGroupedTypeQuery<TKey> OrderByKeyDescending()
    {
        return new OrderedGroupedTypeQuery<TKey>(_context, _provider, _keySelector, _comparer, _fileNaming, _diagnostics, ascending: false);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Normalizes source code by adding standard headers if not present.
    /// </summary>
    static string NormalizeSource(string source)
    {
        if (!source.TrimStart().StartsWith("//"))
        {
            return $"""
                // <auto-generated />
                // This file was auto-generated by FluentSourceGen.
                // Changes to this file may be lost when the file is regenerated.

                #nullable enable

                {source}
                """;
        }

        return source;
    }

    #endregion
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
    readonly DiagnosticReporter _diagnostics;

    internal FilteredGroupedTypeQuery(
        IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<TypeQuery.QueryResult> provider,
        Func<INamedTypeSymbol, TKey> keySelector,
        IEqualityComparer<TKey> comparer,
        FileNamingOptions fileNaming,
        DiagnosticReporter diagnostics,
        Func<TKey, bool> groupPredicate)
        : base(context, provider, keySelector, comparer, fileNaming, diagnostics)
    {
        _context = context;
        _provider = provider;
        _keySelector = keySelector;
        _comparer = comparer;
        _diagnostics = diagnostics;
        _groupPredicate = groupPredicate;
    }

    /// <summary>
    /// Generate source code for each group that passes the filter.
    /// </summary>
    public new void Generate(Func<TKey, IReadOnlyList<INamedTypeSymbol>, (string HintName, string Source)?> generator)
    {
        var keySelector = _keySelector;
        var comparer = _comparer;
        var groupPredicate = _groupPredicate;
        var diagnostics = _diagnostics;

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

            foreach (var group in groups)
            {
                try
                {
                    var result = generator(group.Key, group.ToList());
                    if (result is null) continue;

                    var normalizedSource = NormalizeSource(result.Value.Source);
                    spc.AddSource(result.Value.HintName, SourceText.From(normalizedSource, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        diagnostics.UnhandledException,
                        Location.None,
                        $"group '{group.Key}'",
                        ex.Message));
                }
            }
        });
    }

    /// <summary>
    /// Process each group that passes the filter.
    /// </summary>
    [Obsolete("Use Generate instead")]
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

    static string NormalizeSource(string source)
    {
        if (!source.TrimStart().StartsWith("//"))
        {
            return $"""
                // <auto-generated />
                // This file was auto-generated by FluentSourceGen.
                // Changes to this file may be lost when the file is regenerated.

                #nullable enable

                {source}
                """;
        }

        return source;
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
    readonly DiagnosticReporter _diagnostics;
    readonly bool _ascending;

    internal OrderedGroupedTypeQuery(
        IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<TypeQuery.QueryResult> provider,
        Func<INamedTypeSymbol, TKey> keySelector,
        IEqualityComparer<TKey> comparer,
        FileNamingOptions fileNaming,
        DiagnosticReporter diagnostics,
        bool ascending)
    {
        _context = context;
        _provider = provider;
        _keySelector = keySelector;
        _comparer = comparer;
        _diagnostics = diagnostics;
        _ascending = ascending;
    }

    /// <summary>
    /// Generate source code for each group in order.
    /// </summary>
    public void Generate(Func<TKey, IReadOnlyList<INamedTypeSymbol>, (string HintName, string Source)?> generator)
    {
        var keySelector = _keySelector;
        var comparer = _comparer;
        var ascending = _ascending;
        var diagnostics = _diagnostics;

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

            foreach (var group in groups)
            {
                try
                {
                    var result = generator(group.Key, group.ToList());
                    if (result is null) continue;

                    var normalizedSource = NormalizeSource(result.Value.Source);
                    spc.AddSource(result.Value.HintName, SourceText.From(normalizedSource, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        diagnostics.UnhandledException,
                        Location.None,
                        $"group '{group.Key}'",
                        ex.Message));
                }
            }
        });
    }

    /// <summary>
    /// Process each group in order.
    /// </summary>
    [Obsolete("Use Generate instead")]
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

    static string NormalizeSource(string source)
    {
        if (!source.TrimStart().StartsWith("//"))
        {
            return $"""
                // <auto-generated />
                // This file was auto-generated by FluentSourceGen.
                // Changes to this file may be lost when the file is regenerated.

                #nullable enable

                {source}
                """;
        }

        return source;
    }
}
