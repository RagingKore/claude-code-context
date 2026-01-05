using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace FluentSourceGen;

/// <summary>
/// Represents types grouped by a key selector.
/// Enables processing types in groups for generating aggregate outputs per group.
/// </summary>
public sealed class GroupedTypeQuery<TKey> where TKey : notnull
{
    readonly IncrementalGeneratorInitializationContext _roslynContext;
    readonly IncrementalValuesProvider<TypeQuery.QueryResult> _provider;
    readonly Func<INamedTypeSymbol, TKey> _keySelector;
    readonly IEqualityComparer<TKey> _comparer;
    readonly GeneratorContext _generatorContext;

    internal GroupedTypeQuery(
        IncrementalGeneratorInitializationContext roslynContext,
        IncrementalValuesProvider<TypeQuery.QueryResult> provider,
        Func<INamedTypeSymbol, TKey> keySelector,
        GeneratorContext generatorContext)
    {
        _roslynContext = roslynContext;
        _provider = provider;
        _keySelector = keySelector;
        _comparer = EqualityComparer<TKey>.Default;
        _generatorContext = generatorContext;
    }

    internal GroupedTypeQuery(
        IncrementalGeneratorInitializationContext roslynContext,
        IncrementalValuesProvider<TypeQuery.QueryResult> provider,
        Func<INamedTypeSymbol, TKey> keySelector,
        IEqualityComparer<TKey> comparer,
        GeneratorContext generatorContext)
    {
        _roslynContext = roslynContext;
        _provider = provider;
        _keySelector = keySelector;
        _comparer = comparer;
        _generatorContext = generatorContext;
    }

    /// <summary>
    /// Generate source code for each group.
    /// Return (hintName, source) tuple, or null to skip generation for that group.
    /// </summary>
    public void Generate(Func<TKey, IReadOnlyList<INamedTypeSymbol>, (string HintName, string Source)?> generator)
    {
        var keySelector = _keySelector;
        var comparer = _comparer;
        var diagnostics = _generatorContext.Diagnostics;

        var collected = _provider.Collect();

        _generatorContext.AddSourceOutput(collected, (spc, results) =>
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
        var diagnostics = _generatorContext.Diagnostics;

        var collected = _provider.Collect();

        _generatorContext.AddSourceOutput(collected, (spc, results) =>
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
        var diagnostics = _generatorContext.Diagnostics;

        var collected = _provider.Collect();

        _generatorContext.AddSourceOutput(collected, (spc, results) =>
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

    /// <summary>
    /// Filter groups by a predicate on the key.
    /// </summary>
    public GroupedTypeQuery<TKey> WhereGroup(Func<TKey, bool> predicate)
    {
        return new FilteredGroupedTypeQuery<TKey>(_roslynContext, _provider, _keySelector, _comparer, _generatorContext, predicate);
    }

    /// <summary>
    /// Order groups by key.
    /// </summary>
    public OrderedGroupedTypeQuery<TKey> OrderByKey()
    {
        return new OrderedGroupedTypeQuery<TKey>(_roslynContext, _provider, _keySelector, _comparer, _generatorContext, ascending: true);
    }

    /// <summary>
    /// Order groups by key descending.
    /// </summary>
    public OrderedGroupedTypeQuery<TKey> OrderByKeyDescending()
    {
        return new OrderedGroupedTypeQuery<TKey>(_roslynContext, _provider, _keySelector, _comparer, _generatorContext, ascending: false);
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
/// A grouped query with a filter on groups.
/// </summary>
internal sealed class FilteredGroupedTypeQuery<TKey> : GroupedTypeQuery<TKey> where TKey : notnull
{
    readonly Func<TKey, bool> _groupPredicate;
    readonly IncrementalGeneratorInitializationContext _roslynContext;
    readonly IncrementalValuesProvider<TypeQuery.QueryResult> _provider;
    readonly Func<INamedTypeSymbol, TKey> _keySelector;
    readonly IEqualityComparer<TKey> _comparer;
    readonly GeneratorContext _generatorContext;

    internal FilteredGroupedTypeQuery(
        IncrementalGeneratorInitializationContext roslynContext,
        IncrementalValuesProvider<TypeQuery.QueryResult> provider,
        Func<INamedTypeSymbol, TKey> keySelector,
        IEqualityComparer<TKey> comparer,
        GeneratorContext generatorContext,
        Func<TKey, bool> groupPredicate)
        : base(roslynContext, provider, keySelector, comparer, generatorContext)
    {
        _roslynContext = roslynContext;
        _provider = provider;
        _keySelector = keySelector;
        _comparer = comparer;
        _generatorContext = generatorContext;
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
        var diagnostics = _generatorContext.Diagnostics;

        var collected = _provider.Collect();

        _generatorContext.AddSourceOutput(collected, (spc, results) =>
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
    readonly IncrementalGeneratorInitializationContext _roslynContext;
    readonly IncrementalValuesProvider<TypeQuery.QueryResult> _provider;
    readonly Func<INamedTypeSymbol, TKey> _keySelector;
    readonly IEqualityComparer<TKey> _comparer;
    readonly GeneratorContext _generatorContext;
    readonly bool _ascending;

    internal OrderedGroupedTypeQuery(
        IncrementalGeneratorInitializationContext roslynContext,
        IncrementalValuesProvider<TypeQuery.QueryResult> provider,
        Func<INamedTypeSymbol, TKey> keySelector,
        IEqualityComparer<TKey> comparer,
        GeneratorContext generatorContext,
        bool ascending)
    {
        _roslynContext = roslynContext;
        _provider = provider;
        _keySelector = keySelector;
        _comparer = comparer;
        _generatorContext = generatorContext;
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
        var diagnostics = _generatorContext.Diagnostics;

        var collected = _provider.Collect();

        _generatorContext.AddSourceOutput(collected, (spc, results) =>
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
