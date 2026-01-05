using FluentSourceGen.Tests.TestHelpers;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace FluentSourceGen.Tests;

public class SymbolExtensionsTests
{
    #region Namespace Extension Tests

    [Test]
    public async Task GetNamespace_ReturnsNamespace_ForNamespacedType()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            namespace MyNamespace.SubNamespace
            {
                public class TestClass { }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyNamespace.SubNamespace.TestClass");

        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.GetNamespace()).IsEqualTo("MyNamespace.SubNamespace");
    }

    [Test]
    public async Task GetNamespace_ReturnsEmptyString_ForGlobalNamespaceType()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class GlobalClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("GlobalClass");

        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.GetNamespace()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task GetNamespaceDeclaration_ReturnsFileScopedDeclaration()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            namespace MyNamespace
            {
                public class TestClass { }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyNamespace.TestClass");

        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.GetNamespaceDeclaration()).IsEqualTo("namespace MyNamespace;");
    }

    [Test]
    public async Task GetNamespaceDeclaration_ReturnsComment_ForGlobalNamespace()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class GlobalClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("GlobalClass");

        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.GetNamespaceDeclaration()).IsEqualTo("// Global namespace");
    }

    #endregion

    #region Type Name Extension Tests

    [Test]
    public async Task FullName_ReturnsFullyQualifiedName_WithoutGlobalPrefix()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            namespace MyNamespace
            {
                public class TestClass { }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyNamespace.TestClass");

        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.FullName()).IsEqualTo("MyNamespace.TestClass");
    }

    [Test]
    public async Task GlobalName_ReturnsFullyQualifiedName_WithGlobalPrefix()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            namespace MyNamespace
            {
                public class TestClass { }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyNamespace.TestClass");

        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.GlobalName()).IsEqualTo("global::MyNamespace.TestClass");
    }

    [Test]
    public async Task SimpleName_ReturnsTypeNameOnly()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            namespace MyNamespace
            {
                public class TestClass { }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyNamespace.TestClass");

        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.SimpleName()).IsEqualTo("TestClass");
    }

    #endregion

    #region Type Declaration Extension Tests

    [Test]
    public async Task GetTypeKeyword_ReturnsClass_ForClass()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class TestClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");

        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.GetTypeKeyword()).IsEqualTo("class");
    }

    [Test]
    public async Task GetTypeKeyword_ReturnsStruct_ForStruct()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public struct TestStruct { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestStruct");

        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.GetTypeKeyword()).IsEqualTo("struct");
    }

    [Test]
    public async Task GetTypeKeyword_ReturnsRecord_ForRecord()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public record TestRecord;
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestRecord");

        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.GetTypeKeyword()).IsEqualTo("record");
    }

    [Test]
    public async Task GetTypeKeyword_ReturnsRecordStruct_ForRecordStruct()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public record struct TestRecordStruct;
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestRecordStruct");

        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.GetTypeKeyword()).IsEqualTo("record struct");
    }

    [Test]
    public async Task GetTypeKeyword_ReturnsInterface_ForInterface()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public interface ITestInterface { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("ITestInterface");

        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.GetTypeKeyword()).IsEqualTo("interface");
    }

    [Test]
    public async Task GetAccessibility_ReturnsPublic_ForPublicType()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class TestClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");

        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.GetAccessibility()).IsEqualTo("public");
    }

    [Test]
    public async Task GetAccessibility_ReturnsInternal_ForInternalType()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            internal class TestClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");

        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.GetAccessibility()).IsEqualTo("internal");
    }

    [Test]
    public async Task IsPartial_ReturnsTrue_ForPartialType()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public partial class TestClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");

        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.IsPartial()).IsTrue();
    }

    [Test]
    public async Task IsPartial_ReturnsFalse_ForNonPartialType()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class TestClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");

        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.IsPartial()).IsFalse();
    }

    #endregion

    #region Containing Type Extension Tests

    [Test]
    public async Task GetContainingTypes_ReturnsEmptyList_ForTopLevelType()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class OuterClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("OuterClass");

        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.GetContainingTypes().Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetContainingTypes_ReturnsContainingTypes_ForNestedType()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class OuterClass
            {
                public class InnerClass { }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("OuterClass+InnerClass");

        await Assert.That(typeSymbol).IsNotNull();

        var containingTypes = typeSymbol!.GetContainingTypes();
        await Assert.That(containingTypes.Count).IsEqualTo(1);
        await Assert.That(containingTypes[0].Name).IsEqualTo("OuterClass");
    }

    [Test]
    public async Task GetContainingTypes_ReturnsAllContainingTypes_ForDeeplyNestedType()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class Level1
            {
                public class Level2
                {
                    public class Level3 { }
                }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("Level1+Level2+Level3");

        await Assert.That(typeSymbol).IsNotNull();

        var containingTypes = typeSymbol!.GetContainingTypes();
        await Assert.That(containingTypes.Count).IsEqualTo(2);
        await Assert.That(containingTypes[0].Name).IsEqualTo("Level1");
        await Assert.That(containingTypes[1].Name).IsEqualTo("Level2");
    }

    #endregion

    #region Interface Extension Tests

    [Test]
    public async Task FindInterface_ReturnsInterface_WhenImplemented()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public interface IMyInterface { }
            public class TestClass : IMyInterface { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");

        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.FindInterface("IMyInterface")).IsNotNull();
    }

    [Test]
    public async Task FindInterface_ReturnsNull_WhenNotImplemented()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public interface IMyInterface { }
            public class TestClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");

        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.FindInterface("IMyInterface")).IsNull();
    }

    [Test]
    public async Task ImplementsInterface_ReturnsTrue_WhenImplemented()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public interface IMyInterface { }
            public class TestClass : IMyInterface { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");

        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.ImplementsInterface("IMyInterface")).IsTrue();
    }

    #endregion

    #region Attribute Extension Tests

    [Test]
    public async Task HasAttribute_ReturnsTrue_WhenAttributePresent()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            using System;

            [Obsolete]
            public class TestClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");

        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.HasAttribute("System.ObsoleteAttribute")).IsTrue();
    }

    [Test]
    public async Task HasAttribute_ReturnsFalse_WhenAttributeNotPresent()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class TestClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");

        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.HasAttribute("System.ObsoleteAttribute")).IsFalse();
    }

    [Test]
    public async Task FindAttribute_ReturnsAttributeData_WhenPresent()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            using System;

            [Obsolete("Test message")]
            public class TestClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");

        await Assert.That(typeSymbol).IsNotNull();

        var attr = typeSymbol!.FindAttribute("System.ObsoleteAttribute");
        await Assert.That(attr).IsNotNull();
        await Assert.That(attr!.ConstructorArguments.Length).IsGreaterThan(0);
    }

    #endregion
}
