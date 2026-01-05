using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace FluentSourceGen;

/// <summary>
/// Provides methods for emitting generated source code and diagnostics.
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

    #region Source Emission

    /// <summary>
    /// Emits source code with a simple hint name.
    /// </summary>
    /// <param name="hintName">The hint name for the generated file (e.g., "MyType.g.cs")</param>
    /// <param name="source">The source code to emit</param>
    public void Source(string hintName, string source)
    {
        var normalizedSource = NormalizeSource(source);
        _context.AddSource(hintName, SourceText.From(normalizedSource, Encoding.UTF8));
    }

    /// <summary>
    /// Emits source code with automatic hint name generation based on the type.
    /// </summary>
    /// <param name="suffix">Optional suffix to append to the type name (e.g., ".Operators")</param>
    /// <param name="source">The source code to emit</param>
    public void Source(string source, string? suffix = null)
    {
        var hintName = GenerateHintName(_typeSymbol, suffix);
        Source(hintName, source);
    }

    /// <summary>
    /// Emits source code with configurable file naming options.
    /// </summary>
    /// <param name="options">File naming configuration</param>
    /// <param name="source">The source code to emit</param>
    /// <param name="typeArgsForHash">Optional type arguments to include in hash for uniqueness</param>
    public void Source(FileNamingOptions options, string source, params ITypeSymbol[] typeArgsForHash)
    {
        var hintName = GenerateHintName(_typeSymbol, options, typeArgsForHash);
        Source(hintName, source);
    }

    #endregion

    #region Hint Name Generation

    /// <summary>
    /// Generates a hint name for the type with optional suffix.
    /// </summary>
    public static string GenerateHintName(INamedTypeSymbol typeSymbol, string? suffix = null)
    {
        var typeName = SanitizeFileName(typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", ""));

        return suffix is not null
            ? $"{typeName}{suffix}.g.cs"
            : $"{typeName}.g.cs";
    }

    /// <summary>
    /// Generates a deterministic hint name with configurable options.
    /// </summary>
    public static string GenerateHintName(
        INamedTypeSymbol typeSymbol,
        FileNamingOptions options,
        IEnumerable<ITypeSymbol>? typeArgsForHash = null)
    {
        var hashInput = GenerateHashInput(typeSymbol, typeArgsForHash?.ToList() ?? []);
        var hash = ComputeStableHash(hashInput);
        var fileName = BuildFileName(typeSymbol.Name, hash, options);
        var directoryParts = BuildDirectoryParts(typeSymbol, options);

        return CombinePathParts(directoryParts, fileName, options);
    }

    static string GenerateHashInput(INamedTypeSymbol typeSymbol, List<ITypeSymbol> typeArgs)
    {
        if (typeArgs.Count > 0)
            return string.Join(",", typeArgs.Select(t => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));

        if (typeSymbol is { IsGenericType: true, TypeArguments.Length: > 0 })
            return string.Join(",", typeSymbol.TypeArguments.Select(t => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));

        return typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    static string BuildFileName(string typeName, string hash, FileNamingOptions options)
    {
        var name = options.LowercasePath ? typeName.ToLowerInvariant() : typeName;
        return $"{name}_{hash}.g.cs";
    }

    static List<string> BuildDirectoryParts(INamedTypeSymbol typeSymbol, FileNamingOptions options)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(options.Prefix) && options.UseFoldersForPrefix)
        {
            var prefix = ApplyCasing(options.Prefix!, options.LowercasePath);
            parts.Add(prefix);
        }

        if (options.UseFoldersForNamespace)
        {
            var namespaceName = typeSymbol.ContainingNamespace.IsGlobalNamespace
                ? "Global"
                : typeSymbol.ContainingNamespace.ToDisplayString();
            var casedNamespace = ApplyCasing(namespaceName, options.LowercasePath);
            parts.Add(casedNamespace);
        }

        return parts;
    }

    static string CombinePathParts(List<string> directoryParts, string fileName, FileNamingOptions options)
    {
        if (directoryParts.Count > 0)
        {
            var directory = string.Join("/", directoryParts);
            return $"{directory}/{fileName}";
        }

        if (!string.IsNullOrEmpty(options.Prefix) && !options.UseFoldersForPrefix)
        {
            var prefix = ApplyCasing(options.Prefix!, options.LowercasePath);
            return $"{prefix}_{fileName}";
        }

        return fileName;
    }

    static string ApplyCasing(string value, bool lowercase) =>
        lowercase ? value.ToLowerInvariant() : value;

    static string ComputeStableHash(string input)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
        return BitConverter.ToString(hash).Replace("-", "").Substring(0, 8);
    }

    static string SanitizeFileName(string input) =>
        input
            .Replace('.', '_')
            .Replace('<', '_')
            .Replace('>', '_')
            .Replace(',', '_')
            .Replace(' ', '_')
            .Replace('+', '_');

    #endregion

    #region Source Helpers

    static string NormalizeSource(string source)
    {
        // Ensure the source has the auto-generated header if not present
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

    #region Diagnostics

    /// <summary>
    /// Reports an informational diagnostic.
    /// </summary>
    public void ReportInfo(string id, string title, string message)
    {
        var descriptor = new DiagnosticDescriptor(
            id, title, message, "FluentSourceGen",
            DiagnosticSeverity.Info, isEnabledByDefault: true);
        _context.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None));
    }

    /// <summary>
    /// Reports a warning diagnostic.
    /// </summary>
    public void ReportWarning(string id, string title, string message)
    {
        var descriptor = new DiagnosticDescriptor(
            id, title, message, "FluentSourceGen",
            DiagnosticSeverity.Warning, isEnabledByDefault: true);
        _context.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None));
    }

    /// <summary>
    /// Reports an error diagnostic.
    /// </summary>
    public void ReportError(string id, string title, string message)
    {
        var descriptor = new DiagnosticDescriptor(
            id, title, message, "FluentSourceGen",
            DiagnosticSeverity.Error, isEnabledByDefault: true);
        _context.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None));
    }

    /// <summary>
    /// Reports a diagnostic with the type's location.
    /// </summary>
    public void ReportDiagnostic(DiagnosticSeverity severity, string id, string title, string message)
    {
        var location = _typeSymbol.Locations.FirstOrDefault() ?? Location.None;
        var descriptor = new DiagnosticDescriptor(
            id, title, message, "FluentSourceGen",
            severity, isEnabledByDefault: true);
        _context.ReportDiagnostic(Diagnostic.Create(descriptor, location));
    }

    #endregion
}

/// <summary>
/// Configuration options for generated file naming.
/// </summary>
public sealed class FileNamingOptions
{
    /// <summary>
    /// Gets the default file naming options.
    /// </summary>
    public static FileNamingOptions Default { get; } = new()
    {
        UseFoldersForNamespace = true,
        UseFoldersForPrefix = true,
        LowercasePath = false,
        Prefix = null
    };

    /// <summary>
    /// When true, creates a folder hierarchy for the namespace.
    /// Default: true
    /// </summary>
    public bool UseFoldersForNamespace { get; init; } = true;

    /// <summary>
    /// When true and Prefix is set, the prefix becomes a folder.
    /// Default: true
    /// </summary>
    public bool UseFoldersForPrefix { get; init; } = true;

    /// <summary>
    /// When true, converts all path components to lowercase.
    /// Default: false
    /// </summary>
    public bool LowercasePath { get; init; }

    /// <summary>
    /// Optional prefix for the generated file.
    /// </summary>
    public string? Prefix { get; init; }

    /// <summary>
    /// Creates options with flat file naming (no folders).
    /// </summary>
    public static FileNamingOptions Flat => new()
    {
        UseFoldersForNamespace = false,
        UseFoldersForPrefix = false
    };
}
