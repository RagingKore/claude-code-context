using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace FluentSourceGen;

/// <summary>
/// Provides methods for emitting generated source code when processing multiple types together.
/// Used with <see cref="TypeQuery.GenerateAll"/> for generating registries, factories, or aggregate files.
/// </summary>
public sealed class CollectionEmitter
{
    readonly SourceProductionContext _context;

    internal CollectionEmitter(SourceProductionContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Emits source code with the specified hint name.
    /// </summary>
    /// <param name="hintName">The hint name for the generated file (e.g., "ServiceRegistry.g.cs")</param>
    /// <param name="source">The source code to emit</param>
    public void Source(string hintName, string source)
    {
        var normalizedSource = SymbolExtensions.NormalizeSource(source);
        _context.AddSource(hintName, SourceText.From(normalizedSource, Encoding.UTF8));
    }
}
