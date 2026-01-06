using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FluentSourceGen;

/// <summary>
/// Fluent query builder for filtering types in a compilation.
/// Chain filter methods and call Generate() to emit source code.
/// </summary>
public sealed class TypeQuery
{
    readonly SyntaxValueProvider _syntaxProvider;
    readonly GeneratorContext _context;
    readonly List<Func<SyntaxNode, bool>> _syntaxPredicates = [];
    readonly List<Func<INamedTypeSymbol, bool>> _semanticPredicates = [];
    readonly List<string> _attributePatterns = [];
    readonly List<string> _interfacePatterns = [];
    readonly List<string> _excludedAttributePatterns = [];
    readonly List<string> _excludedInterfacePatterns = [];
    readonly List<Func<TypeDeclarationSyntax, INamedTypeSymbol, bool>> _combinedPredicates = [];

    bool _requireAllAttributes;
    bool _requireAllInterfaces;

    internal TypeQuery(SyntaxValueProvider syntaxProvider, GeneratorContext context)
    {
        _syntaxProvider = syntaxProvider;
        _context = context;
    }

    void ApplyPredicate(Func<INamedTypeSymbol, bool> predicate)
    {
        _semanticPredicates.Add(predicate);
    }

    void ApplySyntaxPredicate(Func<SyntaxNode, bool> predicate)
    {
        _syntaxPredicates.Add(predicate);
    }

    #region Type Kind Filters

    /// <summary>
    /// Filter by type kind using flags enum.
    /// </summary>
    public TypeQuery OfKind(TypeKind kind)
    {
        ApplyPredicate(t => MatchesTypeKind(t, kind));
        return this;
    }

    /// <summary>
    /// Filter to only class types (non-record).
    /// </summary>
    public TypeQuery ThatAreClasses()
    {
        ApplyPredicate(t => t.TypeKind == Microsoft.CodeAnalysis.TypeKind.Class && !t.IsRecord);
        return this;
    }

    /// <summary>
    /// Filter to only struct types (non-record).
    /// </summary>
    public TypeQuery ThatAreStructs()
    {
        ApplyPredicate(t => t.TypeKind == Microsoft.CodeAnalysis.TypeKind.Struct && !t.IsRecord);
        return this;
    }

    /// <summary>
    /// Filter to only record types (class or struct).
    /// </summary>
    public TypeQuery ThatAreRecords()
    {
        ApplyPredicate(t => t.IsRecord);
        return this;
    }

    /// <summary>
    /// Filter to only record class types.
    /// </summary>
    public TypeQuery ThatAreRecordClasses()
    {
        ApplyPredicate(t => t.IsRecord && t.TypeKind == Microsoft.CodeAnalysis.TypeKind.Class);
        return this;
    }

    /// <summary>
    /// Filter to only record struct types.
    /// </summary>
    public TypeQuery ThatAreRecordStructs()
    {
        ApplyPredicate(t => t.IsRecord && t.TypeKind == Microsoft.CodeAnalysis.TypeKind.Struct);
        return this;
    }

    /// <summary>
    /// Filter to only interface types.
    /// </summary>
    public TypeQuery ThatAreInterfaces()
    {
        ApplyPredicate(t => t.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface);
        return this;
    }

    /// <summary>
    /// Filter to only enum types.
    /// </summary>
    public TypeQuery ThatAreEnums()
    {
        ApplyPredicate(t => t.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum);
        return this;
    }

    /// <summary>
    /// Filter to only delegate types.
    /// </summary>
    public TypeQuery ThatAreDelegates()
    {
        ApplyPredicate(t => t.TypeKind == Microsoft.CodeAnalysis.TypeKind.Delegate);
        return this;
    }

    /// <summary>
    /// Filter to reference types only.
    /// </summary>
    public TypeQuery ThatAreReferenceTypes()
    {
        ApplyPredicate(t => t.IsReferenceType);
        return this;
    }

    /// <summary>
    /// Filter to value types only.
    /// </summary>
    public TypeQuery ThatAreValueTypes()
    {
        ApplyPredicate(t => t.IsValueType);
        return this;
    }

    #endregion

    #region Modifier Filters

    /// <summary>
    /// Filter by modifiers using flags enum.
    /// </summary>
    public TypeQuery WithModifiers(TypeModifiers modifiers)
    {
        ApplyPredicate(t => MatchesModifiers(t, modifiers));
        return this;
    }

    /// <summary>
    /// Filter to only partial types.
    /// </summary>
    public TypeQuery ThatArePartial()
    {
        ApplySyntaxPredicate(node =>
            node is TypeDeclarationSyntax tds &&
            tds.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)));
        return this;
    }

    /// <summary>
    /// Filter to only static types.
    /// </summary>
    public TypeQuery ThatAreStatic()
    {
        ApplyPredicate(t => t.IsStatic);
        return this;
    }

    /// <summary>
    /// Filter to only abstract types.
    /// </summary>
    public TypeQuery ThatAreAbstract()
    {
        ApplyPredicate(t => t.IsAbstract && !t.TypeKind.Equals(Microsoft.CodeAnalysis.TypeKind.Interface));
        return this;
    }

    /// <summary>
    /// Filter to only sealed types.
    /// </summary>
    public TypeQuery ThatAreSealed()
    {
        ApplyPredicate(t => t.IsSealed);
        return this;
    }

    /// <summary>
    /// Filter to only readonly struct types.
    /// </summary>
    public TypeQuery ThatAreReadonly()
    {
        ApplyPredicate(t => t.IsReadOnly);
        return this;
    }

    /// <summary>
    /// Filter to only ref struct types.
    /// </summary>
    public TypeQuery ThatAreRefStructs()
    {
        ApplyPredicate(t => t.IsRefLikeType);
        return this;
    }

    /// <summary>
    /// Exclude partial types.
    /// </summary>
    public TypeQuery ThatAreNotPartial()
    {
        ApplySyntaxPredicate(node =>
            node is not TypeDeclarationSyntax tds ||
            !tds.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)));
        return this;
    }

    /// <summary>
    /// Exclude static types.
    /// </summary>
    public TypeQuery ThatAreNotStatic()
    {
        ApplyPredicate(t => !t.IsStatic);
        return this;
    }

    /// <summary>
    /// Exclude abstract types (concrete types only).
    /// </summary>
    public TypeQuery ThatAreNotAbstract()
    {
        ApplyPredicate(t => !t.IsAbstract || t.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface);
        return this;
    }

    /// <summary>
    /// Exclude sealed types.
    /// </summary>
    public TypeQuery ThatAreNotSealed()
    {
        ApplyPredicate(t => !t.IsSealed);
        return this;
    }

    /// <summary>
    /// Exclude types with specified modifiers.
    /// </summary>
    public TypeQuery WithoutModifiers(TypeModifiers modifiers)
    {
        ApplyPredicate(t => !MatchesModifiers(t, modifiers));
        return this;
    }

    #endregion

    #region Accessibility Filters

    /// <summary>
    /// Filter by accessibility using flags enum.
    /// </summary>
    public TypeQuery WithAccessibility(TypeAccessibility accessibility)
    {
        ApplyPredicate(t => MatchesAccessibility(t, accessibility));
        return this;
    }

    /// <summary>
    /// Exclude types with specified accessibility.
    /// </summary>
    public TypeQuery WithoutAccessibility(TypeAccessibility accessibility)
    {
        ApplyPredicate(t => !MatchesAccessibility(t, accessibility));
        return this;
    }

    /// <summary>
    /// Filter to only public types.
    /// </summary>
    public TypeQuery ThatArePublic()
    {
        ApplyPredicate(t => t.DeclaredAccessibility == Accessibility.Public);
        return this;
    }

    /// <summary>
    /// Filter to only internal types.
    /// </summary>
    public TypeQuery ThatAreInternal()
    {
        ApplyPredicate(t => t.DeclaredAccessibility == Accessibility.Internal);
        return this;
    }

    #endregion

    #region Base Type / Inheritance Filters

    /// <summary>
    /// Filter to types derived from the specified base type (direct or transitive).
    /// </summary>
    public TypeQuery DerivedFrom(string baseTypeFullName)
    {
        ApplyPredicate(t => IsDerivedFrom(t, baseTypeFullName, transitive: true));
        return this;
    }

    /// <summary>
    /// Filter to types derived from the specified base type.
    /// </summary>
    public TypeQuery DerivedFrom<TBase>()
    {
        return DerivedFrom(typeof(TBase).FullName!);
    }

    /// <summary>
    /// Filter to types directly derived from the specified base type (not transitive).
    /// </summary>
    public TypeQuery DirectlyDerivedFrom(string baseTypeFullName)
    {
        ApplyPredicate(t => IsDerivedFrom(t, baseTypeFullName, transitive: false));
        return this;
    }

    /// <summary>
    /// Filter to types directly derived from the specified base type.
    /// </summary>
    public TypeQuery DirectlyDerivedFrom<TBase>()
    {
        return DirectlyDerivedFrom(typeof(TBase).FullName!);
    }

    /// <summary>
    /// Exclude types derived from the specified base type.
    /// </summary>
    public TypeQuery NotDerivedFrom(string baseTypeFullName)
    {
        ApplyPredicate(t => !IsDerivedFrom(t, baseTypeFullName, transitive: true));
        return this;
    }

    /// <summary>
    /// Exclude types derived from the specified base type.
    /// </summary>
    public TypeQuery NotDerivedFrom<TBase>()
    {
        return NotDerivedFrom(typeof(TBase).FullName!);
    }

    #endregion

    #region Member Filters

    /// <summary>
    /// Filter to types that have any members.
    /// </summary>
    public TypeQuery WithMembers()
    {
        ApplyPredicate(t => t.GetMembers().Any(m => !m.IsImplicitlyDeclared));
        return this;
    }

    /// <summary>
    /// Filter to types that have any methods.
    /// </summary>
    public TypeQuery WithMethods()
    {
        ApplyPredicate(t => t.GetMembers().OfType<IMethodSymbol>()
            .Any(m => m.MethodKind == MethodKind.Ordinary && !m.IsImplicitlyDeclared));
        return this;
    }

    /// <summary>
    /// Filter to types that have a method with the specified name.
    /// </summary>
    public TypeQuery WithMethod(string methodName)
    {
        ApplyPredicate(t => t.GetMembers(methodName).OfType<IMethodSymbol>()
            .Any(m => m.MethodKind == MethodKind.Ordinary));
        return this;
    }

    /// <summary>
    /// Filter to types that have a method matching the predicate.
    /// </summary>
    public TypeQuery WithMethodMatching(Func<IMethodSymbol, bool> predicate)
    {
        ApplyPredicate(t => t.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && !m.IsImplicitlyDeclared)
            .Any(predicate));
        return this;
    }

    /// <summary>
    /// Filter to types that have any properties.
    /// </summary>
    public TypeQuery WithProperties()
    {
        ApplyPredicate(t => t.GetMembers().OfType<IPropertySymbol>()
            .Any(p => !p.IsImplicitlyDeclared));
        return this;
    }

    /// <summary>
    /// Filter to types that have a property with the specified name.
    /// </summary>
    public TypeQuery WithProperty(string propertyName)
    {
        ApplyPredicate(t => t.GetMembers(propertyName).OfType<IPropertySymbol>().Any());
        return this;
    }

    /// <summary>
    /// Filter to types that have a property matching the predicate.
    /// </summary>
    public TypeQuery WithPropertyMatching(Func<IPropertySymbol, bool> predicate)
    {
        ApplyPredicate(t => t.GetMembers().OfType<IPropertySymbol>()
            .Where(p => !p.IsImplicitlyDeclared)
            .Any(predicate));
        return this;
    }

    /// <summary>
    /// Filter to types that have a property of the specified type.
    /// </summary>
    public TypeQuery WithPropertyOfType(string typeFullName)
    {
        ApplyPredicate(t => t.GetMembers().OfType<IPropertySymbol>()
            .Any(p => SymbolExtensions.MatchesTypeName(p.Type.ToDisplayString(), typeFullName)));
        return this;
    }

    /// <summary>
    /// Filter to types that have any fields.
    /// </summary>
    public TypeQuery WithFields()
    {
        ApplyPredicate(t => t.GetMembers().OfType<IFieldSymbol>()
            .Any(f => !f.IsImplicitlyDeclared));
        return this;
    }

    /// <summary>
    /// Filter to types that have a field with the specified name.
    /// </summary>
    public TypeQuery WithField(string fieldName)
    {
        ApplyPredicate(t => t.GetMembers(fieldName).OfType<IFieldSymbol>().Any());
        return this;
    }

    /// <summary>
    /// Filter to types that have a field matching the predicate.
    /// </summary>
    public TypeQuery WithFieldMatching(Func<IFieldSymbol, bool> predicate)
    {
        ApplyPredicate(t => t.GetMembers().OfType<IFieldSymbol>()
            .Where(f => !f.IsImplicitlyDeclared)
            .Any(predicate));
        return this;
    }

    /// <summary>
    /// Filter to types that have any constructor.
    /// </summary>
    public TypeQuery WithConstructor()
    {
        ApplyPredicate(t => t.Constructors.Any(c => !c.IsImplicitlyDeclared));
        return this;
    }

    /// <summary>
    /// Filter to types that have a parameterless constructor.
    /// </summary>
    public TypeQuery WithParameterlessConstructor()
    {
        ApplyPredicate(t => t.Constructors.Any(c => c.Parameters.Length == 0));
        return this;
    }

    /// <summary>
    /// Filter to types that have a constructor matching the predicate.
    /// </summary>
    public TypeQuery WithConstructorMatching(Func<IMethodSymbol, bool> predicate)
    {
        ApplyPredicate(t => t.Constructors.Any(predicate));
        return this;
    }

    #endregion

    #region Namespace Filters

    /// <summary>
    /// Filter to types in the specified namespace (exact match).
    /// </summary>
    public TypeQuery InNamespace(string namespaceName)
    {
        ApplyPredicate(t =>
            !t.ContainingNamespace.IsGlobalNamespace &&
            t.ContainingNamespace.ToDisplayString() == namespaceName);
        return this;
    }

    /// <summary>
    /// Filter to types in namespaces starting with the specified prefix.
    /// </summary>
    public TypeQuery InNamespaceStartingWith(string namespacePrefix)
    {
        ApplyPredicate(t =>
            !t.ContainingNamespace.IsGlobalNamespace &&
            t.ContainingNamespace.ToDisplayString().StartsWith(namespacePrefix, StringComparison.Ordinal));
        return this;
    }

    /// <summary>
    /// Filter to types in namespaces ending with the specified suffix.
    /// </summary>
    public TypeQuery InNamespaceEndingWith(string namespaceSuffix)
    {
        ApplyPredicate(t =>
            !t.ContainingNamespace.IsGlobalNamespace &&
            t.ContainingNamespace.ToDisplayString().EndsWith(namespaceSuffix, StringComparison.Ordinal));
        return this;
    }

    /// <summary>
    /// Filter to types in namespaces containing the specified substring.
    /// </summary>
    public TypeQuery InNamespaceContaining(string substring)
    {
        ApplyPredicate(t =>
            !t.ContainingNamespace.IsGlobalNamespace &&
            t.ContainingNamespace.ToDisplayString().Contains(substring));
        return this;
    }

    /// <summary>
    /// Filter to types in namespaces matching the specified regex pattern.
    /// </summary>
    public TypeQuery InNamespaceMatching(string regexPattern)
    {
        var regex = new Regex(regexPattern, RegexOptions.Compiled);
        ApplyPredicate(t =>
            !t.ContainingNamespace.IsGlobalNamespace &&
            regex.IsMatch(t.ContainingNamespace.ToDisplayString()));
        return this;
    }

    /// <summary>
    /// Filter to types in the global namespace.
    /// </summary>
    public TypeQuery InGlobalNamespace()
    {
        ApplyPredicate(t => t.ContainingNamespace.IsGlobalNamespace);
        return this;
    }

    /// <summary>
    /// Exclude types in the specified namespace.
    /// </summary>
    public TypeQuery NotInNamespace(string namespaceName)
    {
        ApplyPredicate(t =>
            t.ContainingNamespace.IsGlobalNamespace ||
            t.ContainingNamespace.ToDisplayString() != namespaceName);
        return this;
    }

    /// <summary>
    /// Exclude types in namespaces starting with the specified prefix.
    /// </summary>
    public TypeQuery NotInNamespaceStartingWith(string namespacePrefix)
    {
        ApplyPredicate(t =>
            t.ContainingNamespace.IsGlobalNamespace ||
            !t.ContainingNamespace.ToDisplayString().StartsWith(namespacePrefix, StringComparison.Ordinal));
        return this;
    }

    /// <summary>
    /// Filter to types in any of the specified namespaces.
    /// </summary>
    public TypeQuery InAnyNamespace(params string[] namespaces)
    {
        var nsSet = new HashSet<string>(namespaces);
        ApplyPredicate(t =>
            !t.ContainingNamespace.IsGlobalNamespace &&
            nsSet.Contains(t.ContainingNamespace.ToDisplayString()));
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
        _attributePatterns.Add(attributeFullName);
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

    /// <summary>
    /// Filter to types with any of the specified attributes.
    /// </summary>
    public TypeQuery WithAnyAttribute(params string[] attributeFullNames)
    {
        foreach (var name in attributeFullNames)
            _attributePatterns.Add(name);

        _requireAllAttributes = false;
        _syntaxPredicates.Add(static node =>
            node is TypeDeclarationSyntax tds && tds.AttributeLists.Count > 0);
        return this;
    }

    /// <summary>
    /// Filter to types with all of the specified attributes.
    /// </summary>
    public TypeQuery WithAllAttributes(params string[] attributeFullNames)
    {
        foreach (var name in attributeFullNames)
            _attributePatterns.Add(name);

        _requireAllAttributes = true;
        _syntaxPredicates.Add(static node =>
            node is TypeDeclarationSyntax tds && tds.AttributeLists.Count > 0);
        return this;
    }

    /// <summary>
    /// Exclude types with the specified attribute.
    /// </summary>
    public TypeQuery WithoutAttribute(string attributeFullName)
    {
        _excludedAttributePatterns.Add(attributeFullName);
        return this;
    }

    /// <summary>
    /// Exclude types with the specified attribute type.
    /// </summary>
    public TypeQuery WithoutAttribute<TAttribute>() where TAttribute : Attribute
    {
        return WithoutAttribute(typeof(TAttribute).FullName!);
    }

    /// <summary>
    /// Filter to types with the specified attribute where the attribute matches a predicate.
    /// </summary>
    public TypeQuery WithAttributeWhere(string attributeFullName, Func<AttributeData, bool> predicate)
    {
        _attributePatterns.Add(attributeFullName);
        ApplyPredicate(t =>
        {
            var attr = FindMatchingAttribute(t, attributeFullName);
            return attr is not null && predicate(attr);
        });
        _syntaxPredicates.Add(static node =>
            node is TypeDeclarationSyntax tds && tds.AttributeLists.Count > 0);
        return this;
    }

    /// <summary>
    /// Filter to types with at least the specified number of attributes.
    /// </summary>
    public TypeQuery WithAttributeCountAtLeast(int count)
    {
        ApplyPredicate(t => t.GetAttributes().Length >= count);
        return this;
    }

    #endregion

    #region Interface Filters

    /// <summary>
    /// Filter to types implementing the specified interface.
    /// Use angle brackets for generic interfaces: "IMyInterface&lt;,&gt;"
    /// </summary>
    public TypeQuery Implementing(string interfaceFullName)
    {
        _interfacePatterns.Add(interfaceFullName);
        return this;
    }

    /// <summary>
    /// Filter to types implementing the specified interface type.
    /// </summary>
    public TypeQuery Implementing<TInterface>()
    {
        return Implementing(typeof(TInterface).FullName!);
    }

    /// <summary>
    /// Filter to types implementing any of the specified interfaces.
    /// </summary>
    public TypeQuery ImplementingAny(params string[] interfaceFullNames)
    {
        foreach (var name in interfaceFullNames)
            _interfacePatterns.Add(name);

        _requireAllInterfaces = false;
        return this;
    }

    /// <summary>
    /// Filter to types implementing all of the specified interfaces.
    /// </summary>
    public TypeQuery ImplementingAll(params string[] interfaceFullNames)
    {
        foreach (var name in interfaceFullNames)
            _interfacePatterns.Add(name);

        _requireAllInterfaces = true;
        return this;
    }

    /// <summary>
    /// Exclude types implementing the specified interface.
    /// </summary>
    public TypeQuery NotImplementing(string interfaceFullName)
    {
        _excludedInterfacePatterns.Add(interfaceFullName);
        return this;
    }

    /// <summary>
    /// Exclude types implementing the specified interface type.
    /// </summary>
    public TypeQuery NotImplementing<TInterface>()
    {
        return NotImplementing(typeof(TInterface).FullName!);
    }

    /// <summary>
    /// Filter to types directly implementing the specified interface (not inherited from base).
    /// </summary>
    public TypeQuery DirectlyImplementing(string interfaceFullName)
    {
        ApplyPredicate(t =>
        {
            foreach (var iface in t.Interfaces) // Interfaces, not AllInterfaces
            {
                if (SymbolExtensions.MatchesTypeName(iface.OriginalDefinition.ToDisplayString(), interfaceFullName))
                    return true;
            }
            return false;
        });
        return this;
    }

    /// <summary>
    /// Filter to types implementing at least the specified number of interfaces.
    /// </summary>
    public TypeQuery ImplementingCountAtLeast(int count)
    {
        ApplyPredicate(t => t.AllInterfaces.Length >= count);
        return this;
    }

    #endregion

    #region Generic Type Filters

    /// <summary>
    /// Filter to generic types.
    /// </summary>
    public TypeQuery ThatAreGeneric()
    {
        ApplyPredicate(t => t.IsGenericType);
        return this;
    }

    /// <summary>
    /// Filter to non-generic types.
    /// </summary>
    public TypeQuery ThatAreNonGeneric()
    {
        ApplyPredicate(t => !t.IsGenericType);
        return this;
    }

    /// <summary>
    /// Filter to types with exactly the specified number of type parameters.
    /// </summary>
    public TypeQuery WithTypeParameterCount(int count)
    {
        ApplyPredicate(t => t.TypeParameters.Length == count);
        return this;
    }

    /// <summary>
    /// Filter to types with at least the specified number of type parameters.
    /// </summary>
    public TypeQuery WithTypeParameterCountAtLeast(int count)
    {
        ApplyPredicate(t => t.TypeParameters.Length >= count);
        return this;
    }

    /// <summary>
    /// Filter to types with a type parameter with the specified name.
    /// </summary>
    public TypeQuery WithTypeParameter(string name)
    {
        ApplyPredicate(t => t.TypeParameters.Any(tp => tp.Name == name));
        return this;
    }

    /// <summary>
    /// Filter to types with any constrained type parameters.
    /// </summary>
    public TypeQuery WithConstrainedTypeParameters()
    {
        ApplyPredicate(t => t.TypeParameters.Any(tp =>
            tp.HasConstructorConstraint ||
            tp.HasReferenceTypeConstraint ||
            tp.HasValueTypeConstraint ||
            tp.HasNotNullConstraint ||
            tp.HasUnmanagedTypeConstraint ||
            tp.ConstraintTypes.Length > 0));
        return this;
    }

    #endregion

    #region Nesting Filters

    /// <summary>
    /// Filter to nested types (inside another type).
    /// </summary>
    public TypeQuery ThatAreNested()
    {
        ApplyPredicate(t => t.ContainingType is not null);
        return this;
    }

    /// <summary>
    /// Filter to top-level types (not nested).
    /// </summary>
    public TypeQuery ThatAreTopLevel()
    {
        ApplyPredicate(t => t.ContainingType is null);
        return this;
    }

    /// <summary>
    /// Filter to types nested in a type with the specified name.
    /// </summary>
    public TypeQuery NestedIn(string containingTypeName)
    {
        ApplyPredicate(t =>
            t.ContainingType is not null &&
            t.ContainingType.Name == containingTypeName);
        return this;
    }

    /// <summary>
    /// Filter to types nested in a type matching the predicate.
    /// </summary>
    public TypeQuery NestedInTypeMatching(Func<INamedTypeSymbol, bool> predicate)
    {
        ApplyPredicate(t =>
            t.ContainingType is not null &&
            predicate(t.ContainingType));
        return this;
    }

    /// <summary>
    /// Filter to types that have nested types.
    /// </summary>
    public TypeQuery WithNestedTypes()
    {
        ApplyPredicate(t => t.GetTypeMembers().Length > 0);
        return this;
    }

    /// <summary>
    /// Filter to types that have a nested type with the specified name.
    /// </summary>
    public TypeQuery WithNestedType(string nestedTypeName)
    {
        ApplyPredicate(t => t.GetTypeMembers(nestedTypeName).Length > 0);
        return this;
    }

    #endregion

    #region Source Location Filters

    /// <summary>
    /// Filter to types in a file with the specified name.
    /// </summary>
    public TypeQuery InFile(string fileName)
    {
        ApplySyntaxPredicate(node =>
            node.SyntaxTree.FilePath.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        return this;
    }

    /// <summary>
    /// Filter to types in files matching the specified pattern (supports * and ?).
    /// </summary>
    public TypeQuery InFileMatching(string pattern)
    {
        var regex = GlobToRegex(pattern);
        ApplySyntaxPredicate(node =>
            regex.IsMatch(Path.GetFileName(node.SyntaxTree.FilePath)));
        return this;
    }

    /// <summary>
    /// Filter to types in files whose path contains the specified substring.
    /// </summary>
    public TypeQuery InFilePath(string pathSubstring)
    {
        ApplySyntaxPredicate(node =>
            node.SyntaxTree.FilePath.Contains(pathSubstring));
        return this;
    }

    /// <summary>
    /// Exclude types in generated code files (*.g.cs, *.generated.cs, *.designer.cs).
    /// </summary>
    public TypeQuery NotInGeneratedCode()
    {
        ApplySyntaxPredicate(node =>
        {
            var filePath = node.SyntaxTree.FilePath;
            return !filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) &&
                   !filePath.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) &&
                   !filePath.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase);
        });
        return this;
    }

    /// <summary>
    /// Filter with a custom syntax tree predicate.
    /// </summary>
    public TypeQuery InSyntaxTree(Func<SyntaxTree, bool> predicate)
    {
        ApplySyntaxPredicate(node => predicate(node.SyntaxTree));
        return this;
    }

    #endregion

    #region Assembly Filters

    /// <summary>
    /// Filter to types defined in the assembly being compiled (not referenced assemblies).
    /// </summary>
    public TypeQuery InCurrentAssembly()
    {
        ApplyPredicate(t => SymbolEqualityComparer.Default.Equals(
            t.ContainingAssembly,
            t.ContainingModule?.ContainingAssembly));
        return this;
    }

    /// <summary>
    /// Filter to types from a referenced assembly with the specified name.
    /// </summary>
    public TypeQuery InReferencedAssembly(string assemblyName)
    {
        ApplyPredicate(t =>
            t.ContainingAssembly?.Name.Equals(assemblyName, StringComparison.OrdinalIgnoreCase) == true);
        return this;
    }

    /// <summary>
    /// Filter to types from assemblies matching the specified pattern (supports * wildcard).
    /// </summary>
    public TypeQuery InAssemblyMatching(string pattern)
    {
        var regex = GlobToRegex(pattern);
        ApplyPredicate(t =>
            t.ContainingAssembly?.Name is { } name && regex.IsMatch(name));
        return this;
    }

    /// <summary>
    /// Exclude types from assemblies with the specified name.
    /// </summary>
    public TypeQuery NotInAssembly(string assemblyName)
    {
        ApplyPredicate(t =>
            t.ContainingAssembly?.Name.Equals(assemblyName, StringComparison.OrdinalIgnoreCase) != true);
        return this;
    }

    /// <summary>
    /// Exclude types from assemblies matching the specified pattern.
    /// </summary>
    public TypeQuery NotInAssemblyMatching(string pattern)
    {
        var regex = GlobToRegex(pattern);
        ApplyPredicate(t =>
            t.ContainingAssembly?.Name is not { } name || !regex.IsMatch(name));
        return this;
    }

    /// <summary>
    /// Exclude types from System.* assemblies.
    /// </summary>
    public TypeQuery NotInSystemAssemblies()
    {
        ApplyPredicate(t =>
            t.ContainingAssembly?.Name is not { } name ||
            (!name.StartsWith("System", StringComparison.Ordinal) &&
             !name.StartsWith("Microsoft", StringComparison.Ordinal) &&
             !name.Equals("mscorlib", StringComparison.Ordinal) &&
             !name.Equals("netstandard", StringComparison.Ordinal)));
        return this;
    }

    #endregion

    #region Low-Level / Raw Access

    /// <summary>
    /// Add a custom syntax-level predicate.
    /// </summary>
    public TypeQuery WithSyntax(Func<TypeDeclarationSyntax, bool> predicate)
    {
        ApplySyntaxPredicate(node => node is TypeDeclarationSyntax tds && predicate(tds));
        return this;
    }

    /// <summary>
    /// Add a custom semantic-level predicate.
    /// </summary>
    public TypeQuery Where(Func<INamedTypeSymbol, bool> predicate)
    {
        ApplyPredicate(predicate);
        return this;
    }

    /// <summary>
    /// Add a custom predicate with access to both syntax and symbol.
    /// </summary>
    public TypeQuery Where(Func<TypeDeclarationSyntax, INamedTypeSymbol, bool> predicate)
    {
        _combinedPredicates.Add(predicate);
        return this;
    }

    #endregion

    #region Grouping Operations

    /// <summary>
    /// Group matching types by a key selector.
    /// Useful for generating files per namespace, per assembly, or other groupings.
    /// </summary>
    public GroupedTypeQuery<TKey> GroupBy<TKey>(Func<INamedTypeSymbol, TKey> keySelector) where TKey : notnull
    {
        return new GroupedTypeQuery<TKey>(Build(), keySelector, _context);
    }

    /// <summary>
    /// Group matching types by a key selector with a custom comparer.
    /// </summary>
    public GroupedTypeQuery<TKey> GroupBy<TKey>(Func<INamedTypeSymbol, TKey> keySelector, IEqualityComparer<TKey> comparer) where TKey : notnull
    {
        return new GroupedTypeQuery<TKey>(Build(), keySelector, _context, comparer);
    }

    /// <summary>
    /// Group matching types by namespace.
    /// </summary>
    public GroupedTypeQuery<string> GroupByNamespace()
    {
        return GroupBy(t => t.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : t.ContainingNamespace.ToDisplayString());
    }

    /// <summary>
    /// Group matching types by assembly name.
    /// </summary>
    public GroupedTypeQuery<string> GroupByAssembly()
    {
        return GroupBy(t => t.ContainingAssembly?.Name ?? string.Empty);
    }

    #endregion

    #region Projection Operations

    /// <summary>
    /// Project each matching type to a new value.
    /// </summary>
    public ProjectedTypeQuery<T> Select<T>(Func<INamedTypeSymbol, T> selector)
    {
        var provider = Build();
        var projected = provider.Select((result, _) =>
            result.Symbol is not null
                ? new ProjectedItem<T>(result.Symbol, selector(result.Symbol))
                : new ProjectedItem<T>(null, default));

        return new ProjectedTypeQuery<T>(projected, _context);
    }

    /// <summary>
    /// Project each matching type with attribute data.
    /// </summary>
    public ProjectedTypeQuery<T> Select<T>(Func<INamedTypeSymbol, AttributeMatch, T> selector)
    {
        if (_attributePatterns.Count == 0)
            throw new InvalidOperationException("WithAttribute must be called before using this overload");

        var provider = Build();
        var projected = provider.Select((result, _) =>
            result.Symbol is not null && result.Attributes.Count > 0
                ? new ProjectedItem<T>(result.Symbol, selector(result.Symbol, new AttributeMatch(result.Attributes[0])))
                : new ProjectedItem<T>(null, default));

        return new ProjectedTypeQuery<T>(projected, _context);
    }

    /// <summary>
    /// Project each matching type with interface data.
    /// </summary>
    public ProjectedTypeQuery<T> Select<T>(Func<INamedTypeSymbol, InterfaceMatch, T> selector)
    {
        if (_interfacePatterns.Count == 0)
            throw new InvalidOperationException("Implementing must be called before using this overload");

        var provider = Build();
        var projected = provider.Select((result, _) =>
            result.Symbol is not null && result.Interfaces.Count > 0
                ? new ProjectedItem<T>(result.Symbol, selector(result.Symbol, new InterfaceMatch(result.Interfaces[0])))
                : new ProjectedItem<T>(null, default));

        return new ProjectedTypeQuery<T>(projected, _context);
    }

    /// <summary>
    /// Project each matching type to multiple values and flatten.
    /// </summary>
    public FlattenedTypeQuery<T> SelectMany<T>(Func<INamedTypeSymbol, IEnumerable<T>> selector)
    {
        var provider = Build();
        var flattened = provider.SelectMany((result, _) =>
        {
            if (result.Symbol is null)
                return Enumerable.Empty<FlattenedItem<T>>();

            return selector(result.Symbol)
                .Select(value => new FlattenedItem<T>(result.Symbol, value));
        });

        return new FlattenedTypeQuery<T>(flattened, _context);
    }

    /// <summary>
    /// Project each matching type with attribute data to multiple values and flatten.
    /// </summary>
    public FlattenedTypeQuery<T> SelectMany<T>(Func<INamedTypeSymbol, AttributeMatch, IEnumerable<T>> selector)
    {
        if (_attributePatterns.Count == 0)
            throw new InvalidOperationException("WithAttribute must be called before using this overload");

        var provider = Build();
        var flattened = provider.SelectMany((result, _) =>
        {
            if (result.Symbol is null || result.Attributes.Count == 0)
                return Enumerable.Empty<FlattenedItem<T>>();

            return selector(result.Symbol, new AttributeMatch(result.Attributes[0]))
                .Select(value => new FlattenedItem<T>(result.Symbol, value));
        });

        return new FlattenedTypeQuery<T>(flattened, _context);
    }

    #endregion

    #region Generate Methods (Terminal Operations)

    /// <summary>
    /// Generate source code for each matching type.
    /// </summary>
    public void Generate(Func<INamedTypeSymbol, string?> generator, string? suffix = null)
    {
        var provider = Build();
        var ctx = _context;

        _context.EnqueueRegistration(() =>
        {
            ctx.RoslynContext.RegisterSourceOutput(provider, (spc, result) =>
            {
                if (result.Symbol is null) return;
                try
                {
                    var source = generator(result.Symbol);
                    if (source is null) return;
                    ctx.AddSource(spc, ctx.GetHintName(result.Symbol, suffix), source);
                }
                catch (Exception ex)
                {
                    ctx.ReportException(spc, result.Symbol.Name, ex, result.Symbol.Locations.FirstOrDefault());
                }
            });
        });
    }

    /// <summary>
    /// Generate source code for each matching type with attribute data.
    /// </summary>
    public void Generate(Func<INamedTypeSymbol, AttributeMatch, string?> generator, string? suffix = null)
    {
        var provider = Build();
        var ctx = _context;

        _context.EnqueueRegistration(() =>
        {
            ctx.RoslynContext.RegisterSourceOutput(provider, (spc, result) =>
            {
                if (result.Symbol is null || result.Attributes.Count == 0) return;
                try
                {
                    var source = generator(result.Symbol, new AttributeMatch(result.Attributes[0]));
                    if (source is null) return;
                    ctx.AddSource(spc, ctx.GetHintName(result.Symbol, suffix), source);
                }
                catch (Exception ex)
                {
                    ctx.ReportException(spc, result.Symbol.Name, ex, result.Symbol.Locations.FirstOrDefault());
                }
            });
        });
    }

    /// <summary>
    /// Generate source code for each matching type with interface data.
    /// </summary>
    public void Generate(Func<INamedTypeSymbol, InterfaceMatch, string?> generator, string? suffix = null)
    {
        var provider = Build();
        var ctx = _context;

        _context.EnqueueRegistration(() =>
        {
            ctx.RoslynContext.RegisterSourceOutput(provider, (spc, result) =>
            {
                if (result.Symbol is null || result.Interfaces.Count == 0) return;
                try
                {
                    var source = generator(result.Symbol, new InterfaceMatch(result.Interfaces[0]));
                    if (source is null) return;
                    ctx.AddSource(spc, ctx.GetHintName(result.Symbol, suffix), source);
                }
                catch (Exception ex)
                {
                    ctx.ReportException(spc, result.Symbol.Name, ex, result.Symbol.Locations.FirstOrDefault());
                }
            });
        });
    }

    /// <summary>
    /// Generate source code for each matching type with both attribute and interface data.
    /// </summary>
    public void Generate(Func<INamedTypeSymbol, AttributeMatch, InterfaceMatch, string?> generator, string? suffix = null)
    {
        var provider = Build();
        var ctx = _context;

        _context.EnqueueRegistration(() =>
        {
            ctx.RoslynContext.RegisterSourceOutput(provider, (spc, result) =>
            {
                if (result.Symbol is null || result.Attributes.Count == 0 || result.Interfaces.Count == 0) return;
                try
                {
                    var source = generator(result.Symbol, new AttributeMatch(result.Attributes[0]), new InterfaceMatch(result.Interfaces[0]));
                    if (source is null) return;
                    ctx.AddSource(spc, ctx.GetHintName(result.Symbol, suffix), source);
                }
                catch (Exception ex)
                {
                    ctx.ReportException(spc, result.Symbol.Name, ex, result.Symbol.Locations.FirstOrDefault());
                }
            });
        });
    }

    /// <summary>
    /// Collect all matching types and generate a single source file.
    /// </summary>
    public void GenerateAll(Func<IReadOnlyList<INamedTypeSymbol>, (string HintName, string Source)?> generator)
    {
        var provider = Build();
        var ctx = _context;

        _context.EnqueueRegistration(() =>
        {
            ctx.RoslynContext.RegisterSourceOutput(provider.Collect(), (spc, results) =>
            {
                var symbols = results.Where(r => r.Symbol is not null).Select(r => r.Symbol!).ToList();
                if (symbols.Count == 0) return;
                try
                {
                    var result = generator(symbols);
                    if (result is null) return;
                    ctx.AddSource(spc, result.Value.HintName, result.Value.Source);
                }
                catch (Exception ex)
                {
                    ctx.ReportException(spc, "collection", ex);
                }
            });
        });
    }

    /// <summary>
    /// Collect all matching types with attribute data and generate a single source file.
    /// </summary>
    public void GenerateAll(Func<IReadOnlyList<(INamedTypeSymbol Symbol, AttributeMatch Attribute)>, (string HintName, string Source)?> generator)
    {
        var provider = Build();
        var ctx = _context;

        _context.EnqueueRegistration(() =>
        {
            ctx.RoslynContext.RegisterSourceOutput(provider.Collect(), (spc, results) =>
            {
                var items = results
                    .Where(r => r.Symbol is not null && r.Attributes.Count > 0)
                    .Select(r => (r.Symbol!, new AttributeMatch(r.Attributes[0])))
                    .ToList();
                if (items.Count == 0) return;
                try
                {
                    var result = generator(items);
                    if (result is null) return;
                    ctx.AddSource(spc, result.Value.HintName, result.Value.Source);
                }
                catch (Exception ex)
                {
                    ctx.ReportException(spc, "collection", ex);
                }
            });
        });
    }

    #endregion

    #region Build Method

    /// <summary>
    /// Builds the query and returns the incremental values provider.
    /// For advanced scenarios only - prefer using Generate() methods.
    /// </summary>
    public IncrementalValuesProvider<QueryResult> Build()
    {
        // Capture state for lambdas
        var syntaxPredicates = _syntaxPredicates.ToList();
        var semanticPredicates = _semanticPredicates.ToList();
        var combinedPredicates = _combinedPredicates.ToList();
        var attributePatterns = _attributePatterns.ToList();
        var interfacePatterns = _interfacePatterns.ToList();
        var excludedAttributePatterns = _excludedAttributePatterns.ToList();
        var excludedInterfacePatterns = _excludedInterfacePatterns.ToList();
        var requireAllAttributes = _requireAllAttributes;
        var requireAllInterfaces = _requireAllInterfaces;

        return _syntaxProvider.CreateSyntaxProvider(
            predicate: (node, _) =>
            {
                if (node is not TypeDeclarationSyntax)
                    return false;

                foreach (var predicate in syntaxPredicates)
                {
                    if (!predicate(node))
                        return false;
                }

                return true;
            },
            transform: (ctx, _) =>
            {
                if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node) is not INamedTypeSymbol symbol)
                    return default;

                var syntax = (TypeDeclarationSyntax)ctx.Node;

                // Apply semantic predicates
                foreach (var predicate in semanticPredicates)
                {
                    if (!predicate(symbol))
                        return default;
                }

                // Apply combined predicates
                foreach (var predicate in combinedPredicates)
                {
                    if (!predicate(syntax, symbol))
                        return default;
                }

                // Find matching attributes
                var matchedAttributes = new List<AttributeData>();
                if (attributePatterns.Count > 0)
                {
                    foreach (var pattern in attributePatterns)
                    {
                        var attr = FindMatchingAttribute(symbol, pattern);
                        if (attr is not null)
                            matchedAttributes.Add(attr);
                    }

                    if (requireAllAttributes && matchedAttributes.Count != attributePatterns.Count)
                        return default;
                    if (!requireAllAttributes && matchedAttributes.Count == 0)
                        return default;
                }

                // Check excluded attributes
                foreach (var pattern in excludedAttributePatterns)
                {
                    if (FindMatchingAttribute(symbol, pattern) is not null)
                        return default;
                }

                // Find matching interfaces
                var matchedInterfaces = new List<INamedTypeSymbol>();
                if (interfacePatterns.Count > 0)
                {
                    foreach (var pattern in interfacePatterns)
                    {
                        var iface = FindMatchingInterface(symbol, pattern);
                        if (iface is not null)
                            matchedInterfaces.Add(iface);
                    }

                    if (requireAllInterfaces && matchedInterfaces.Count != interfacePatterns.Count)
                        return default;
                    if (!requireAllInterfaces && matchedInterfaces.Count == 0)
                        return default;
                }

                // Check excluded interfaces
                foreach (var pattern in excludedInterfacePatterns)
                {
                    if (FindMatchingInterface(symbol, pattern) is not null)
                        return default;
                }

                return new QueryResult(symbol, matchedAttributes, matchedInterfaces);
            }
        ).Where(static result => result.Symbol is not null);
    }

    #endregion

    #region Internal Helpers

    static bool MatchesTypeKind(INamedTypeSymbol symbol, TypeKind kind)
    {
        if (kind == TypeKind.None)
            return true;

        var symbolKind = symbol switch
        {
            { IsRecord: true, TypeKind: Microsoft.CodeAnalysis.TypeKind.Class } => TypeKind.RecordClass,
            { IsRecord: true, TypeKind: Microsoft.CodeAnalysis.TypeKind.Struct } => TypeKind.RecordStruct,
            { TypeKind: Microsoft.CodeAnalysis.TypeKind.Class } => TypeKind.Class,
            { TypeKind: Microsoft.CodeAnalysis.TypeKind.Struct } => TypeKind.Struct,
            { TypeKind: Microsoft.CodeAnalysis.TypeKind.Interface } => TypeKind.Interface,
            { TypeKind: Microsoft.CodeAnalysis.TypeKind.Enum } => TypeKind.Enum,
            { TypeKind: Microsoft.CodeAnalysis.TypeKind.Delegate } => TypeKind.Delegate,
            _ => TypeKind.None
        };

        return (kind & symbolKind) != 0;
    }

    static bool MatchesModifiers(INamedTypeSymbol symbol, TypeModifiers modifiers)
    {
        if (modifiers == TypeModifiers.None)
            return true;

        if ((modifiers & TypeModifiers.Static) != 0 && !symbol.IsStatic)
            return false;
        if ((modifiers & TypeModifiers.Abstract) != 0 && !symbol.IsAbstract)
            return false;
        if ((modifiers & TypeModifiers.Sealed) != 0 && !symbol.IsSealed)
            return false;
        if ((modifiers & TypeModifiers.Readonly) != 0 && !symbol.IsReadOnly)
            return false;
        if ((modifiers & TypeModifiers.Ref) != 0 && !symbol.IsRefLikeType)
            return false;

        // Partial requires syntax check - handled separately
        if ((modifiers & TypeModifiers.Partial) != 0)
        {
            var isPartial = symbol.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax())
                .OfType<TypeDeclarationSyntax>()
                .Any(s => s.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)));
            if (!isPartial)
                return false;
        }

        return true;
    }

    static bool MatchesAccessibility(INamedTypeSymbol symbol, TypeAccessibility accessibility)
    {
        if (accessibility == TypeAccessibility.None)
            return true;

        var symbolAccessibility = symbol.DeclaredAccessibility switch
        {
            Accessibility.Private => TypeAccessibility.Private,
            Accessibility.Protected => TypeAccessibility.Protected,
            Accessibility.Internal => TypeAccessibility.Internal,
            Accessibility.ProtectedOrInternal => TypeAccessibility.ProtectedInternal,
            Accessibility.ProtectedAndInternal => TypeAccessibility.PrivateProtected,
            Accessibility.Public => TypeAccessibility.Public,
            _ => TypeAccessibility.None
        };

        return (accessibility & symbolAccessibility) != 0;
    }

    static bool IsDerivedFrom(INamedTypeSymbol symbol, string pattern, bool transitive)
    {
        if (transitive)
        {
            var current = symbol.BaseType;
            while (current is not null)
            {
                if (SymbolExtensions.MatchesTypeName(current.OriginalDefinition.ToDisplayString(), pattern))
                    return true;
                current = current.BaseType;
            }
        }
        else
        {
            if (symbol.BaseType is not null &&
                SymbolExtensions.MatchesTypeName(symbol.BaseType.OriginalDefinition.ToDisplayString(), pattern))
                return true;
        }

        return false;
    }

    internal static AttributeData? FindMatchingAttribute(INamedTypeSymbol symbol, string pattern)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass is not { } attrClass)
                continue;

            var attrFullName = attrClass.OriginalDefinition.ToDisplayString();

            if (SymbolExtensions.MatchesTypeName(attrFullName, pattern))
                return attr;
        }

        return null;
    }

    internal static INamedTypeSymbol? FindMatchingInterface(INamedTypeSymbol symbol, string pattern)
    {
        foreach (var iface in symbol.AllInterfaces)
        {
            var ifaceFullName = iface.OriginalDefinition.ToDisplayString();

            if (SymbolExtensions.MatchesTypeName(ifaceFullName, pattern))
                return iface;
        }

        return null;
    }



    static Regex GlobToRegex(string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }

    #endregion

    /// <summary>
    /// Result of a type query containing the symbol and matched attributes/interfaces.
    /// </summary>
    public readonly record struct QueryResult(
        INamedTypeSymbol? Symbol,
        List<AttributeData> Attributes,
        List<INamedTypeSymbol> Interfaces)
    {
        /// <summary>
        /// Creates an empty query result.
        /// </summary>
        public QueryResult() : this(null, [], []) { }
    }
}
