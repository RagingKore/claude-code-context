using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;

namespace FluentSourceGen;

/// <summary>
/// Provides deterministic, collision-resistant file naming for source generators.
/// </summary>
public static class SourceGeneratorFileNaming
{
    #region Per-Type Mode

    /// <summary>
    /// Generates a deterministic hint name for source-generated files.
    /// The method automatically extracts generic type arguments from the type symbol if available,
    /// or uses the type's fully qualified name for hash computation.
    /// </summary>
    /// <param name="typeSymbol">The type symbol being generated.</param>
    /// <param name="options">Optional configuration for file naming.</param>
    /// <param name="typeArgs">Optional explicit type arguments. If null, automatically extracted from typeSymbol.</param>
    /// <returns>A hint name suitable for AddSource() that includes a deterministic hash.</returns>
    public static string GetHintName(
        INamedTypeSymbol typeSymbol,
        FileNamingOptions? options = null,
        IEnumerable<ITypeSymbol>? typeArgs = null)
    {
        options ??= FileNamingOptions.Default;

        // Use custom strategy if provided
        if (options.TypeHintNameStrategy is not null)
            return options.TypeHintNameStrategy(typeSymbol, typeArgs?.ToList());

        var fileName = BuildFileName(typeSymbol, options, typeArgs?.ToList());
        var directoryParts = BuildDirectoryParts(typeSymbol, options);

        return CombinePathParts(directoryParts, fileName, options);
    }

    #endregion

    #region Aggregate Mode

    /// <summary>
    /// Gets the hint name for aggregate mode (all types → single file).
    /// </summary>
    /// <param name="options">File naming options with FileName set.</param>
    /// <returns>The configured file name.</returns>
    /// <exception cref="InvalidOperationException">When FileName is not configured.</exception>
    public static string GetAggregateHintName(FileNamingOptions options)
    {
        if (string.IsNullOrEmpty(options.FileName))
            throw new InvalidOperationException(
                "FileName must be set in FileNamingOptions for aggregate mode. " +
                "Configure it in the Configure() method: options.FileNaming.FileName = \"MyRegistry.g.cs\"");

        return options.FileName;
    }

    #endregion

    #region Grouped Mode

    /// <summary>
    /// Gets the hint name for a namespace group.
    /// Uses custom delegate if configured, otherwise uses default pattern.
    /// </summary>
    /// <param name="namespaceName">The namespace (group key).</param>
    /// <param name="options">File naming options.</param>
    /// <returns>A hint name for this namespace group.</returns>
    public static string GetNamespaceGroupHintName(string namespaceName, FileNamingOptions options)
    {
        // Use custom delegate if provided
        if (options.NamespaceGroupHintName is not null)
            return options.NamespaceGroupHintName(namespaceName);

        // Default implementation
        return DefaultNamespaceGroupHintName(namespaceName, options);
    }

    /// <summary>
    /// Gets the hint name for an assembly group.
    /// Uses custom delegate if configured, otherwise uses default pattern.
    /// </summary>
    /// <param name="assemblyName">The assembly name (group key).</param>
    /// <param name="options">File naming options.</param>
    /// <returns>A hint name for this assembly group.</returns>
    public static string GetAssemblyGroupHintName(string assemblyName, FileNamingOptions options)
    {
        // Use custom delegate if provided
        if (options.AssemblyGroupHintName is not null)
            return options.AssemblyGroupHintName(assemblyName);

        // Default implementation
        return DefaultAssemblyGroupHintName(assemblyName, options);
    }

    /// <summary>
    /// Default implementation for namespace group hint names.
    /// Pattern: {Prefix/}{Namespace/}{LastPart}Services.g.cs
    /// Example: "MyApp.Domain.Services" → "Generated/MyApp.Domain.Services/ServicesRegistry.g.cs"
    /// </summary>
    public static string DefaultNamespaceGroupHintName(string namespaceName, FileNamingOptions options)
    {
        var ns = string.IsNullOrEmpty(namespaceName) ? "Global" : namespaceName;
        var lastPart = ns.Contains('.') ? ns.Substring(ns.LastIndexOf('.') + 1) : ns;
        var className = $"{lastPart}Services";

        var parts = new List<string>();

        // Add prefix folder if configured
        if (!string.IsNullOrEmpty(options.Prefix) && options.UseFoldersForPrefix)
        {
            var prefix = ApplyCasing(options.Prefix, options.LowercasePath);
            parts.Add(prefix);
        }

        // Add namespace as folder path
        if (options.UseFoldersForNamespace)
        {
            var casedNamespace = ApplyCasing(ns, options.LowercasePath);
            parts.Add(casedNamespace);
        }

        var fileName = ApplyCasing(className, options.LowercasePath) + ".g.cs";

        if (parts.Count > 0)
        {
            var directory = string.Join("/", parts);
            return $"{directory}/{fileName}";
        }

        // Flat mode with prefix
        if (!string.IsNullOrEmpty(options.Prefix) && !options.UseFoldersForPrefix)
        {
            var prefix = ApplyCasing(options.Prefix, options.LowercasePath);
            return $"{prefix}_{fileName}";
        }

        return fileName;
    }

    /// <summary>
    /// Default implementation for assembly group hint names.
    /// Pattern: {Prefix/}{AssemblyName}Services.g.cs
    /// Example: "MyApp.Core" → "Generated/MyApp.CoreServices.g.cs"
    /// </summary>
    public static string DefaultAssemblyGroupHintName(string assemblyName, FileNamingOptions options)
    {
        var name = string.IsNullOrEmpty(assemblyName) ? "Global" : assemblyName;
        var className = $"{name.Replace(".", "")}Services";

        var parts = new List<string>();

        // Add prefix folder if configured
        if (!string.IsNullOrEmpty(options.Prefix) && options.UseFoldersForPrefix)
        {
            var prefix = ApplyCasing(options.Prefix, options.LowercasePath);
            parts.Add(prefix);
        }

        var fileName = ApplyCasing(className, options.LowercasePath) + ".g.cs";

        if (parts.Count > 0)
        {
            var directory = string.Join("/", parts);
            return $"{directory}/{fileName}";
        }

        // Flat mode with prefix
        if (!string.IsNullOrEmpty(options.Prefix) && !options.UseFoldersForPrefix)
        {
            var prefix = ApplyCasing(options.Prefix, options.LowercasePath);
            return $"{prefix}_{fileName}";
        }

        return fileName;
    }

    #endregion

    #region Helper Methods

    static string GenerateHashInput(INamedTypeSymbol typeSymbol, List<ITypeSymbol>? typeArgs)
    {
        if (typeArgs is { Count: > 0 })
            // Use fully qualified type arguments for hash
            return string.Join(",", typeArgs.Select(t => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));

        if (typeSymbol is { IsGenericType: true, TypeArguments.Length: > 0 })
            // Auto-extract from generic type
            return string.Join(",", typeSymbol.TypeArguments.Select(t => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));

        // Use fully qualified type name as fallback
        return typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    static string BuildFileName(INamedTypeSymbol typeSymbol, FileNamingOptions options, List<ITypeSymbol>? typeArgs)
    {
        var typeName = ApplyCasing(typeSymbol.Name, options.LowercasePath);

        if (!options.IncludeHash)
            return $"{typeName}.g.cs";

        var hashInput = GenerateHashInput(typeSymbol, typeArgs);
        var hash = ComputeStableHash(hashInput);
        return $"{typeName}_{hash}.g.cs";
    }

    static List<string> BuildDirectoryParts(INamedTypeSymbol typeSymbol, FileNamingOptions options)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(options.Prefix) && options.UseFoldersForPrefix)
        {
            var prefix = ApplyCasing(options.Prefix, options.LowercasePath);
            parts.Add(prefix);
        }

        if (options.UseFoldersForNamespace)
        {
            var namespaceName = GetNamespaceName(typeSymbol);
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
            var prefix = ApplyCasing(options.Prefix, options.LowercasePath);
            return $"{prefix}_{fileName}";
        }

        return fileName;
    }

    static string GetNamespaceName(INamedTypeSymbol typeSymbol) =>
        typeSymbol.ContainingNamespace.IsGlobalNamespace
            ? "Global"
            : typeSymbol.ContainingNamespace.ToDisplayString();

    static string ApplyCasing(string value, bool lowercase) =>
        lowercase ? value.ToLowerInvariant() : value;

    static string ComputeStableHash(string input)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
        return BitConverter.ToString(hash).Replace("-", "").Substring(0, 8);
    }

    #endregion
}

/// <summary>
/// Configuration options for source generator file naming.
/// </summary>
public sealed class FileNamingOptions
{
    #region Per-Type Mode Options

    /// <summary>
    /// Optional prefix for the generated file.
    /// Behavior depends on UseFoldersForPrefix.
    /// Default: null
    /// </summary>
    public string? Prefix { get; set; }

    /// <summary>
    /// When true, creates a folder hierarchy for the namespace (e.g., "Kurrent/Client/").
    /// When false, namespace is not included in the path.
    /// Default: true
    /// </summary>
    public bool UseFoldersForNamespace { get; set; } = true;

    /// <summary>
    /// When true and Prefix is set, the prefix becomes a folder (e.g., "Variants/...").
    /// When false and Prefix is set, the prefix is prepended to the filename (e.g., "Variants_TypeName_HASH.g.cs").
    /// Default: true
    /// </summary>
    public bool UseFoldersForPrefix { get; set; } = true;

    /// <summary>
    /// When true, converts all path components to lowercase.
    /// Default: false
    /// </summary>
    public bool LowercasePath { get; set; }

    /// <summary>
    /// When true, includes a hash in the file name for collision resistance.
    /// Recommended for types with generic arguments to ensure uniqueness.
    /// Default: true
    /// </summary>
    public bool IncludeHash { get; set; } = true;

    /// <summary>
    /// Custom strategy for generating per-type hint names.
    /// When set, completely overrides the default naming logic.
    /// Receives the type symbol and optional type arguments for hash.
    /// </summary>
    /// <example>
    /// options.TypeHintNameStrategy = (type, typeArgs) => $"Generated/{type.Name}.g.cs";
    /// </example>
    public Func<INamedTypeSymbol, IReadOnlyList<ITypeSymbol>?, string>? TypeHintNameStrategy { get; set; }

    #endregion

    #region Aggregate Mode Options

    /// <summary>
    /// Fixed file name for aggregate mode (all types → single file).
    /// Example: "ServiceRegistry.g.cs"
    /// When set, this takes precedence and all types are generated into this single file.
    /// </summary>
    public string? FileName { get; set; }

    #endregion

    #region Grouped Mode Options

    /// <summary>
    /// Custom delegate to compute hint name for namespace groups.
    /// If null, uses default pattern: {Namespace}/{LastPart}Services.g.cs
    /// </summary>
    /// <example>
    /// options.NamespaceGroupHintName = ns => $"{ns.Replace(".", "/")}/{ns.Split('.').Last()}Registry.g.cs";
    /// </example>
    public Func<string, string>? NamespaceGroupHintName { get; set; }

    /// <summary>
    /// Custom delegate to compute hint name for assembly groups.
    /// If null, uses default pattern: {AssemblyName}Services.g.cs
    /// </summary>
    public Func<string, string>? AssemblyGroupHintName { get; set; }

    #endregion

    #region Static Factories

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
    /// Creates a new instance with flat file naming (no folders).
    /// </summary>
    public static FileNamingOptions Flat => new()
    {
        UseFoldersForNamespace = false,
        UseFoldersForPrefix = false
    };

    /// <summary>
    /// Creates a new instance with lowercase paths.
    /// </summary>
    public static FileNamingOptions Lowercase => new()
    {
        UseFoldersForNamespace = true,
        UseFoldersForPrefix = true,
        LowercasePath = true
    };

    #endregion
}
