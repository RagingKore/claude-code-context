using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace FluentSourceGen;

/// <summary>
/// Base class for fluent source generators.
/// Inherit from this class and override <see cref="Configure"/> to define your generation logic.
/// </summary>
public abstract class FluentGenerator : IIncrementalGenerator
{
    /// <summary>
    /// Gets the file naming options for this generator.
    /// Override to customize how generated files are named.
    /// </summary>
    protected virtual FileNamingOptions FileNaming => FileNamingOptions.Default;

    /// <summary>
    /// Gets the diagnostic ID prefix for this generator (e.g., "FSG").
    /// Override to use a custom prefix for your generator's diagnostics.
    /// </summary>
    protected virtual string DiagnosticIdPrefix => "FSG";

    /// <summary>
    /// Called by Roslyn to initialize the generator.
    /// </summary>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var generatorContext = new GeneratorContext(context, FileNaming, DiagnosticIdPrefix);
        Configure(generatorContext);
        generatorContext.ExecuteAllRegistrations();
    }

    /// <summary>
    /// Override this method to configure your source generation logic.
    /// </summary>
    /// <param name="context">The generator context providing access to type queries.</param>
    protected abstract void Configure(GeneratorContext context);
}

/// <summary>
/// Context for configuring fluent source generators.
/// Provides type queries and source emission methods.
/// </summary>
public sealed class GeneratorContext
{
    readonly IncrementalGeneratorInitializationContext _context;
    readonly List<Action> _registrations = [];

    internal GeneratorContext(
        IncrementalGeneratorInitializationContext context,
        FileNamingOptions fileNaming,
        string diagnosticIdPrefix)
    {
        _context = context;
        FileNaming = fileNaming;
        Diagnostics = new DiagnosticReporter(diagnosticIdPrefix);
    }

    /// <summary>
    /// Gets the underlying Roslyn incremental generator context.
    /// </summary>
    public IncrementalGeneratorInitializationContext RoslynContext => _context;

    /// <summary>
    /// Gets the file naming options configured for this generator.
    /// </summary>
    public FileNamingOptions FileNaming { get; }

    /// <summary>
    /// Gets the diagnostic reporter for reporting errors, warnings, and info.
    /// </summary>
    public DiagnosticReporter Diagnostics { get; }

    /// <summary>
    /// Starts a fluent query to find and process types.
    /// Chain filter methods and call Generate() to emit source code.
    /// </summary>
    public TypeQuery Types => new(_context.SyntaxProvider, this);

    /// <summary>
    /// Registers a post-initialization output (e.g., marker attributes).
    /// </summary>
    /// <param name="hintName">The hint name for the generated file.</param>
    /// <param name="source">The source code to emit.</param>
    public void AddPostInitializationOutput(string hintName, string source)
    {
        _context.RegisterPostInitializationOutput(ctx =>
            ctx.AddSource(hintName, source));
    }

    /// <summary>
    /// Enqueues a registration action to be executed after Configure() completes.
    /// This is called internally by query Generate() methods.
    /// </summary>
    internal void EnqueueRegistration(Action registration)
    {
        _registrations.Add(registration);
    }

    /// <summary>
    /// Executes all queued registrations.
    /// Called by FluentGenerator after Configure() completes.
    /// </summary>
    internal void ExecuteAllRegistrations()
    {
        foreach (var registration in _registrations)
        {
            registration();
        }
    }

    #region Internal Registration Helpers

    internal void RegisterTypeQueryOutput(
        IncrementalValuesProvider<TypeQuery.QueryResult> provider,
        Func<INamedTypeSymbol, string?> generator,
        string? suffix)
    {
        var fileNaming = FileNaming;
        var diagnostics = Diagnostics;

        _context.RegisterSourceOutput(provider, (spc, result) =>
        {
            if (result.Symbol is null) return;

            try
            {
                var source = generator(result.Symbol);
                if (source is null) return;

                var hintName = SourceGeneratorFileNaming.GetHintName(result.Symbol, fileNaming);
                if (suffix is not null)
                    hintName = hintName.Replace(".g.cs", $"{suffix}.g.cs");

                var normalizedSource = NormalizeSource(source);
                spc.AddSource(hintName, SourceText.From(normalizedSource, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                var location = result.Symbol.Locations.FirstOrDefault() ?? Location.None;
                spc.ReportDiagnostic(Diagnostic.Create(
                    diagnostics.UnhandledException,
                    location,
                    result.Symbol.Name,
                    ex.Message));
            }
        });
    }

    internal void RegisterTypeQueryWithAttributeOutput(
        IncrementalValuesProvider<TypeQuery.QueryResult> provider,
        Func<INamedTypeSymbol, AttributeMatch, string?> generator,
        string? suffix)
    {
        var fileNaming = FileNaming;
        var diagnostics = Diagnostics;

        _context.RegisterSourceOutput(provider, (spc, result) =>
        {
            if (result.Symbol is null || result.Attributes.Count == 0) return;

            try
            {
                var source = generator(result.Symbol, new AttributeMatch(result.Attributes[0]));
                if (source is null) return;

                var hintName = SourceGeneratorFileNaming.GetHintName(result.Symbol, fileNaming);
                if (suffix is not null)
                    hintName = hintName.Replace(".g.cs", $"{suffix}.g.cs");

                var normalizedSource = NormalizeSource(source);
                spc.AddSource(hintName, SourceText.From(normalizedSource, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                var location = result.Symbol.Locations.FirstOrDefault() ?? Location.None;
                spc.ReportDiagnostic(Diagnostic.Create(
                    diagnostics.UnhandledException,
                    location,
                    result.Symbol.Name,
                    ex.Message));
            }
        });
    }

    internal void RegisterTypeQueryWithInterfaceOutput(
        IncrementalValuesProvider<TypeQuery.QueryResult> provider,
        Func<INamedTypeSymbol, InterfaceMatch, string?> generator,
        string? suffix)
    {
        var fileNaming = FileNaming;
        var diagnostics = Diagnostics;

        _context.RegisterSourceOutput(provider, (spc, result) =>
        {
            if (result.Symbol is null || result.Interfaces.Count == 0) return;

            try
            {
                var source = generator(result.Symbol, new InterfaceMatch(result.Interfaces[0]));
                if (source is null) return;

                var hintName = SourceGeneratorFileNaming.GetHintName(result.Symbol, fileNaming);
                if (suffix is not null)
                    hintName = hintName.Replace(".g.cs", $"{suffix}.g.cs");

                var normalizedSource = NormalizeSource(source);
                spc.AddSource(hintName, SourceText.From(normalizedSource, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                var location = result.Symbol.Locations.FirstOrDefault() ?? Location.None;
                spc.ReportDiagnostic(Diagnostic.Create(
                    diagnostics.UnhandledException,
                    location,
                    result.Symbol.Name,
                    ex.Message));
            }
        });
    }

    internal void RegisterTypeQueryWithBothOutput(
        IncrementalValuesProvider<TypeQuery.QueryResult> provider,
        Func<INamedTypeSymbol, AttributeMatch, InterfaceMatch, string?> generator,
        string? suffix)
    {
        var fileNaming = FileNaming;
        var diagnostics = Diagnostics;

        _context.RegisterSourceOutput(provider, (spc, result) =>
        {
            if (result.Symbol is null || result.Attributes.Count == 0 || result.Interfaces.Count == 0) return;

            try
            {
                var source = generator(result.Symbol, new AttributeMatch(result.Attributes[0]), new InterfaceMatch(result.Interfaces[0]));
                if (source is null) return;

                var hintName = SourceGeneratorFileNaming.GetHintName(result.Symbol, fileNaming);
                if (suffix is not null)
                    hintName = hintName.Replace(".g.cs", $"{suffix}.g.cs");

                var normalizedSource = NormalizeSource(source);
                spc.AddSource(hintName, SourceText.From(normalizedSource, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                var location = result.Symbol.Locations.FirstOrDefault() ?? Location.None;
                spc.ReportDiagnostic(Diagnostic.Create(
                    diagnostics.UnhandledException,
                    location,
                    result.Symbol.Name,
                    ex.Message));
            }
        });
    }

    internal void RegisterCollectedTypeQueryOutput(
        IncrementalValuesProvider<TypeQuery.QueryResult> provider,
        Func<IReadOnlyList<INamedTypeSymbol>, (string HintName, string Source)?> generator)
    {
        var diagnostics = Diagnostics;

        _context.RegisterSourceOutput(provider.Collect(), (spc, results) =>
        {
            var symbols = results.Where(r => r.Symbol is not null).Select(r => r.Symbol!).ToList();
            if (symbols.Count == 0) return;

            try
            {
                var result = generator(symbols);
                if (result is null) return;

                var normalizedSource = NormalizeSource(result.Value.Source);
                spc.AddSource(result.Value.HintName, SourceText.From(normalizedSource, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    diagnostics.UnhandledException,
                    Location.None,
                    "collection",
                    ex.Message));
            }
        });
    }

    internal void RegisterCollectedTypeQueryWithAttributeOutput(
        IncrementalValuesProvider<TypeQuery.QueryResult> provider,
        Func<IReadOnlyList<(INamedTypeSymbol Symbol, AttributeMatch Attribute)>, (string HintName, string Source)?> generator)
    {
        var diagnostics = Diagnostics;

        _context.RegisterSourceOutput(provider.Collect(), (spc, results) =>
        {
            var items = results
                .Where(r => r.Symbol is not null && r.Attributes.Count > 0)
                .Select(r => (r.Symbol!, new AttributeMatch(r.Attributes[0])))
                .ToList();

            if (items.Count == 0) return;

            try
            {
                var result = generator(items);
                if (result is null) return;

                var normalizedSource = NormalizeSource(result.Value.Source);
                spc.AddSource(result.Value.HintName, SourceText.From(normalizedSource, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    diagnostics.UnhandledException,
                    Location.None,
                    "collection",
                    ex.Message));
            }
        });
    }

    internal void RegisterGroupedTypeQueryOutput<TKey>(
        IncrementalValueProvider<GroupedQueryResult<TKey>> provider,
        Func<TKey, IReadOnlyList<INamedTypeSymbol>, (string HintName, string Source)?> generator) where TKey : notnull
    {
        var diagnostics = Diagnostics;

        _context.RegisterSourceOutput(provider, (spc, groupedResult) =>
        {
            foreach (var group in groupedResult.GetGroups())
            {
                try
                {
                    var result = generator(group.Key, group.Types);
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

    internal void RegisterGroupedTypeQueryWithAttributeOutput<TKey>(
        IncrementalValueProvider<GroupedQueryResult<TKey>> provider,
        Func<TKey, IReadOnlyList<(INamedTypeSymbol Symbol, AttributeMatch Attribute)>, (string HintName, string Source)?> generator) where TKey : notnull
    {
        var diagnostics = Diagnostics;

        _context.RegisterSourceOutput(provider, (spc, groupedResult) =>
        {
            foreach (var group in groupedResult.GetGroups())
            {
                try
                {
                    var result = generator(group.Key, group.TypesWithAttributes);
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

    internal void RegisterProjectedTypeQueryOutput<T>(
        IncrementalValuesProvider<ProjectedItem<T>> provider,
        Func<T, INamedTypeSymbol, string?> generator,
        string? suffix)
    {
        var fileNaming = FileNaming;
        var diagnostics = Diagnostics;

        _context.RegisterSourceOutput(provider, (spc, item) =>
        {
            if (item.Value is null || item.Symbol is null) return;

            try
            {
                var source = generator(item.Value, item.Symbol);
                if (source is null) return;

                var hintName = SourceGeneratorFileNaming.GetHintName(item.Symbol, fileNaming);
                if (suffix is not null)
                    hintName = hintName.Replace(".g.cs", $"{suffix}.g.cs");

                var normalizedSource = NormalizeSource(source);
                spc.AddSource(hintName, SourceText.From(normalizedSource, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                var location = item.Symbol.Locations.FirstOrDefault() ?? Location.None;
                spc.ReportDiagnostic(Diagnostic.Create(
                    diagnostics.UnhandledException,
                    location,
                    item.Symbol.Name,
                    ex.Message));
            }
        });
    }

    internal void RegisterCollectedProjectedOutput<T>(
        IncrementalValueProvider<IReadOnlyList<ProjectedItem<T>>> provider,
        Func<IReadOnlyList<T>, (string HintName, string Source)?> generator)
    {
        var diagnostics = Diagnostics;

        _context.RegisterSourceOutput(provider, (spc, items) =>
        {
            var values = items.Where(i => i.Value is not null).Select(i => i.Value!).ToList();
            if (values.Count == 0) return;

            try
            {
                var result = generator(values);
                if (result is null) return;

                var normalizedSource = NormalizeSource(result.Value.Source);
                spc.AddSource(result.Value.HintName, SourceText.From(normalizedSource, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    diagnostics.UnhandledException,
                    Location.None,
                    "collection",
                    ex.Message));
            }
        });
    }

    internal void RegisterCollectedProjectedWithSymbolOutput<T>(
        IncrementalValueProvider<IReadOnlyList<ProjectedItem<T>>> provider,
        Func<IReadOnlyList<(T Value, INamedTypeSymbol Symbol)>, (string HintName, string Source)?> generator)
    {
        var diagnostics = Diagnostics;

        _context.RegisterSourceOutput(provider, (spc, items) =>
        {
            var pairs = items
                .Where(i => i.Value is not null && i.Symbol is not null)
                .Select(i => (i.Value!, i.Symbol!))
                .ToList();

            if (pairs.Count == 0) return;

            try
            {
                var result = generator(pairs);
                if (result is null) return;

                var normalizedSource = NormalizeSource(result.Value.Source);
                spc.AddSource(result.Value.HintName, SourceText.From(normalizedSource, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    diagnostics.UnhandledException,
                    Location.None,
                    "collection",
                    ex.Message));
            }
        });
    }

    internal void RegisterProjectedGroupedOutput<TKey, T>(
        IncrementalValueProvider<ProjectedGroupedResult<TKey, T>> provider,
        Func<TKey, IReadOnlyList<T>, (string HintName, string Source)?> generator) where TKey : notnull
    {
        var diagnostics = Diagnostics;

        _context.RegisterSourceOutput(provider, (spc, groupedResult) =>
        {
            foreach (var group in groupedResult.GetGroups())
            {
                try
                {
                    var result = generator(group.Key, group.Values);
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

    internal void RegisterFlattenedTypeQueryOutput<T>(
        IncrementalValueProvider<IReadOnlyList<FlattenedItem<T>>> provider,
        Func<IReadOnlyList<FlattenedItem<T>>, (string HintName, string Source)?> generator)
    {
        var diagnostics = Diagnostics;

        _context.RegisterSourceOutput(provider, (spc, items) =>
        {
            if (items.Count == 0) return;

            try
            {
                var result = generator(items.ToList());
                if (result is null) return;

                var normalizedSource = NormalizeSource(result.Value.Source);
                spc.AddSource(result.Value.HintName, SourceText.From(normalizedSource, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    diagnostics.UnhandledException,
                    Location.None,
                    "collection",
                    ex.Message));
            }
        });
    }

    #endregion

    #region Helpers

    internal static string NormalizeSource(string source)
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
/// Provides methods for reporting diagnostics during source generation.
/// </summary>
public sealed class DiagnosticReporter
{
    readonly string _idPrefix;

    internal DiagnosticReporter(string idPrefix)
    {
        _idPrefix = idPrefix;
    }

    /// <summary>
    /// Creates an error diagnostic descriptor.
    /// </summary>
    public DiagnosticDescriptor Error(string id, string title, string messageFormat) =>
        new($"{_idPrefix}{id}", title, messageFormat, "FluentSourceGen",
            DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>
    /// Creates a warning diagnostic descriptor.
    /// </summary>
    public DiagnosticDescriptor Warning(string id, string title, string messageFormat) =>
        new($"{_idPrefix}{id}", title, messageFormat, "FluentSourceGen",
            DiagnosticSeverity.Warning, isEnabledByDefault: true);

    /// <summary>
    /// Creates an info diagnostic descriptor.
    /// </summary>
    public DiagnosticDescriptor Info(string id, string title, string messageFormat) =>
        new($"{_idPrefix}{id}", title, messageFormat, "FluentSourceGen",
            DiagnosticSeverity.Info, isEnabledByDefault: true);

    /// <summary>
    /// Creates a diagnostic for an unhandled exception during generation.
    /// </summary>
    internal DiagnosticDescriptor UnhandledException { get; } = new(
        "FSG0001",
        "Source generation failed",
        "An error occurred while generating source for '{0}': {1}",
        "FluentSourceGen",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
