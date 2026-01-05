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

    public void Initialize(GeneratorInitializationContext context)
    {
        context.RegisterForSyntaxNotifications(() => new ValueObjectSyntaxReceiver());
    }

    public void Execute(GeneratorExecutionContext context)
    {
        if (context.SyntaxReceiver is not ValueObjectSyntaxReceiver receiver)
            return;

        foreach (var typeDeclaration in receiver.CandidateTypes)
        {
            var semanticModel = context.Compilation.GetSemanticModel(typeDeclaration.SyntaxTree);
            if (semanticModel.GetDeclaredSymbol(typeDeclaration) is not { } typeSymbol)
                continue;

            var (hasAttribute, valueType) = HasValueObjectAttribute(typeSymbol);
            if (!hasAttribute || valueType is null)
                continue;

            // ... 100+ more lines of generation logic, diagnostics, file naming ...
        }
    }

    static (bool, ITypeSymbol?) HasValueObjectAttribute(INamedTypeSymbol typeSymbol)
    {
        var attr = typeSymbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString()
                .StartsWith("Kurrent.ValueObjectAttribute") == true);

        var valueType = attr?.AttributeClass?.TypeArguments.FirstOrDefault();
        return (attr is not null, valueType);
    }
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

                    [System.Diagnostics.DebuggerDisplay("{GetDebugString()}")]
                    readonly partial record struct {{type.Name}}
                    {
                        public {{valueType.FullName()}} Value { get; private init; }

                        public static {{type.Name}} From({{valueType.FullName()}} value) => new() { Value = value };

                        public static implicit operator {{valueType.FullName()}}({{type.Name}} _) => _.Value;
                        public static implicit operator {{type.Name}}({{valueType.FullName()}} _) => From(_);

                        string GetDebugString() => $"{{type.Name}}: {Value}";
                        public override string ToString() => Value?.ToString() ?? "";
                    }
                    """,
                    valueType);
            });
    }
}
```

### Example 2: ResultBaseImplicitOperatorsGenerator

**Before (100+ lines):**
```csharp
[Generator]
public class ResultBaseImplicitOperatorsGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidateTypes = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is RecordDeclarationSyntax,
                transform: static (ctx, _) =>
                {
                    var recordDecl = (RecordDeclarationSyntax)ctx.Node;
                    var symbol = ctx.SemanticModel.GetDeclaredSymbol(recordDecl);

                    if (symbol is null) return null;

                    foreach (var iface in symbol.AllInterfaces)
                    {
                        if (iface.OriginalDefinition.ToDisplayString() == "Kurrent.IResultBase<TValue, TError>")
                            return symbol;
                    }
                    return null;
                })
            .Where(static m => m is not null);

        context.RegisterSourceOutput(candidateTypes, static (spc, typeSymbol) =>
        {
            // ... 80+ more lines extracting interfaces, building source ...
        });
    }
}
```

**After (25 lines):**
```csharp
[Generator]
public class ResultBaseImplicitOperatorsGenerator : FluentGenerator
{
    protected override void Configure(GeneratorContext ctx)
    {
        ctx.Types
            .ThatAreRecords()
            .Implementing("Kurrent.IResultBase<,>")
            .ForEach((type, iface, emit) =>
            {
                var tValue = iface.TypeArgument(0);
                var tError = iface.TypeArgument(1);

                emit.Source($"{type.Name}.Operators.g.cs", $$"""
                    {{type.GetNamespaceDeclaration()}}

                    partial record {{type.Name}}
                    {
                        {{type.Name}}({{tValue.FullName()}} value) : base(true, value, default) { }
                        {{type.Name}}({{tError.FullName()}} error) : base(false, default, error) { }

                        public static implicit operator {{type.Name}}({{tValue.FullName()}} value) => new(value);
                        public static implicit operator {{type.Name}}({{tError.FullName()}} error) => new(error);
                    }
                    """);
            });
    }
}
```

## API Reference

### Type Queries

Start with `ctx.Types` and chain filters:

```csharp
ctx.Types
    // Type kind filters
    .ThatAreClasses()
    .ThatAreStructs()
    .ThatAreRecords()           // record class or record struct
    .ThatAreRecordClasses()
    .ThatAreRecordStructs()
    .ThatAreInterfaces()
    .ThatAreEnums()

    // Modifier filters
    .ThatArePartial()
    .ThatAreStatic()
    .ThatAreAbstract()
    .ThatAreSealed()

    // Visibility filters
    .ThatArePublic()
    .ThatAreInternal()

    // Attribute filters (use <> for generic attributes)
    .WithAttribute("MyNamespace.MyAttribute")
    .WithAttribute("MyNamespace.MyAttribute<>")      // generic
    .WithAttribute("MyNamespace.MyAttribute<,>")     // 2 type params
    .WithAttribute<MyAttribute>()                     // compile-time

    // Interface filters
    .Implementing("IMyInterface")
    .Implementing("IMyInterface<>")
    .Implementing<IMyInterface>()

    // Namespace filters
    .InNamespace("MyNamespace")
    .InNamespaceStartingWith("MyNamespace.")

    // Custom filters
    .Where((SyntaxNode node) => /* custom syntax check */)
    .Where((INamedTypeSymbol symbol) => /* custom semantic check */)
```

### Terminal Operations

```csharp
// Basic - just the symbol
.ForEach((INamedTypeSymbol type, SourceEmitter emit) => { ... });

// With attribute data (requires .WithAttribute())
.ForEach((INamedTypeSymbol type, AttributeMatch attr, SourceEmitter emit) => { ... });

// With interface data (requires .Implementing())
.ForEach((INamedTypeSymbol type, InterfaceMatch iface, SourceEmitter emit) => { ... });
```

### AttributeMatch

Access attribute information easily:

```csharp
// For generic attributes like MyAttribute<TValue, TOptions>
var valueType = attr.TypeArgument(0);    // TValue
var optionsType = attr.TypeArgument(1);  // TOptions
var allTypeArgs = attr.TypeArguments;     // IReadOnlyList<ITypeSymbol>

// Constructor arguments
var name = attr.ConstructorArgument(0);   // TypedConstant
attr.TryGetConstructorArgument<string>(0, out var nameValue);

// Named arguments
var option = attr.NamedArgument("Option");
attr.TryGetNamedArgument<bool>("Enabled", out var enabled);

// Raw access
AttributeData data = attr.Data;
```

### InterfaceMatch

Access interface type arguments:

```csharp
// For interfaces like IResultBase<TValue, TError>
var tValue = iface.TypeArgument(0);
var tError = iface.TypeArgument(1);
var allTypeArgs = iface.TypeArguments;

// Extract variant types from nested interfaces
var variants = InterfaceMatch.ExtractVariantTypes(tError, "Kurrent.IVariantException<");

// Raw access
INamedTypeSymbol symbol = iface.Symbol;
```

### SourceEmitter

Emit generated source code:

```csharp
// Simple emission
emit.Source("MyType.g.cs", sourceCode);

// Auto-named based on type
emit.Source(sourceCode);                    // MyNamespace_MyType.g.cs
emit.Source(sourceCode, ".Operators");      // MyNamespace_MyType.Operators.g.cs

// With file naming options
emit.Source(
    new FileNamingOptions
    {
        Prefix = "ValueObjects",            // Folder or prefix
        UseFoldersForPrefix = true,         // ValueObjects/MyType_HASH.g.cs
        UseFoldersForNamespace = true,      // ValueObjects/MyNamespace/MyType_HASH.g.cs
        LowercasePath = false
    },
    sourceCode,
    typeArgsForHash);                       // Additional types for hash uniqueness
```

### Symbol Extensions

Convenient extensions on `INamedTypeSymbol`:

```csharp
// Namespace
type.GetNamespace()              // "MyNamespace" or ""
type.GetNamespaceDeclaration()   // "namespace MyNamespace;" or "// Global namespace"
type.GetNamespaceBlockStart()    // "namespace MyNamespace {"
type.GetNamespaceBlockEnd()      // "}" or ""

// Type names
type.FullName()                  // "MyNamespace.MyType"
type.GlobalName()                // "global::MyNamespace.MyType"
type.SimpleName()                // "MyType"

// Type declaration
type.GetTypeKeyword()            // "record struct", "class", "interface", etc.
type.GetAccessibility()          // "public", "internal", etc.
type.GetModifiers()              // "public partial readonly"
type.GetTypeDeclaration()        // "public partial record struct MyType"
type.IsPartial()                 // true/false

// Containing types (for nested types)
type.GetContainingTypes()        // List from outermost to innermost
type.GetContainingTypeDeclarations() // (declarations, closingBraces, indentLevel)

// Finding attributes/interfaces
type.FindAttribute("MyAttribute<>")
type.HasAttribute("MyAttribute")
type.FindInterface("IMyInterface<>")
type.ImplementsInterface("IMyInterface")
```

### Diagnostics

Report diagnostics easily:

```csharp
emit.ReportInfo("GEN001", "Info", "Processing type...");
emit.ReportWarning("GEN002", "Warning", "Consider using partial");
emit.ReportError("GEN003", "Error", "Type must be partial");

// With location from the type
emit.ReportDiagnostic(DiagnosticSeverity.Error, "GEN004", "Error", "Details...");
```

### Post-Initialization Output

Add marker attributes or other static source:

```csharp
protected override void Configure(GeneratorContext ctx)
{
    ctx.AddPostInitializationOutput("GenerateAttribute.g.cs", """
        namespace MyNamespace;

        [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
        public sealed class GenerateAttribute : Attribute { }
        """);

    ctx.Types
        .WithAttribute("MyNamespace.GenerateAttribute")
        .ForEach((type, emit) => { ... });
}
```

## Design Principles

1. **Expose Roslyn types** - `INamedTypeSymbol`, `ITypeSymbol`, `AttributeData` are used directly
2. **Hide ceremony** - Syntax receivers, semantic transforms, incremental pipelines are internal
3. **String interpolation templates** - Use C# raw string literals with `{{expression}}`
4. **Automatic error handling** - Exceptions in ForEach are caught and reported as diagnostics
5. **Modern .NET** - Uses `IIncrementalGenerator` for optimal IDE performance

## Project Structure

```
prototypes/fluent-source-gen/
├── src/
│   └── FluentSourceGen/
│       ├── FluentGenerator.cs      # Base class and GeneratorContext
│       ├── TypeQuery.cs            # Fluent query builder
│       ├── TypeFilter.cs           # Filter flags enum
│       ├── AttributeMatch.cs       # Attribute data wrapper
│       ├── InterfaceMatch.cs       # Interface data wrapper
│       ├── SourceEmitter.cs        # Code emission and file naming
│       └── SymbolExtensions.cs     # INamedTypeSymbol extensions
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
