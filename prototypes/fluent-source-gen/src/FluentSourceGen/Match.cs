using Microsoft.CodeAnalysis;

namespace FluentSourceGen;

/// <summary>
/// Base class for matched items (attributes or interfaces) with common type argument access.
/// </summary>
public abstract class Match
{
    /// <summary>
    /// Gets the number of type arguments.
    /// </summary>
    public abstract int TypeArgumentCount { get; }

    /// <summary>
    /// Gets all type arguments.
    /// </summary>
    public abstract IReadOnlyList<ITypeSymbol> TypeArguments { get; }

    /// <summary>
    /// Gets a type argument by index.
    /// </summary>
    public ITypeSymbol TypeArgument(int index)
    {
        var typeArgs = TypeArguments;
        if (index < 0 || index >= typeArgs.Count)
            throw new ArgumentOutOfRangeException(nameof(index),
                $"Type argument index {index} is out of range. Match has {typeArgs.Count} type arguments.");

        return typeArgs[index];
    }

    /// <summary>
    /// Tries to get a type argument by index.
    /// </summary>
    public bool TryGetTypeArgument(int index, out ITypeSymbol? typeSymbol)
    {
        var typeArgs = TypeArguments;
        if (index >= 0 && index < typeArgs.Count)
        {
            typeSymbol = typeArgs[index];
            return true;
        }

        typeSymbol = null;
        return false;
    }

    /// <summary>
    /// Whether this match is an attribute match.
    /// </summary>
    public bool IsAttribute => this is AttributeMatch;

    /// <summary>
    /// Whether this match is an interface match.
    /// </summary>
    public bool IsInterface => this is InterfaceMatch;

    /// <summary>
    /// Tries to cast this match to an AttributeMatch.
    /// </summary>
    public AttributeMatch? AsAttribute => this as AttributeMatch;

    /// <summary>
    /// Tries to cast this match to an InterfaceMatch.
    /// </summary>
    public InterfaceMatch? AsInterface => this as InterfaceMatch;
}

/// <summary>
/// Wraps an AttributeData with convenient access to type arguments and constructor arguments.
/// </summary>
public sealed class AttributeMatch : Match
{
    readonly AttributeData _attribute;

    internal AttributeMatch(AttributeData attribute)
    {
        _attribute = attribute;
    }

    /// <summary>
    /// Gets the underlying AttributeData.
    /// </summary>
    public AttributeData Data => _attribute;

    /// <summary>
    /// Gets the attribute class symbol.
    /// </summary>
    public INamedTypeSymbol? AttributeClass => _attribute.AttributeClass;

    /// <inheritdoc />
    public override int TypeArgumentCount => _attribute.AttributeClass?.TypeArguments.Length ?? 0;

    /// <inheritdoc />
    public override IReadOnlyList<ITypeSymbol> TypeArguments =>
        _attribute.AttributeClass?.TypeArguments.ToList() ?? [];

    /// <summary>
    /// Gets a constructor argument by index.
    /// </summary>
    public TypedConstant ConstructorArgument(int index)
    {
        var args = _attribute.ConstructorArguments;
        if (index < 0 || index >= args.Length)
            throw new ArgumentOutOfRangeException(nameof(index),
                $"Constructor argument index {index} is out of range. Attribute has {args.Length} constructor arguments.");

        return args[index];
    }

    /// <summary>
    /// Tries to get a constructor argument value by index.
    /// </summary>
    public bool TryGetConstructorArgument<T>(int index, out T? value)
    {
        var args = _attribute.ConstructorArguments;
        if (index >= 0 && index < args.Length && args[index].Value is T typedValue)
        {
            value = typedValue;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Gets all constructor arguments.
    /// </summary>
    public IReadOnlyList<TypedConstant> ConstructorArguments => _attribute.ConstructorArguments;

    /// <summary>
    /// Gets a named argument by name.
    /// </summary>
    public TypedConstant? NamedArgument(string name)
    {
        foreach (var kvp in _attribute.NamedArguments)
        {
            if (kvp.Key == name)
                return kvp.Value;
        }
        return null;
    }

    /// <summary>
    /// Tries to get a named argument value.
    /// </summary>
    public bool TryGetNamedArgument<T>(string name, out T? value)
    {
        foreach (var kvp in _attribute.NamedArguments)
        {
            if (kvp.Key == name && kvp.Value.Value is T typedValue)
            {
                value = typedValue;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Gets all named arguments.
    /// </summary>
    public IReadOnlyDictionary<string, TypedConstant> NamedArguments =>
        _attribute.NamedArguments.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
}

/// <summary>
/// Wraps an INamedTypeSymbol interface with convenient access to type arguments.
/// </summary>
public sealed class InterfaceMatch : Match
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

    /// <inheritdoc />
    public override int TypeArgumentCount => _interface.TypeArguments.Length;

    /// <inheritdoc />
    public override IReadOnlyList<ITypeSymbol> TypeArguments => _interface.TypeArguments;

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
