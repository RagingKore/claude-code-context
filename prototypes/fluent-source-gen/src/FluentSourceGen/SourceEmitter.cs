using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace FluentSourceGen;

/// <summary>
/// Provides methods for emitting generated source code.
/// </summary>
public sealed class SourceEmitter
{
    readonly SourceProductionContext _context;
    readonly INamedTypeSymbol _typeSymbol;

    internal SourceEmitter(SourceProductionContext context, INamedTypeSymbol typeSymbol)
    {
        _context = context;
        _typeSymbol = typeSymbol;
    }

    /// <summary>
    /// Gets the type symbol being processed.
    /// </summary>
    public INamedTypeSymbol Type => _typeSymbol;

    /// <summary>
    /// Emits source code with a simple hint name.
    /// </summary>
    public void Source(string hintName, string source)
    {
        var normalizedSource = SymbolExtensions.NormalizeSource(source);
        _context.AddSource(hintName, SourceText.From(normalizedSource, Encoding.UTF8));
    }

    /// <summary>
    /// Emits source code with configurable file naming options.
    /// </summary>
    public void Source(FileNamingOptions options, string source, params ITypeSymbol[] typeArgsForHash)
    {
        var hintName = SourceGeneratorFileNaming.GetHintName(_typeSymbol, options, typeArgsForHash);
        Source(hintName, source);
    }
}
