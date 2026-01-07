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
    /// Gets the diagnostic options for this generator.
    /// Override to customize diagnostic ID prefix, category, and verbosity.
    /// </summary>
    protected virtual DiagnosticOptions DiagnosticOptions => DiagnosticOptions.Default;

    /// <summary>
    /// Gets the diagnostic logger for this generator.
    /// Use <see cref="DiagnosticLogger.For"/> to create scoped loggers in callbacks.
    /// </summary>
    protected DiagnosticLogger Log { get; private set; } = null!;

    /// <summary>
    /// Called by Roslyn to initialize the generator.
    /// </summary>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        Log = new DiagnosticLogger(DiagnosticOptions);
        var generatorContext = new GeneratorContext(context, FileNaming, Log);
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
        DiagnosticLogger log)
    {
        _context = context;
        FileNaming = fileNaming;
        Log = log;
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
    /// Gets the diagnostic logger for reporting errors, warnings, and info.
    /// </summary>
    public DiagnosticLogger Log { get; }

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
    /// Adds source with standard normalization (auto-generated header) and optional auto-logging.
    /// </summary>
    internal void AddSource(SourceProductionContext spc, string hintName, string source, INamedTypeSymbol? forType = null)
    {
        var normalizedSource = NormalizeSource(source);
        spc.AddSource(hintName, SourceText.From(normalizedSource, Encoding.UTF8));

        // Auto-log file generation when verbose
        var log = Log.For(spc);
        log.Info(forType?.Locations.FirstOrDefault(), 0, "Generated {FileName} for {TypeName}", hintName, forType?.Name ?? "aggregate");
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
    /// Reports an exception as an error diagnostic.
    /// </summary>
    internal void ReportException(SourceProductionContext spc, string context, Exception ex, Location? location = null)
    {
        var log = Log.For(spc);
        log.Error(location, 1, "Generation failed for {Context}: {Message}", context, ex.Message);
    }

    static string NormalizeSource(string source) => SymbolExtensions.NormalizeSource(source);
}
