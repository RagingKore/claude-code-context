using Microsoft.CodeAnalysis;

namespace FluentSourceGen;

/// <summary>
/// Wraps an AttributeData with convenient access to type arguments and constructor arguments.
/// </summary>
public readonly struct AttributeMatch
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

    /// <summary>
    /// Gets the number of type arguments (for generic attributes).
    /// </summary>
    public int TypeArgumentCount => _attribute.AttributeClass?.TypeArguments.Length ?? 0;

    /// <summary>
    /// Gets a type argument by index (for generic attributes like MyAttribute&lt;T&gt;).
    /// </summary>
    public ITypeSymbol TypeArgument(int index)
    {
        var typeArgs = _attribute.AttributeClass?.TypeArguments;
        if (typeArgs is null || index < 0 || index >= typeArgs.Value.Length)
            throw new ArgumentOutOfRangeException(nameof(index),
                $"Type argument index {index} is out of range. Attribute has {typeArgs?.Length ?? 0} type arguments.");

        return typeArgs.Value[index];
    }

    /// <summary>
    /// Tries to get a type argument by index.
    /// </summary>
    public bool TryGetTypeArgument(int index, out ITypeSymbol? typeSymbol)
    {
        var typeArgs = _attribute.AttributeClass?.TypeArguments;
        if (typeArgs is not null && index >= 0 && index < typeArgs.Value.Length)
        {
            typeSymbol = typeArgs.Value[index];
            return true;
        }

        typeSymbol = null;
        return false;
    }

    /// <summary>
    /// Gets all type arguments.
    /// </summary>
    public IReadOnlyList<ITypeSymbol> TypeArguments =>
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
