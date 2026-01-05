using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FluentSourceGen;

/// <summary>
/// Fluent query builder for filtering types in a compilation.
/// </summary>
public sealed class TypeQuery
{
    readonly IncrementalGeneratorInitializationContext _context;
    readonly List<Func<SyntaxNode, bool>> _syntaxPredicates = [];
    readonly List<Func<INamedTypeSymbol, bool>> _semanticPredicates = [];

    string? _attributeFullName;
    string? _interfaceFullName;
    TypeFilter _typeFilter = TypeFilter.None;

    internal TypeQuery(IncrementalGeneratorInitializationContext context)
    {
        _context = context;
    }

    #region Type Kind Filters

    /// <summary>
    /// Filter to only class types.
    /// </summary>
    public TypeQuery ThatAreClasses()
    {
        _typeFilter |= TypeFilter.Class;
        return this;
    }

    /// <summary>
    /// Filter to only struct types.
    /// </summary>
    public TypeQuery ThatAreStructs()
    {
        _typeFilter |= TypeFilter.Struct;
        return this;
    }

    /// <summary>
    /// Filter to only record types (class or struct).
    /// </summary>
    public TypeQuery ThatAreRecords()
    {
        _typeFilter |= TypeFilter.AnyRecord;
        return this;
    }

    /// <summary>
    /// Filter to only record class types.
    /// </summary>
    public TypeQuery ThatAreRecordClasses()
    {
        _typeFilter |= TypeFilter.Record;
        return this;
    }

    /// <summary>
    /// Filter to only record struct types.
    /// </summary>
    public TypeQuery ThatAreRecordStructs()
    {
        _typeFilter |= TypeFilter.RecordStruct;
        return this;
    }

    /// <summary>
    /// Filter to only interface types.
    /// </summary>
    public TypeQuery ThatAreInterfaces()
    {
        _typeFilter |= TypeFilter.Interface;
        return this;
    }

    /// <summary>
    /// Filter to only enum types.
    /// </summary>
    public TypeQuery ThatAreEnums()
    {
        _typeFilter |= TypeFilter.Enum;
        return this;
    }

    #endregion

    #region Modifier Filters

    /// <summary>
    /// Filter to only partial types.
    /// </summary>
    public TypeQuery ThatArePartial()
    {
        _typeFilter |= TypeFilter.Partial;
        _syntaxPredicates.Add(static node =>
            node is TypeDeclarationSyntax tds &&
            tds.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)));
        return this;
    }

    /// <summary>
    /// Filter to only static types.
    /// </summary>
    public TypeQuery ThatAreStatic()
    {
        _typeFilter |= TypeFilter.Static;
        _semanticPredicates.Add(static t => t.IsStatic);
        return this;
    }

    /// <summary>
    /// Filter to only abstract types.
    /// </summary>
    public TypeQuery ThatAreAbstract()
    {
        _typeFilter |= TypeFilter.Abstract;
        _semanticPredicates.Add(static t => t.IsAbstract);
        return this;
    }

    /// <summary>
    /// Filter to only sealed types.
    /// </summary>
    public TypeQuery ThatAreSealed()
    {
        _typeFilter |= TypeFilter.Sealed;
        _semanticPredicates.Add(static t => t.IsSealed);
        return this;
    }

    #endregion

    #region Visibility Filters

    /// <summary>
    /// Filter to only public types.
    /// </summary>
    public TypeQuery ThatArePublic()
    {
        _typeFilter |= TypeFilter.Public;
        _semanticPredicates.Add(static t => t.DeclaredAccessibility == Accessibility.Public);
        return this;
    }

    /// <summary>
    /// Filter to only internal types.
    /// </summary>
    public TypeQuery ThatAreInternal()
    {
        _typeFilter |= TypeFilter.Internal;
        _semanticPredicates.Add(static t => t.DeclaredAccessibility == Accessibility.Internal);
        return this;
    }

    #endregion

    #region Attribute Filters

    /// <summary>
    /// Filter to types with the specified attribute.
    /// Use angle brackets for generic attributes: "MyNamespace.MyAttribute&lt;&gt;" or "MyAttribute&lt;,&gt;"
    /// </summary>
    public TypeQuery WithAttribute(string attributeFullName)
    {
        _attributeFullName = NormalizeTypeName(attributeFullName);
        _syntaxPredicates.Add(static node =>
            node is TypeDeclarationSyntax tds && tds.AttributeLists.Count > 0);
        return this;
    }

    /// <summary>
    /// Filter to types with the specified attribute type.
    /// </summary>
    public TypeQuery WithAttribute<TAttribute>() where TAttribute : Attribute
    {
        return WithAttribute(typeof(TAttribute).FullName!);
    }

    #endregion

    #region Interface Filters

    /// <summary>
    /// Filter to types implementing the specified interface.
    /// Use angle brackets for generic interfaces: "IMyInterface&lt;,&gt;"
    /// </summary>
    public TypeQuery Implementing(string interfaceFullName)
    {
        _interfaceFullName = NormalizeTypeName(interfaceFullName);
        return this;
    }

    /// <summary>
    /// Filter to types implementing the specified interface type.
    /// </summary>
    public TypeQuery Implementing<TInterface>()
    {
        return Implementing(typeof(TInterface).FullName!);
    }

    #endregion

    #region Namespace Filters

    /// <summary>
    /// Filter to types in the specified namespace (exact match).
    /// </summary>
    public TypeQuery InNamespace(string namespaceName)
    {
        _semanticPredicates.Add(t =>
            !t.ContainingNamespace.IsGlobalNamespace &&
            t.ContainingNamespace.ToDisplayString() == namespaceName);
        return this;
    }

    /// <summary>
    /// Filter to types in namespaces starting with the specified prefix.
    /// </summary>
    public TypeQuery InNamespaceStartingWith(string namespacePrefix)
    {
        _semanticPredicates.Add(t =>
            !t.ContainingNamespace.IsGlobalNamespace &&
            t.ContainingNamespace.ToDisplayString().StartsWith(namespacePrefix, StringComparison.Ordinal));
        return this;
    }

    #endregion

    #region Custom Filters

    /// <summary>
    /// Add a custom syntax-level predicate.
    /// </summary>
    public TypeQuery Where(Func<SyntaxNode, bool> syntaxPredicate)
    {
        _syntaxPredicates.Add(syntaxPredicate);
        return this;
    }

    /// <summary>
    /// Add a custom semantic-level predicate.
    /// </summary>
    public TypeQuery Where(Func<INamedTypeSymbol, bool> semanticPredicate)
    {
        _semanticPredicates.Add(semanticPredicate);
        return this;
    }

    #endregion

    #region Terminal Operations

    /// <summary>
    /// Execute the query and process each matching type.
    /// </summary>
    public void ForEach(Action<INamedTypeSymbol, SourceEmitter> action)
    {
        var provider = _context.SyntaxProvider.CreateSyntaxProvider(
            predicate: CreateSyntaxPredicate(),
            transform: CreateSemanticTransform()
        ).Where(static result => result.Symbol is not null);

        _context.RegisterSourceOutput(provider, (spc, result) =>
        {
            var emitter = new SourceEmitter(spc, result.Symbol!);
            try
            {
                action(result.Symbol!, emitter);
            }
            catch (Exception ex)
            {
                emitter.ReportError("GEN500", "Generator failed", ex.ToString());
            }
        });
    }

    /// <summary>
    /// Execute the query with attribute data and process each matching type.
    /// </summary>
    public void ForEach(Action<INamedTypeSymbol, AttributeMatch, SourceEmitter> action)
    {
        if (_attributeFullName is null)
            throw new InvalidOperationException("WithAttribute must be called before using this overload");

        var provider = _context.SyntaxProvider.CreateSyntaxProvider(
            predicate: CreateSyntaxPredicate(),
            transform: CreateSemanticTransform()
        ).Where(static result => result.Symbol is not null && result.Attribute is not null);

        _context.RegisterSourceOutput(provider, (spc, result) =>
        {
            var emitter = new SourceEmitter(spc, result.Symbol!);
            try
            {
                action(result.Symbol!, new AttributeMatch(result.Attribute!), emitter);
            }
            catch (Exception ex)
            {
                emitter.ReportError("GEN500", "Generator failed", ex.ToString());
            }
        });
    }

    /// <summary>
    /// Execute the query with interface data and process each matching type.
    /// </summary>
    public void ForEach(Action<INamedTypeSymbol, InterfaceMatch, SourceEmitter> action)
    {
        if (_interfaceFullName is null)
            throw new InvalidOperationException("Implementing must be called before using this overload");

        var provider = _context.SyntaxProvider.CreateSyntaxProvider(
            predicate: CreateSyntaxPredicate(),
            transform: CreateSemanticTransform()
        ).Where(static result => result.Symbol is not null && result.Interface is not null);

        _context.RegisterSourceOutput(provider, (spc, result) =>
        {
            var emitter = new SourceEmitter(spc, result.Symbol!);
            try
            {
                action(result.Symbol!, new InterfaceMatch(result.Interface!), emitter);
            }
            catch (Exception ex)
            {
                emitter.ReportError("GEN500", "Generator failed", ex.ToString());
            }
        });
    }

    #endregion

    #region Internal Helpers

    Func<SyntaxNode, CancellationToken, bool> CreateSyntaxPredicate()
    {
        return (node, _) =>
        {
            if (node is not TypeDeclarationSyntax)
                return false;

            foreach (var predicate in _syntaxPredicates)
            {
                if (!predicate(node))
                    return false;
            }

            return true;
        };
    }

    Func<GeneratorSyntaxContext, CancellationToken, QueryResult> CreateSemanticTransform()
    {
        // Capture these for the lambda
        var typeFilter = _typeFilter;
        var attributeName = _attributeFullName;
        var interfaceName = _interfaceFullName;
        var semanticPredicates = _semanticPredicates.ToList();

        return (ctx, _) =>
        {
            if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node) is not INamedTypeSymbol symbol)
                return default;

            // Apply type filter
            if (!MatchesTypeFilter(symbol, typeFilter))
                return default;

            // Apply semantic predicates
            foreach (var predicate in semanticPredicates)
            {
                if (!predicate(symbol))
                    return default;
            }

            // Find matching attribute
            AttributeData? matchedAttribute = null;
            if (attributeName is not null)
            {
                matchedAttribute = FindMatchingAttribute(symbol, attributeName);
                if (matchedAttribute is null)
                    return default;
            }

            // Find matching interface
            INamedTypeSymbol? matchedInterface = null;
            if (interfaceName is not null)
            {
                matchedInterface = FindMatchingInterface(symbol, interfaceName);
                if (matchedInterface is null)
                    return default;
            }

            return new QueryResult(symbol, matchedAttribute, matchedInterface);
        };
    }

    static bool MatchesTypeFilter(INamedTypeSymbol symbol, TypeFilter filter)
    {
        if (filter == TypeFilter.None)
            return true;

        // Check type kinds (if any specified)
        var hasKindFilter = (filter & TypeFilter.AnyType) != 0;
        if (hasKindFilter)
        {
            var kindMatches = false;

            if ((filter & TypeFilter.Class) != 0 && symbol.TypeKind == TypeKind.Class && !symbol.IsRecord)
                kindMatches = true;
            if ((filter & TypeFilter.Struct) != 0 && symbol.TypeKind == TypeKind.Struct && !symbol.IsRecord)
                kindMatches = true;
            if ((filter & TypeFilter.Record) != 0 && symbol.IsRecord && symbol.TypeKind == TypeKind.Class)
                kindMatches = true;
            if ((filter & TypeFilter.RecordStruct) != 0 && symbol.IsRecord && symbol.TypeKind == TypeKind.Struct)
                kindMatches = true;
            if ((filter & TypeFilter.Interface) != 0 && symbol.TypeKind == TypeKind.Interface)
                kindMatches = true;
            if ((filter & TypeFilter.Enum) != 0 && symbol.TypeKind == TypeKind.Enum)
                kindMatches = true;

            if (!kindMatches)
                return false;
        }

        return true;
    }

    static AttributeData? FindMatchingAttribute(INamedTypeSymbol symbol, string attributeName)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass is not { } attrClass)
                continue;

            var attrFullName = attrClass.OriginalDefinition.ToDisplayString();

            // Handle generic attribute matching (e.g., "MyAttr<>" matches "MyAttr<int>")
            if (MatchesTypeName(attrFullName, attributeName))
                return attr;
        }

        return null;
    }

    static INamedTypeSymbol? FindMatchingInterface(INamedTypeSymbol symbol, string interfaceName)
    {
        foreach (var iface in symbol.AllInterfaces)
        {
            var ifaceFullName = iface.OriginalDefinition.ToDisplayString();

            if (MatchesTypeName(ifaceFullName, interfaceName))
                return iface;
        }

        return null;
    }

    static bool MatchesTypeName(string actualName, string pattern)
    {
        // Direct match
        if (actualName == pattern)
            return true;

        // Generic pattern matching: "MyType<>" or "MyType<,>" matches "MyType<T>" or "MyType<T,U>"
        if (pattern.Contains('<'))
        {
            var patternBase = pattern.Substring(0, pattern.IndexOf('<'));
            var actualBase = actualName.Contains('<')
                ? actualName.Substring(0, actualName.IndexOf('<'))
                : actualName;

            return patternBase == actualBase;
        }

        return false;
    }

    static string NormalizeTypeName(string name)
    {
        // Remove "Attribute" suffix if present for convenience
        // "MyAttribute" and "My" both match "MyAttribute"
        return name;
    }

    #endregion

    internal readonly record struct QueryResult(
        INamedTypeSymbol? Symbol,
        AttributeData? Attribute,
        INamedTypeSymbol? Interface);
}
