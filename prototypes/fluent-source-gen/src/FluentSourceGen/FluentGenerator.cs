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
    /// </summary>
    internal void EnqueueRegistration(Action registration)
    {
        _registrations.Add(registration);
    }

    /// <summary>
    /// Executes all queued registrations.
    /// </summary>
    internal void ExecuteAllRegistrations()
    {
        foreach (var registration in _registrations)
        {
            registration();
        }
    }

    /// <summary>
    /// Adds source with standard normalization (auto-generated header).
    /// </summary>
    internal void AddSource(SourceProductionContext spc, string hintName, string source)
    {
        var normalizedSource = NormalizeSource(source);
        spc.AddSource(hintName, SourceText.From(normalizedSource, Encoding.UTF8));
    }

    /// <summary>
    /// Gets the hint name for a type symbol.
    /// </summary>
    internal string GetHintName(INamedTypeSymbol symbol, string? suffix = null)
    {
        var hintName = SourceGeneratorFileNaming.GetHintName(symbol, FileNaming);
        if (suffix is not null)
            hintName = hintName.Replace(".g.cs", $"{suffix}.g.cs");
        return hintName;
    }

    /// <summary>
    /// Reports an exception as a diagnostic.
    /// </summary>
    internal void ReportException(SourceProductionContext spc, string context, Exception ex, Location? location = null)
    {
        spc.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.UnhandledException,
            location ?? Location.None,
            context,
            ex.Message));
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
    /// Diagnostic for unhandled exceptions during generation.
    /// </summary>
    internal DiagnosticDescriptor UnhandledException { get; } = new(
        "FSG0001",
        "Source generation failed",
        "An error occurred while generating source for '{0}': {1}",
        "FluentSourceGen",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
