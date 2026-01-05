using Microsoft.CodeAnalysis;

namespace FluentSourceGen;

/// <summary>
/// Base class for fluent source generators.
/// Inherit from this class and override <see cref="Configure"/> to define your generation logic.
/// </summary>
public abstract class FluentGenerator : IIncrementalGenerator
{
    /// <summary>
    /// Called by Roslyn to initialize the generator.
    /// </summary>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var generatorContext = new GeneratorContext(context);
        Configure(generatorContext);
    }

    /// <summary>
    /// Override this method to configure your source generation logic.
    /// </summary>
    /// <param name="context">The generator context providing access to type queries.</param>
    protected abstract void Configure(GeneratorContext context);
}

/// <summary>
/// Context for configuring fluent source generators.
/// </summary>
public sealed class GeneratorContext
{
    readonly IncrementalGeneratorInitializationContext _context;

    internal GeneratorContext(IncrementalGeneratorInitializationContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Gets the underlying Roslyn incremental generator context.
    /// </summary>
    public IncrementalGeneratorInitializationContext RoslynContext => _context;

    /// <summary>
    /// Starts a fluent query to find and process types.
    /// </summary>
    public TypeQuery Types => new(_context);

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
}
