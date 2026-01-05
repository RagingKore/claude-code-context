using Microsoft.CodeAnalysis;

namespace FluentSourceGen;

/// <summary>
/// Wraps an INamedTypeSymbol interface with convenient access to type arguments.
/// </summary>
public readonly struct InterfaceMatch
{
    readonly INamedTypeSymbol _interface;

    internal InterfaceMatch(INamedTypeSymbol interfaceSymbol)
    {
        _interface = interfaceSymbol;
    }

    /// <summary>
    /// Gets the underlying interface symbol.
    /// </summary>
    public INamedTypeSymbol Symbol => _interface;

    /// <summary>
    /// Gets the interface name.
    /// </summary>
    public string Name => _interface.Name;

    /// <summary>
    /// Gets the fully qualified interface name.
    /// </summary>
    public string FullName => _interface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    /// <summary>
    /// Gets the number of type arguments.
    /// </summary>
    public int TypeArgumentCount => _interface.TypeArguments.Length;

    /// <summary>
    /// Gets a type argument by index (e.g., for IResultBase&lt;TValue, TError&gt;).
    /// </summary>
    public ITypeSymbol TypeArgument(int index)
    {
        var typeArgs = _interface.TypeArguments;
        if (index < 0 || index >= typeArgs.Length)
            throw new ArgumentOutOfRangeException(nameof(index),
                $"Type argument index {index} is out of range. Interface has {typeArgs.Length} type arguments.");

        return typeArgs[index];
    }

    /// <summary>
    /// Tries to get a type argument by index.
    /// </summary>
    public bool TryGetTypeArgument(int index, out ITypeSymbol? typeSymbol)
    {
        var typeArgs = _interface.TypeArguments;
        if (index >= 0 && index < typeArgs.Length)
        {
            typeSymbol = typeArgs[index];
            return true;
        }

        typeSymbol = null;
        return false;
    }

    /// <summary>
    /// Gets all type arguments.
    /// </summary>
    public IReadOnlyList<ITypeSymbol> TypeArguments => _interface.TypeArguments;

    /// <summary>
    /// Extracts variant types from IVariantException&lt;T1, T2, ...&gt; style interfaces.
    /// Searches all interfaces implemented by the given type for variant patterns.
    /// </summary>
    public static IReadOnlyList<ITypeSymbol> ExtractVariantTypes(ITypeSymbol type, string variantInterfacePrefix)
    {
        var variants = new List<ITypeSymbol>();

        foreach (var iface in type.AllInterfaces)
        {
            var ifaceName = iface.OriginalDefinition.ToDisplayString();

            if (ifaceName.StartsWith(variantInterfacePrefix, StringComparison.Ordinal))
            {
                variants.AddRange(iface.TypeArguments);
                break;
            }
        }

        return variants;
    }
}
