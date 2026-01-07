using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FluentSourceGen;

/// <summary>
/// Extension methods for Roslyn symbols to simplify common operations.
/// </summary>
public static class SymbolExtensions
{
    #region Namespace Extensions

    /// <summary>
    /// Gets the namespace as a string, or empty string for global namespace.
    /// </summary>
    public static string GetNamespace(this INamedTypeSymbol symbol) =>
        symbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : symbol.ContainingNamespace.ToDisplayString();

    /// <summary>
    /// Gets a file-scoped namespace declaration, or a comment for global namespace.
    /// </summary>
    public static string GetNamespaceDeclaration(this INamedTypeSymbol symbol)
    {
        var ns = symbol.GetNamespace();
        return !string.IsNullOrEmpty(ns)
            ? $"namespace {ns};"
            : "// Global namespace";
    }

    /// <summary>
    /// Gets a block-scoped namespace declaration with opening brace.
    /// </summary>
    public static string GetNamespaceBlockStart(this INamedTypeSymbol symbol)
    {
        var ns = symbol.GetNamespace();
        return !string.IsNullOrEmpty(ns)
            ? $"namespace {ns} {{"
            : "// Global namespace";
    }

    /// <summary>
    /// Gets the closing brace for a block-scoped namespace (empty if global).
    /// </summary>
    public static string GetNamespaceBlockEnd(this INamedTypeSymbol symbol) =>
        symbol.ContainingNamespace.IsGlobalNamespace ? "" : "}";

    #endregion

    #region Type Name Extensions

    /// <summary>
    /// Gets the fully qualified name without the "global::" prefix.
    /// </summary>
    public static string FullName(this ITypeSymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");

    /// <summary>
    /// Gets the fully qualified name with the "global::" prefix for unambiguous references.
    /// </summary>
    public static string GlobalName(this ITypeSymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    /// <summary>
    /// Gets just the type name without namespace.
    /// </summary>
    public static string SimpleName(this ITypeSymbol symbol) => symbol.Name;

    #endregion

    #region Type Declaration Extensions

    /// <summary>
    /// Gets the type declaration keyword (class, struct, record, etc.).
    /// </summary>
    public static string GetTypeKeyword(this INamedTypeSymbol symbol) =>
        symbol switch
        {
            { IsRecord: true, IsValueType: true } => "record struct",
            { IsRecord: true } => "record",
            { IsValueType: true } => "struct",
            { TypeKind: TypeKind.Interface } => "interface",
            { TypeKind: TypeKind.Enum } => "enum",
            { TypeKind: TypeKind.Class } => "class",
            _ => "class"
        };

    /// <summary>
    /// Gets the accessibility keyword (public, internal, etc.).
    /// </summary>
    public static string GetAccessibility(this INamedTypeSymbol symbol) =>
        symbol.DeclaredAccessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Protected => "protected",
            Accessibility.Private => "private",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            _ => ""
        };

    /// <summary>
    /// Gets all modifiers as a string (public static partial, etc.).
    /// </summary>
    public static string GetModifiers(this INamedTypeSymbol symbol)
    {
        var parts = new List<string>();

        var accessibility = symbol.GetAccessibility();
        if (!string.IsNullOrEmpty(accessibility))
            parts.Add(accessibility);

        if (symbol.IsStatic)
            parts.Add("static");

        if (symbol.IsReadOnly && symbol.IsValueType)
            parts.Add("readonly");
        else if (symbol.IsAbstract && !symbol.IsSealed && symbol.TypeKind != TypeKind.Interface)
            parts.Add("abstract");
        else if (!symbol.IsAbstract && symbol.IsSealed && symbol.TypeKind == TypeKind.Class)
            parts.Add("sealed");

        if (symbol.IsPartial())
            parts.Add("partial");

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Checks if the type is declared as partial.
    /// </summary>
    public static bool IsPartial(this INamedTypeSymbol symbol) =>
        symbol.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<TypeDeclarationSyntax>()
            .Any(syntax => syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)));

    /// <summary>
    /// Gets a full type declaration line (e.g., "public partial record struct MyType").
    /// </summary>
    public static string GetTypeDeclaration(this INamedTypeSymbol symbol) =>
        $"{symbol.GetModifiers()} {symbol.GetTypeKeyword()} {symbol.Name}";

    #endregion

    #region Containing Type Extensions

    /// <summary>
    /// Gets all containing types from outermost to innermost.
    /// </summary>
    public static IReadOnlyList<INamedTypeSymbol> GetContainingTypes(this INamedTypeSymbol symbol)
    {
        var types = new List<INamedTypeSymbol>();
        var current = symbol.ContainingType;

        while (current is not null)
        {
            types.Add(current);
            current = current.ContainingType;
        }

        types.Reverse();
        return types;
    }

    /// <summary>
    /// Generates opening declarations for all containing types.
    /// Returns a tuple of (declarations, closingBraces, indentLevel).
    /// </summary>
    public static (string Declarations, string ClosingBraces, int IndentLevel) GetContainingTypeDeclarations(
        this INamedTypeSymbol symbol,
        string indent = "    ")
    {
        var containingTypes = symbol.GetContainingTypes();
        if (containingTypes.Count == 0)
            return ("", "", 0);

        var declarations = new StringBuilder();
        var closingBraces = new StringBuilder();
        var currentIndent = "";

        foreach (var containingType in containingTypes)
        {
            currentIndent += indent;
            declarations.AppendLine($"{currentIndent}{containingType.GetTypeDeclaration()} {{");
        }

        for (var i = containingTypes.Count - 1; i >= 0; i--)
        {
            var closeIndent = new string(' ', (i + 1) * indent.Length);
            closingBraces.AppendLine($"{closeIndent}}}");
        }

        return (declarations.ToString(), closingBraces.ToString(), containingTypes.Count);
    }

    #endregion

    #region Interface Extensions

    /// <summary>
    /// Finds an interface by name pattern (supports generic patterns like "IFoo&lt;&gt;").
    /// </summary>
    public static INamedTypeSymbol? FindInterface(this INamedTypeSymbol symbol, string interfacePattern)
    {
        foreach (var iface in symbol.AllInterfaces)
        {
            var ifaceName = iface.OriginalDefinition.ToDisplayString();

            if (MatchesTypeName(ifaceName, interfacePattern))
                return iface;
        }

        return null;
    }

    /// <summary>
    /// Checks if the type implements an interface matching the pattern.
    /// </summary>
    public static bool ImplementsInterface(this INamedTypeSymbol symbol, string interfacePattern) =>
        symbol.FindInterface(interfacePattern) is not null;

    #endregion

    #region Attribute Extensions

    /// <summary>
    /// Finds an attribute by name pattern (supports generic patterns).
    /// </summary>
    public static AttributeData? FindAttribute(this INamedTypeSymbol symbol, string attributePattern)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass is not { } attrClass)
                continue;

            var attrName = attrClass.OriginalDefinition.ToDisplayString();

            if (MatchesTypeName(attrName, attributePattern))
                return attr;
        }

        return null;
    }

    /// <summary>
    /// Finds all attributes matching the name pattern.
    /// </summary>
    public static IEnumerable<AttributeData> FindAttributes(this INamedTypeSymbol symbol, string attributePattern)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass is not { } attrClass)
                continue;

            var attrName = attrClass.OriginalDefinition.ToDisplayString();

            if (MatchesTypeName(attrName, attributePattern))
                yield return attr;
        }
    }

    /// <summary>
    /// Checks if the type has an attribute matching the pattern.
    /// </summary>
    public static bool HasAttribute(this INamedTypeSymbol symbol, string attributePattern) =>
        symbol.FindAttribute(attributePattern) is not null;

    /// <summary>
    /// Gets a constructor argument from an attribute by index.
    /// </summary>
    public static T? GetAttributeArg<T>(this INamedTypeSymbol symbol, string attributePattern, int index)
    {
        var attr = symbol.FindAttribute(attributePattern);
        if (attr is null) return default;

        if (index < 0 || index >= attr.ConstructorArguments.Length)
            return default;

        var arg = attr.ConstructorArguments[index];
        return arg.Value is T value ? value : default;
    }

    /// <summary>
    /// Gets a named argument from an attribute.
    /// </summary>
    public static T? GetAttributeNamedArg<T>(this INamedTypeSymbol symbol, string attributePattern, string argName)
    {
        var attr = symbol.FindAttribute(attributePattern);
        if (attr is null) return default;

        foreach (var namedArg in attr.NamedArguments)
        {
            if (namedArg.Key == argName && namedArg.Value.Value is T value)
                return value;
        }

        return default;
    }

    /// <summary>
    /// Gets a type argument from an attribute's generic parameter by index.
    /// </summary>
    public static ITypeSymbol? GetAttributeTypeArg(this INamedTypeSymbol symbol, string attributePattern, int index)
    {
        var attr = symbol.FindAttribute(attributePattern);
        if (attr?.AttributeClass is not { IsGenericType: true } attrClass)
            return null;

        if (index < 0 || index >= attrClass.TypeArguments.Length)
            return null;

        return attrClass.TypeArguments[index];
    }

    /// <summary>
    /// Gets all type arguments from an attribute's generic parameters.
    /// </summary>
    public static IReadOnlyList<ITypeSymbol> GetAttributeTypeArgs(this INamedTypeSymbol symbol, string attributePattern)
    {
        var attr = symbol.FindAttribute(attributePattern);
        if (attr?.AttributeClass is not { IsGenericType: true } attrClass)
            return [];

        return attrClass.TypeArguments;
    }

    #endregion

    #region Interface Type Argument Extensions

    /// <summary>
    /// Finds all interfaces matching the name pattern.
    /// </summary>
    public static IEnumerable<INamedTypeSymbol> FindInterfaces(this INamedTypeSymbol symbol, string interfacePattern)
    {
        foreach (var iface in symbol.AllInterfaces)
        {
            var ifaceName = iface.OriginalDefinition.ToDisplayString();

            if (MatchesTypeName(ifaceName, interfacePattern))
                yield return iface;
        }
    }

    /// <summary>
    /// Gets a type argument from an interface by index.
    /// </summary>
    public static ITypeSymbol? GetInterfaceTypeArg(this INamedTypeSymbol symbol, string interfacePattern, int index)
    {
        var iface = symbol.FindInterface(interfacePattern);
        if (iface is null || !iface.IsGenericType)
            return null;

        if (index < 0 || index >= iface.TypeArguments.Length)
            return null;

        return iface.TypeArguments[index];
    }

    /// <summary>
    /// Gets all type arguments from an interface.
    /// </summary>
    public static IReadOnlyList<ITypeSymbol> GetInterfaceTypeArgs(this INamedTypeSymbol symbol, string interfacePattern)
    {
        var iface = symbol.FindInterface(interfacePattern);
        if (iface is null || !iface.IsGenericType)
            return [];

        return iface.TypeArguments;
    }

    #endregion

    #region Type Name Matching

    /// <summary>
    /// Matches a type name against a pattern with smart normalization.
    /// Supports: short names, generic patterns, attribute suffix, global:: prefix.
    /// </summary>
    /// <example>
    /// MatchesTypeName("System.SerializableAttribute", "Serializable") // true
    /// MatchesTypeName("MyNamespace.MyType", "MyType") // true
    /// MatchesTypeName("MyType&lt;T&gt;", "MyType&lt;&gt;") // true
    /// MatchesTypeName("global::MyNamespace.Foo", "MyNamespace.Foo") // true
    /// </example>
    public static bool MatchesTypeName(string actualName, string pattern)
    {
        // Normalize: strip global:: prefix
        if (actualName.StartsWith("global::", StringComparison.Ordinal))
            actualName = actualName.Substring(8);
        if (pattern.StartsWith("global::", StringComparison.Ordinal))
            pattern = pattern.Substring(8);

        // Exact match
        if (actualName == pattern)
            return true;

        // Extract base names (before generic args)
        var actualBase = actualName.Contains('<')
            ? actualName.Substring(0, actualName.IndexOf('<'))
            : actualName;
        var patternBase = pattern.Contains('<')
            ? pattern.Substring(0, pattern.IndexOf('<'))
            : pattern;

        // Generic pattern matching: "MyType<>" or "MyType<,>" matches "MyType<T>" or "MyType<T,U>"
        if (pattern.Contains('<') && patternBase == actualBase)
            return true;

        // Short name matching: "MyType" matches "Namespace.MyType"
        var actualShortName = actualBase.Contains('.')
            ? actualBase.Substring(actualBase.LastIndexOf('.') + 1)
            : actualBase;

        if (patternBase == actualShortName)
            return true;

        // Attribute suffix matching: "Serializable" matches "SerializableAttribute"
        if (actualShortName.EndsWith("Attribute", StringComparison.Ordinal))
        {
            var withoutSuffix = actualShortName.Substring(0, actualShortName.Length - 9);
            if (patternBase == withoutSuffix || patternBase.EndsWith("." + withoutSuffix))
                return true;
        }

        return false;
    }

    #endregion
}
