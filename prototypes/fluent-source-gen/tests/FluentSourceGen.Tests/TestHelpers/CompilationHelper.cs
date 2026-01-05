using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FluentSourceGen.Tests.TestHelpers;

/// <summary>
/// Helper class for creating compilations for testing source generators.
/// </summary>
public static class CompilationHelper
{
    /// <summary>
    /// Creates a CSharp compilation from source code.
    /// </summary>
    public static CSharpCompilation CreateCompilation(string source, string assemblyName = "TestAssembly")
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        // Add netstandard reference
        var netStandardPath = Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!,
            "netstandard.dll");
        if (File.Exists(netStandardPath))
            references.Add(MetadataReference.CreateFromFile(netStandardPath));

        return CSharpCompilation.Create(
            assemblyName,
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>
    /// Creates a CSharp compilation from multiple source files.
    /// </summary>
    public static CSharpCompilation CreateCompilation(IEnumerable<string> sources, string assemblyName = "TestAssembly")
    {
        var syntaxTrees = sources.Select(s => CSharpSyntaxTree.ParseText(s)).ToList();

        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        return CSharpCompilation.Create(
            assemblyName,
            syntaxTrees: syntaxTrees,
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>
    /// Gets a named type symbol from a compilation.
    /// </summary>
    public static INamedTypeSymbol? GetTypeSymbol(this CSharpCompilation compilation, string fullyQualifiedName)
    {
        return compilation.GetTypeByMetadataName(fullyQualifiedName);
    }

    /// <summary>
    /// Gets all type symbols declared in the compilation's source.
    /// </summary>
    public static IEnumerable<INamedTypeSymbol> GetDeclaredTypes(this CSharpCompilation compilation)
    {
        var types = new List<INamedTypeSymbol>();

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot();

            var typeDeclarations = root.DescendantNodes()
                .Where(n => n is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax);

            foreach (var typeDecl in typeDeclarations)
            {
                if (semanticModel.GetDeclaredSymbol(typeDecl) is INamedTypeSymbol typeSymbol)
                    types.Add(typeSymbol);
            }
        }

        return types;
    }
}
