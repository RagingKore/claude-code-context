using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace FluentSourceGen;

/// <summary>
/// Base class for fluent source generators.
/// Inherit from this class and override <see cref="Execute"/> to define your generation logic.
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
        Execute(generatorContext);
        generatorContext.RegisterOutputs();
    }

    /// <summary>
    /// Override this method to define your source generation logic.
    /// </summary>
    protected abstract void Execute(GeneratorContext context);
}

/// <summary>
/// Context for fluent source generators.
/// </summary>
public sealed class GeneratorContext
{
    readonly IncrementalGeneratorInitializationContext _context;
    readonly List<Action> _pendingRegistrations = [];

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
    /// Gets the file naming options configured for this generator.
    /// </summary>
    public FileNamingOptions FileNaming { get; }

    /// <summary>
    /// Gets the diagnostic reporter for reporting errors, warnings, and info.
    /// </summary>
    public DiagnosticReporter Diagnostics { get; }

    /// <summary>
    /// Starts a fluent query to find and process types.
    /// </summary>
    public TypeQuery Types => new(_context, this);

    /// <summary>
    /// Registers a post-initialization output (e.g., marker attributes).
    /// </summary>
    public void AddPostInitializationOutput(string hintName, string source)
    {
        _pendingRegistrations.Add(() =>
            _context.RegisterPostInitializationOutput(ctx =>
                ctx.AddSource(hintName, source)));
    }

    /// <summary>
    /// Adds a pending source output registration.
    /// </summary>
    internal void AddSourceOutput<T>(IncrementalValuesProvider<T> provider, Action<SourceProductionContext, T> action)
    {
        _pendingRegistrations.Add(() => _context.RegisterSourceOutput(provider, action));
    }

    /// <summary>
    /// Adds a pending collected source output registration.
    /// </summary>
    internal void AddSourceOutput<T>(IncrementalValueProvider<System.Collections.Immutable.ImmutableArray<T>> provider, Action<SourceProductionContext, System.Collections.Immutable.ImmutableArray<T>> action)
    {
        _pendingRegistrations.Add(() => _context.RegisterSourceOutput(provider, action));
    }

    /// <summary>
    /// Registers all pending outputs with Roslyn.
    /// Called by FluentGenerator after Execute completes.
    /// </summary>
    internal void RegisterOutputs()
    {
        foreach (var registration in _pendingRegistrations)
        {
            registration();
        }
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
