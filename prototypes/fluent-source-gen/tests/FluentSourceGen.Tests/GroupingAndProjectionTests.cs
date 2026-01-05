using FluentSourceGen.Tests.TestHelpers;
using Microsoft.CodeAnalysis;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace FluentSourceGen.Tests;

/// <summary>
/// Tests for grouping and projection logic.
/// Tests the underlying grouping/projection behaviors using direct symbol manipulation.
/// </summary>
public class GroupingAndProjectionTests
{
    #region Grouping Logic Tests

    [Test]
    public async Task GroupBy_Namespace_GroupsCorrectly()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            namespace GroupA
            {
                public class Type1 { }
                public class Type2 { }
            }
            namespace GroupB
            {
                public class Type3 { }
            }
            namespace GroupA
            {
                public class Type4 { }
            }
            """);

        var types = compilation.GetDeclaredTypes().ToList();

        var groups = types
            .GroupBy(t => t.ContainingNamespace.ToDisplayString())
            .ToDictionary(g => g.Key, g => g.ToList());

        await Assert.That(groups.Count).IsEqualTo(2);
        await Assert.That(groups["GroupA"].Count).IsEqualTo(3);
        await Assert.That(groups["GroupB"].Count).IsEqualTo(1);
    }

    [Test]
    public async Task GroupBy_TypeKind_GroupsCorrectly()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class MyClass { }
            public struct MyStruct { }
            public interface IMyInterface { }
            public class AnotherClass { }
            """);

        var types = compilation.GetDeclaredTypes().ToList();

        var groups = types
            .GroupBy(t => t.TypeKind)
            .ToDictionary(g => g.Key, g => g.ToList());

        await Assert.That(groups[TypeKind.Class].Count).IsEqualTo(2);
        await Assert.That(groups[TypeKind.Struct].Count).IsEqualTo(1);
        await Assert.That(groups[TypeKind.Interface].Count).IsEqualTo(1);
    }

    [Test]
    public async Task GroupBy_BaseType_GroupsCorrectly()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class BaseA { }
            public class BaseB { }
            public class DerivedA1 : BaseA { }
            public class DerivedA2 : BaseA { }
            public class DerivedB1 : BaseB { }
            """);

        var types = compilation.GetDeclaredTypes()
            .Where(t => t.BaseType?.SpecialType != SpecialType.System_Object)
            .ToList();

        var groups = types
            .GroupBy(t => t.BaseType!.Name)
            .ToDictionary(g => g.Key, g => g.ToList());

        await Assert.That(groups["BaseA"].Count).IsEqualTo(2);
        await Assert.That(groups["BaseB"].Count).IsEqualTo(1);
    }

    [Test]
    public async Task GroupBy_CustomKey_WorksWithAnonymousType()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            namespace NS1 { public class PublicType { } }
            namespace NS2 { internal class InternalType { } }
            namespace NS1 { public class AnotherPublic { } }
            """);

        var types = compilation.GetDeclaredTypes().ToList();

        var groups = types
            .GroupBy(t => new
            {
                Namespace = t.ContainingNamespace.ToDisplayString(),
                IsPublic = t.DeclaredAccessibility == Accessibility.Public
            })
            .ToList();

        await Assert.That(groups.Count).IsEqualTo(3);
    }

    #endregion

    #region Projection Logic Tests

    [Test]
    public async Task Select_ExtractsName()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class Alpha { }
            public class Beta { }
            public class Gamma { }
            """);

        var types = compilation.GetDeclaredTypes().ToList();
        var names = types.Select(t => t.Name).ToList();

        await Assert.That(names.Count).IsEqualTo(3);
        await Assert.That(names).Contains("Alpha");
        await Assert.That(names).Contains("Beta");
        await Assert.That(names).Contains("Gamma");
    }

    [Test]
    public async Task Select_ExtractsComplexData()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            namespace MyNS
            {
                public class TestClass
                {
                    public int Prop1 { get; set; }
                    public string Prop2 { get; set; }
                }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyNS.TestClass");
        await Assert.That(typeSymbol).IsNotNull();

        var projected = new
        {
            Name = typeSymbol!.Name,
            Namespace = typeSymbol.ContainingNamespace.ToDisplayString(),
            PropertyCount = typeSymbol.GetMembers().OfType<IPropertySymbol>().Count()
        };

        await Assert.That(projected.Name).IsEqualTo("TestClass");
        await Assert.That(projected.Namespace).IsEqualTo("MyNS");
        await Assert.That(projected.PropertyCount).IsEqualTo(2);
    }

    [Test]
    public async Task SelectMany_FlattensProperties()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class Type1
            {
                public int A { get; set; }
                public int B { get; set; }
            }
            public class Type2
            {
                public string C { get; set; }
            }
            """);

        var types = compilation.GetDeclaredTypes().ToList();

        var allProperties = types
            .SelectMany(t => t.GetMembers().OfType<IPropertySymbol>())
            .ToList();

        await Assert.That(allProperties.Count).IsEqualTo(3);
        await Assert.That(allProperties.Select(p => p.Name)).Contains("A");
        await Assert.That(allProperties.Select(p => p.Name)).Contains("B");
        await Assert.That(allProperties.Select(p => p.Name)).Contains("C");
    }

    [Test]
    public async Task SelectMany_FlattensInterfaces()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public interface IA { }
            public interface IB { }
            public interface IC { }
            public class Type1 : IA, IB { }
            public class Type2 : IC { }
            public class Type3 { }
            """);

        var types = compilation.GetDeclaredTypes()
            .Where(t => t.TypeKind == TypeKind.Class)
            .ToList();

        var allInterfaces = types
            .SelectMany(t => t.AllInterfaces)
            .ToList();

        await Assert.That(allInterfaces.Count).IsEqualTo(3);
    }

    [Test]
    public async Task SelectMany_WithDistinct_RemovesDuplicates()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public interface IShared { }
            public interface IUnique1 { }
            public interface IUnique2 { }
            public class Type1 : IShared, IUnique1 { }
            public class Type2 : IShared, IUnique2 { }
            """);

        var types = compilation.GetDeclaredTypes()
            .Where(t => t.TypeKind == TypeKind.Class)
            .ToList();

        var allInterfaces = types
            .SelectMany(t => t.AllInterfaces)
            .Distinct(SymbolEqualityComparer.Default)
            .ToList();

        await Assert.That(allInterfaces.Count).IsEqualTo(3);
    }

    #endregion

    #region Combined Grouping and Projection Tests

    [Test]
    public async Task GroupThenProject_WorksTogether()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            namespace NS1
            {
                public class Type1 { }
                public class Type2 { }
            }
            namespace NS2
            {
                public class Type3 { }
            }
            """);

        var types = compilation.GetDeclaredTypes().ToList();

        var result = types
            .GroupBy(t => t.ContainingNamespace.ToDisplayString())
            .Select(g => new { Namespace = g.Key, TypeNames = g.Select(t => t.Name).ToList() })
            .ToList();

        await Assert.That(result.Count).IsEqualTo(2);

        var ns1 = result.First(r => r.Namespace == "NS1");
        await Assert.That(ns1.TypeNames.Count).IsEqualTo(2);
        await Assert.That(ns1.TypeNames).Contains("Type1");
        await Assert.That(ns1.TypeNames).Contains("Type2");
    }

    [Test]
    public async Task ProjectThenGroup_WorksTogether()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class SmallType { public int A { get; set; } }
            public class MediumType { public int A { get; set; } public int B { get; set; } }
            public class LargeType { public int A { get; set; } public int B { get; set; } public int C { get; set; } }
            public class AnotherSmall { public int X { get; set; } }
            """);

        var types = compilation.GetDeclaredTypes().ToList();

        var projected = types
            .Select(t => new
            {
                Type = t,
                PropertyCount = t.GetMembers().OfType<IPropertySymbol>().Count()
            });

        var grouped = projected
            .GroupBy(p => p.PropertyCount)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Type.Name).ToList());

        await Assert.That(grouped[1].Count).IsEqualTo(2);
        await Assert.That(grouped[2].Count).IsEqualTo(1);
        await Assert.That(grouped[3].Count).IsEqualTo(1);
    }

    #endregion

    #region Interface Extraction Tests

    [Test]
    public async Task ExtractInterfaceTypeArguments_ForGrouping()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public interface IHandler<TRequest, TResponse> { }
            public class StringToIntHandler : IHandler<string, int> { }
            public class IntToStringHandler : IHandler<int, string> { }
            public class StringToStringHandler : IHandler<string, string> { }
            """);

        var types = compilation.GetDeclaredTypes()
            .Where(t => t.TypeKind == TypeKind.Class)
            .ToList();

        // Group by first type argument of the interface
        var grouped = types
            .Select(t => new
            {
                Type = t,
                Interface = t.AllInterfaces.FirstOrDefault(i => i.Name == "IHandler")
            })
            .Where(x => x.Interface is not null)
            .GroupBy(x => x.Interface!.TypeArguments[0].Name)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Type.Name).ToList());

        await Assert.That(grouped["String"].Count).IsEqualTo(2);
        await Assert.That(grouped["Int32"].Count).IsEqualTo(1);
    }

    #endregion

    #region Attribute-Based Grouping Tests

    [Test]
    public async Task GroupBy_AttributeValue()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public class CategoryAttribute : Attribute
            {
                public string Name { get; }
                public CategoryAttribute(string name) => Name = name;
            }

            [Category("Commands")]
            public class CreateCommand { }

            [Category("Commands")]
            public class UpdateCommand { }

            [Category("Queries")]
            public class GetQuery { }
            """);

        var types = compilation.GetDeclaredTypes()
            .Where(t => t.GetAttributes().Any(a => a.AttributeClass?.Name == "CategoryAttribute"))
            .ToList();

        var grouped = types
            .GroupBy(t =>
            {
                var attr = t.GetAttributes().First(a => a.AttributeClass?.Name == "CategoryAttribute");
                return attr.ConstructorArguments.FirstOrDefault().Value?.ToString() ?? "";
            })
            .ToDictionary(g => g.Key, g => g.Select(t => t.Name).ToList());

        await Assert.That(grouped["Commands"].Count).IsEqualTo(2);
        await Assert.That(grouped["Queries"].Count).IsEqualTo(1);
    }

    #endregion
}
