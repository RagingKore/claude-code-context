# FluentSourceGen

A fluent API for building .NET source generators with minimal boilerplate.

## The Problem

Writing Roslyn source generators involves significant boilerplate:

- Custom syntax receivers or incremental predicates
- Manual semantic model querying
- Repetitive attribute/interface checking with string manipulation
- Complex namespace and containing type handling
- Verbose diagnostic descriptor definitions
- Manual file naming with collision handling

**A typical 50-line generator becomes 200+ lines.**

## The Solution

FluentSourceGen provides a fluent API that abstracts the ceremony while keeping Roslyn types (`INamedTypeSymbol`, `ITypeSymbol`, etc.) directly accessible.

## Quick Start

```csharp
using FluentSourceGen;
using Microsoft.CodeAnalysis;

[Generator]
public class MyGenerator : FluentGenerator
{
    protected override void Configure(GeneratorContext ctx)
    {
        ctx.Types
            .ThatAreRecords()
            .ThatArePartial()
            .WithAttribute("MyNamespace.GenerateAttribute")
            .ForEach((type, attr, emit) =>
            {
                emit.Source($"{type.Name}.g.cs", $$"""
                    {{type.GetNamespaceDeclaration()}}

                    partial record {{type.Name}}
                    {
                        public string Id => "{{type.Name}}";
                    }
                    """);
            });
    }
}
```

## Before & After Examples

### Example 1: ValueObjectGenerator

**Before (150+ lines):**
```csharp
[Generator]
public class ValueObjectGenerator : ISourceGenerator
{
    class ValueObjectSyntaxReceiver : ISyntaxReceiver
    {
        public List<TypeDeclarationSyntax> CandidateTypes { get; } = [];

        public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
        {
            if (syntaxNode is not TypeDeclarationSyntax typeDeclaration ||
                !typeDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)) ||
                typeDeclaration.AttributeLists.Count <= 0) return;

            if (typeDeclaration is ClassDeclarationSyntax or RecordDeclarationSyntax)
                CandidateTypes.Add(typeDeclaration);
        }
    }

    // ... 100+ more lines of boilerplate ...
}
```

**After (30 lines):**
```csharp
[Generator]
public class ValueObjectGenerator : FluentGenerator
{
    protected override void Configure(GeneratorContext ctx)
    {
        ctx.Types
            .ThatArePartial()
            .WithAttribute("Kurrent.ValueObjectAttribute<>")
            .ForEach((type, attr, emit) =>
            {
                var valueType = attr.TypeArgument(0);

                emit.Source(
                    new FileNamingOptions { Prefix = "ValueObjects" },
                    $$"""
                    {{type.GetNamespaceDeclaration()}}

                    readonly partial record struct {{type.Name}}
                    {
                        public {{valueType.FullName()}} Value { get; private init; }
                        public static implicit operator {{valueType.FullName()}}({{type.Name}} _) => _.Value;
                        public static implicit operator {{type.Name}}({{valueType.FullName()}} _) => new() { Value = _ };
                    }
                    """,
                    valueType);
            });
    }
}
```

### Example 2: Service Registry Generator (ForAll)

```csharp
[Generator]
public class ServiceRegistryGenerator : FluentGenerator
{
    protected override void Configure(GeneratorContext ctx)
    {
        ctx.Types
            .ThatAreClasses()
            .Implementing("IService")
            .WithAccessibility(TypeAccessibility.Public)
            .ForAll((types, emit) =>
            {
                var registrations = string.Join("\n            ",
                    types.Select(t => $"services.AddScoped<{t.FullName()}>();"));

                emit.Source("ServiceRegistry.g.cs", $$"""
                    using Microsoft.Extensions.DependencyInjection;

                    public static class ServiceRegistry
                    {
                        public static IServiceCollection AddGeneratedServices(this IServiceCollection services)
                        {
                            {{registrations}}
                            return services;
                        }
                    }
                    """);
            });
    }
}
```

## Complete API Reference

### Type Kind Filters

```csharp
ctx.Types
    .ThatAreClasses()              // Non-record classes
    .ThatAreStructs()              // Non-record structs
    .ThatAreRecords()              // Record class or record struct
    .ThatAreRecordClasses()        // Record classes only
    .ThatAreRecordStructs()        // Record structs only
    .ThatAreInterfaces()
    .ThatAreEnums()
    .ThatAreDelegates()
    .ThatAreReferenceTypes()       // Any reference type
    .ThatAreValueTypes()           // Any value type
    .OfKind(TypeKind.AnyClass)     // Using flags enum
```

### Modifier Filters

```csharp
ctx.Types
    .ThatArePartial()
    .ThatAreStatic()
    .ThatAreAbstract()
    .ThatAreSealed()
    .ThatAreReadonly()             // Readonly structs
    .ThatAreRefStructs()           // Ref structs
    .WithModifiers(TypeModifiers.Partial | TypeModifiers.Sealed)

    // Negation variants
    .ThatAreNotPartial()
    .ThatAreNotStatic()
    .ThatAreNotAbstract()          // Concrete types only
    .ThatAreNotSealed()
    .WithoutModifiers(TypeModifiers.Abstract | TypeModifiers.Static)
```

### Accessibility Filters

```csharp
ctx.Types
    .ThatArePublic()
    .ThatAreInternal()
    .WithAccessibility(TypeAccessibility.Public)
    .WithAccessibility(TypeAccessibility.PublicOrInternal)  // Combined
    .WithoutAccessibility(TypeAccessibility.Private)        // Exclusion
```

### Base Type / Inheritance Filters

```csharp
ctx.Types
    .DerivedFrom("MyNamespace.BaseClass")      // Direct or transitive
    .DerivedFrom<BaseClass>()                   // Generic version
    .DirectlyDerivedFrom("BaseClass")           // Direct parent only
    .NotDerivedFrom("BaseClass")                // Exclusion
```

### Member Filters

```csharp
ctx.Types
    .WithMembers()                              // Has any members
    .WithMethods()                              // Has any methods
    .WithMethod("MethodName")                   // Has specific method
    .WithMethodMatching(m => m.IsAsync)         // Custom method predicate
    .WithProperties()                           // Has any properties
    .WithProperty("PropertyName")               // Has specific property
    .WithPropertyMatching(p => p.IsRequired)    // Custom property predicate
    .WithPropertyOfType("System.String")        // Property of specific type
    .WithFields()                               // Has any fields
    .WithField("_fieldName")                    // Has specific field
    .WithFieldMatching(f => f.IsReadOnly)       // Custom field predicate
    .WithConstructor()                          // Has explicit constructor
    .WithParameterlessConstructor()             // Has default constructor
    .WithConstructorMatching(c => c.Parameters.Length == 2)
```

### Namespace Filters

```csharp
ctx.Types
    .InNamespace("MyNamespace")                 // Exact match
    .InNamespaceStartingWith("MyNamespace.")    // Prefix match
    .InNamespaceEndingWith(".Models")           // Suffix match
    .InNamespaceContaining("Domain")            // Contains substring
    .InNamespaceMatching(@"^My\..*\.Core$")     // Regex pattern
    .InGlobalNamespace()                        // Global namespace only
    .InAnyNamespace("NS1", "NS2", "NS3")        // Any of these
    .NotInNamespace("System")                   // Exclusion
    .NotInNamespaceStartingWith("Microsoft.")   // Exclude prefix
```

### Attribute Filters

```csharp
ctx.Types
    .WithAttribute("MyNamespace.MyAttribute")           // Has attribute
    .WithAttribute("MyAttribute<>")                     // Generic attribute
    .WithAttribute<ObsoleteAttribute>()                 // Compile-time type
    .WithAnyAttribute("Attr1", "Attr2")                 // Has ANY of these
    .WithAllAttributes("Attr1", "Attr2")                // Has ALL of these
    .WithoutAttribute("ObsoleteAttribute")              // Exclusion
    .WithAttributeWhere("MyAttr", a => a.ConstructorArguments.Length > 0)
    .WithAttributeCountAtLeast(2)                       // At least N attributes
```

### Interface Filters

```csharp
ctx.Types
    .Implementing("IMyInterface")                       // Implements interface
    .Implementing("IGeneric<>")                         // Generic interface
    .Implementing<IDisposable>()                        // Compile-time type
    .ImplementingAny("IFoo", "IBar")                    // Implements ANY
    .ImplementingAll("IFoo", "IBar")                    // Implements ALL
    .DirectlyImplementing("IFoo")                       // Not inherited from base
    .NotImplementing("IDisposable")                     // Exclusion
    .ImplementingCountAtLeast(2)                        // At least N interfaces
```

### Generic Type Filters

```csharp
ctx.Types
    .ThatAreGeneric()                                   // Is generic type
    .ThatAreNonGeneric()                                // Not generic
    .WithTypeParameterCount(2)                          // Exactly N type params
    .WithTypeParameterCountAtLeast(1)                   // At least N type params
    .WithTypeParameter("T")                             // Has type param named "T"
    .WithConstrainedTypeParameters()                    // Has any constraints
```

### Nesting Filters

```csharp
ctx.Types
    .ThatAreNested()                                    // Inside another type
    .ThatAreTopLevel()                                  // Not nested
    .NestedIn("ContainerClass")                         // Nested in specific type
    .NestedInTypeMatching(t => t.IsStatic)              // Custom predicate
    .WithNestedTypes()                                  // Has nested types
    .WithNestedType("InnerClass")                       // Has specific nested type
```

### Source Location Filters

```csharp
ctx.Types
    .InFile("MyFile.cs")                                // Specific file name
    .InFileMatching("*.Models.cs")                      // Glob pattern
    .InFilePath("src/Domain/")                          // Path contains
    .NotInGeneratedCode()                               // Exclude *.g.cs, etc.
    .InSyntaxTree(tree => /* custom predicate */)
```

### Assembly Filters

```csharp
ctx.Types
    .InCurrentAssembly()                                // Being compiled (not referenced)
    .InReferencedAssembly("Newtonsoft.Json")            // Specific referenced assembly
    .InAssemblyMatching("MyCompany.*")                  // Wildcard pattern
    .NotInAssembly("System.Private.CoreLib")            // Exclusion
    .NotInAssemblyMatching("Microsoft.*")               // Exclude pattern
    .NotInSystemAssemblies()                            // Exclude System.*, Microsoft.*, etc.
```

### Low-Level Access

```csharp
ctx.Types
    .Where((INamedTypeSymbol symbol) => /* custom */)
    .WithSyntax((TypeDeclarationSyntax syntax) => /* custom */)
    .Where((TypeDeclarationSyntax syntax, INamedTypeSymbol symbol) => /* both */)
```

### Terminal Operations

```csharp
// Basic - just the symbol
.ForEach((INamedTypeSymbol type, SourceEmitter emit) => { ... });

// With single attribute
.ForEach((INamedTypeSymbol type, AttributeMatch attr, SourceEmitter emit) => { ... });

// With multiple attributes (WithAllAttributes)
.ForEach((INamedTypeSymbol type, IReadOnlyList<AttributeMatch> attrs, SourceEmitter emit) => { ... });

// With single interface
.ForEach((INamedTypeSymbol type, InterfaceMatch iface, SourceEmitter emit) => { ... });

// With multiple interfaces (ImplementingAll)
.ForEach((INamedTypeSymbol type, IReadOnlyList<InterfaceMatch> ifaces, SourceEmitter emit) => { ... });

// With both attribute AND interface
.ForEach((INamedTypeSymbol type, AttributeMatch attr, InterfaceMatch iface, SourceEmitter emit) => { ... });

// Collect all matches and process together
.ForAll((IReadOnlyList<INamedTypeSymbol> types, CollectionEmitter emit) => { ... });

// Collect with attributes
.ForAll((IReadOnlyList<(INamedTypeSymbol, AttributeMatch)> items, CollectionEmitter emit) => { ... });
```

### Grouping Operations

Group types by key and process each group separately:

```csharp
// Group by namespace - generate one file per namespace
ctx.Types
    .ThatAreClasses()
    .WithAttribute("ServiceAttribute")
    .GroupByNamespace()
    .ForEachGroup((ns, types, emit) =>
    {
        var registrations = string.Join("\n",
            types.Select(t => $"    services.AddScoped<{t.FullName()}>();"));

        emit.Source($"{ns.Replace(".", "/")}/Services.g.cs", $$"""
            namespace {{ns}};

            public static partial class Services
            {
                public static void Register(IServiceCollection services)
                {
                    {{registrations}}
                }
            }
            """);
    });

// Group by custom key
ctx.Types
    .ThatAreRecords()
    .GroupBy(t => t.BaseType?.Name ?? "Object")
    .ForEachGroup((baseTypeName, types, emit) => { ... });

// Group by assembly
ctx.Types
    .Implementing("IPlugin")
    .GroupByAssembly()
    .ForEachGroup((assembly, types, emit) => { ... });

// Filter groups
ctx.Types
    .GroupByNamespace()
    .WhereGroup(ns => ns.StartsWith("MyApp."))
    .ForEachGroup((ns, types, emit) => { ... });

// Order groups
ctx.Types
    .GroupByNamespace()
    .OrderByKey()
    .ForEachGroup((ns, types, emit) => { ... });
```

### Projection Operations

Transform types before terminal operations:

```csharp
// Project to extract specific data
ctx.Types
    .ThatAreClasses()
    .Select(t => new
    {
        Type = t,
        Properties = t.GetMembers().OfType<IPropertySymbol>().ToList(),
        HasId = t.GetMembers().Any(m => m.Name == "Id")
    })
    .ForEach((data, emit) =>
    {
        // data.Type, data.Properties, data.HasId available
    });

// Project with attribute data
ctx.Types
    .WithAttribute("TableAttribute")
    .Select((type, attr) => new
    {
        Type = type,
        TableName = attr.TryGetConstructorArgument<string>(0, out var name) ? name : type.Name
    })
    .ForEach((data, emit) => { ... });

// SelectMany - flatten and extract members
ctx.Types
    .ThatAreClasses()
    .SelectMany(t => t.GetMembers().OfType<IPropertySymbol>())
    .Distinct()
    .ForAll((allProperties, emit) =>
    {
        // Generate based on all properties across all types
    });

// Chain projections
ctx.Types
    .Implementing("IEntity")
    .Select(t => new { t.Name, Namespace = t.GetNamespace() })
    .GroupBy(x => x.Namespace)
    .ForEachGroup((ns, items, emit) => { ... });
```

### Enums

```csharp
// TypeAccessibility - flags enum
TypeAccessibility.Public
TypeAccessibility.Internal
TypeAccessibility.Private
TypeAccessibility.Protected
TypeAccessibility.ProtectedInternal
TypeAccessibility.PrivateProtected
TypeAccessibility.PublicOrInternal     // Combined
TypeAccessibility.AnyProtected         // All protected variants
TypeAccessibility.Any                  // All levels

// TypeModifiers - flags enum
TypeModifiers.Partial
TypeModifiers.Static
TypeModifiers.Abstract
TypeModifiers.Sealed
TypeModifiers.Readonly
TypeModifiers.Ref

// TypeKind - flags enum
TypeKind.Class
TypeKind.Struct
TypeKind.RecordClass
TypeKind.RecordStruct
TypeKind.Interface
TypeKind.Enum
TypeKind.Delegate
TypeKind.AnyClass          // Class | RecordClass
TypeKind.AnyStruct         // Struct | RecordStruct
TypeKind.AnyRecord         // RecordClass | RecordStruct
TypeKind.AnyReferenceType  // Class | RecordClass | Interface | Delegate
TypeKind.AnyValueType      // Struct | RecordStruct | Enum
```

### AttributeMatch

```csharp
var valueType = attr.TypeArgument(0);           // Get type argument
var allTypeArgs = attr.TypeArguments;           // IReadOnlyList<ITypeSymbol>
attr.TryGetTypeArgument(0, out var type);       // Safe access

var arg = attr.ConstructorArgument(0);          // TypedConstant
attr.TryGetConstructorArgument<string>(0, out var value);

var named = attr.NamedArgument("Option");       // TypedConstant?
attr.TryGetNamedArgument<bool>("Enabled", out var enabled);

AttributeData data = attr.Data;                 // Raw Roslyn type
```

### InterfaceMatch

```csharp
var tValue = iface.TypeArgument(0);             // Get type argument
var allTypeArgs = iface.TypeArguments;          // IReadOnlyList<ITypeSymbol>
iface.TryGetTypeArgument(0, out var type);      // Safe access

string name = iface.Name;                       // "IMyInterface"
string fullName = iface.FullName;               // "global::MyNamespace.IMyInterface"
INamedTypeSymbol symbol = iface.Symbol;         // Raw Roslyn type

// Extract variant types from nested interfaces
var variants = InterfaceMatch.ExtractVariantTypes(errorType, "IVariantException<");
```

### SourceEmitter

```csharp
emit.Source("MyType.g.cs", sourceCode);                    // Simple emission
emit.Source(sourceCode);                                    // Auto-named
emit.Source(sourceCode, ".Operators");                      // With suffix

emit.Source(new FileNamingOptions {
    Prefix = "ValueObjects",
    UseFoldersForPrefix = true,
    UseFoldersForNamespace = true,
    LowercasePath = false
}, sourceCode, typeArgsForHash);

// Diagnostics
emit.ReportInfo("GEN001", "Title", "Message");
emit.ReportWarning("GEN002", "Title", "Message");
emit.ReportError("GEN003", "Title", "Message");
```

### Symbol Extensions

```csharp
type.GetNamespace()                  // "MyNamespace" or ""
type.GetNamespaceDeclaration()       // "namespace MyNamespace;" or "// Global namespace"
type.FullName()                      // "MyNamespace.MyType"
type.GlobalName()                    // "global::MyNamespace.MyType"
type.GetTypeKeyword()                // "record struct", "class", etc.
type.GetAccessibility()              // "public", "internal", etc.
type.GetModifiers()                  // "public partial readonly"
type.GetTypeDeclaration()            // "public partial record struct MyType"
type.IsPartial()                     // true/false
type.GetContainingTypes()            // List from outermost to innermost
type.FindAttribute("MyAttribute<>")
type.HasAttribute("MyAttribute")
type.FindInterface("IMyInterface<>")
type.ImplementsInterface("IMyInterface")
```

## Project Structure

```
prototypes/fluent-source-gen/
├── src/
│   └── FluentSourceGen/
│       ├── FluentGenerator.cs      # Base class and GeneratorContext
│       ├── TypeQuery.cs            # Fluent query builder (1600+ lines)
│       ├── TypeEnums.cs            # TypeAccessibility, TypeModifiers, TypeKind
│       ├── AttributeMatch.cs       # Attribute data wrapper
│       ├── InterfaceMatch.cs       # Interface data wrapper
│       ├── SourceEmitter.cs        # Code emission and file naming
│       ├── CollectionEmitter.cs    # Emitter for ForAll operations
│       ├── GroupedTypeQuery.cs     # Grouping operations (GroupBy, ForEachGroup)
│       ├── ProjectedTypeQuery.cs   # Projection operations (Select, SelectMany)
│       └── SymbolExtensions.cs     # INamedTypeSymbol extensions
├── tests/
│   └── FluentSourceGen.Tests/      # TUnit tests
│       ├── TypeEnumsTests.cs
│       ├── SymbolExtensionsTests.cs
│       ├── TypeFilteringTests.cs
│       ├── MatchWrapperTests.cs
│       ├── FileNamingTests.cs
│       ├── AssemblyFilteringTests.cs
│       └── GroupingAndProjectionTests.cs
├── FluentSourceGen.slnx            # Solution file
└── README.md                       # This file
```

## Prerequisites

- .NET 8+ SDK (or .NET Standard 2.0 compatible)
- Microsoft.CodeAnalysis.CSharp 4.12.0+

## Getting Started

```bash
cd prototypes/fluent-source-gen
dotnet build
dotnet test
```

Reference the built analyzer in your project:

```xml
<ItemGroup>
  <ProjectReference Include="path/to/FluentSourceGen.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

## License

This is a prototype for experimentation purposes.
