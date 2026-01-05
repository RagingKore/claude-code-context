using FluentSourceGen.Tests.TestHelpers;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace FluentSourceGen.Tests;

/// <summary>
/// Tests for AttributeMatch and InterfaceMatch wrapper classes.
/// </summary>
public class MatchWrapperTests
{
    #region AttributeMatch Tests

    [Test]
    public async Task AttributeMatch_TypeArgument_ReturnsCorrectType()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            using System;

            public class GenericAttribute<T> : Attribute { }

            [GenericAttribute<string>]
            public class TestClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");
        await Assert.That(typeSymbol).IsNotNull();

        var attr = typeSymbol!.GetAttributes().First();
        var match = new AttributeMatch(attr);

        await Assert.That(match.TypeArgumentCount).IsEqualTo(1);

        var typeArg = match.TypeArgument(0);
        await Assert.That(typeArg.Name).IsEqualTo("String");
    }

    [Test]
    public async Task AttributeMatch_MultipleTypeArguments_ReturnsAll()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            using System;

            public class MultiAttribute<T1, T2> : Attribute { }

            [MultiAttribute<string, int>]
            public class TestClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");
        await Assert.That(typeSymbol).IsNotNull();

        var attr = typeSymbol!.GetAttributes().First();
        var match = new AttributeMatch(attr);

        await Assert.That(match.TypeArgumentCount).IsEqualTo(2);
        await Assert.That(match.TypeArgument(0).Name).IsEqualTo("String");
        await Assert.That(match.TypeArgument(1).Name).IsEqualTo("Int32");
    }

    [Test]
    public async Task AttributeMatch_TryGetTypeArgument_ReturnsTrueForValidIndex()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            using System;

            public class GenericAttribute<T> : Attribute { }

            [GenericAttribute<string>]
            public class TestClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");
        var attr = typeSymbol!.GetAttributes().First();
        var match = new AttributeMatch(attr);

        var result = match.TryGetTypeArgument(0, out var typeArg);

        await Assert.That(result).IsTrue();
        await Assert.That(typeArg).IsNotNull();
    }

    [Test]
    public async Task AttributeMatch_TryGetTypeArgument_ReturnsFalseForInvalidIndex()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            using System;

            public class GenericAttribute<T> : Attribute { }

            [GenericAttribute<string>]
            public class TestClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");
        var attr = typeSymbol!.GetAttributes().First();
        var match = new AttributeMatch(attr);

        var result = match.TryGetTypeArgument(5, out var typeArg);

        await Assert.That(result).IsFalse();
        await Assert.That(typeArg).IsNull();
    }

    [Test]
    public async Task AttributeMatch_ConstructorArgument_ReturnsValue()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            using System;

            [Obsolete("Test message")]
            public class TestClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");
        await Assert.That(typeSymbol).IsNotNull();

        var attr = typeSymbol!.GetAttributes().First();
        var match = new AttributeMatch(attr);

        await Assert.That(match.ConstructorArguments.Count).IsGreaterThan(0);

        var arg = match.ConstructorArgument(0);
        await Assert.That(arg.Value).IsEqualTo("Test message");
    }

    [Test]
    public async Task AttributeMatch_TryGetConstructorArgument_ExtractsTypedValue()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            using System;

            [Obsolete("Test message")]
            public class TestClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");
        var attr = typeSymbol!.GetAttributes().First();
        var match = new AttributeMatch(attr);

        var result = match.TryGetConstructorArgument<string>(0, out var value);

        await Assert.That(result).IsTrue();
        await Assert.That(value).IsEqualTo("Test message");
    }

    [Test]
    public async Task AttributeMatch_TypeArguments_ReturnsAllAsReadOnlyList()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            using System;

            public class MultiAttribute<T1, T2, T3> : Attribute { }

            [MultiAttribute<string, int, bool>]
            public class TestClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");
        var attr = typeSymbol!.GetAttributes().First();
        var match = new AttributeMatch(attr);

        var typeArgs = match.TypeArguments;

        await Assert.That(typeArgs.Count).IsEqualTo(3);
        await Assert.That(typeArgs[0].Name).IsEqualTo("String");
        await Assert.That(typeArgs[1].Name).IsEqualTo("Int32");
        await Assert.That(typeArgs[2].Name).IsEqualTo("Boolean");
    }

    #endregion

    #region InterfaceMatch Tests

    [Test]
    public async Task InterfaceMatch_TypeArgument_ReturnsCorrectType()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public interface IGeneric<T> { }
            public class TestClass : IGeneric<string> { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");
        await Assert.That(typeSymbol).IsNotNull();

        var iface = typeSymbol!.AllInterfaces.First();
        var match = new InterfaceMatch(iface);

        await Assert.That(match.TypeArgumentCount).IsEqualTo(1);

        var typeArg = match.TypeArgument(0);
        await Assert.That(typeArg.Name).IsEqualTo("String");
    }

    [Test]
    public async Task InterfaceMatch_MultipleTypeArguments_ReturnsAll()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public interface IMulti<T1, T2> { }
            public class TestClass : IMulti<string, int> { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");
        await Assert.That(typeSymbol).IsNotNull();

        var iface = typeSymbol!.AllInterfaces.First();
        var match = new InterfaceMatch(iface);

        await Assert.That(match.TypeArgumentCount).IsEqualTo(2);
        await Assert.That(match.TypeArgument(0).Name).IsEqualTo("String");
        await Assert.That(match.TypeArgument(1).Name).IsEqualTo("Int32");
    }

    [Test]
    public async Task InterfaceMatch_Name_ReturnsInterfaceName()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public interface IMyInterface { }
            public class TestClass : IMyInterface { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");
        var iface = typeSymbol!.AllInterfaces.First();
        var match = new InterfaceMatch(iface);

        await Assert.That(match.Name).IsEqualTo("IMyInterface");
    }

    [Test]
    public async Task InterfaceMatch_FullName_ReturnsFullyQualifiedName()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            namespace MyNamespace
            {
                public interface IMyInterface { }
                public class TestClass : IMyInterface { }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyNamespace.TestClass");
        var iface = typeSymbol!.AllInterfaces.First();
        var match = new InterfaceMatch(iface);

        await Assert.That(match.FullName).Contains("MyNamespace.IMyInterface");
    }

    [Test]
    public async Task InterfaceMatch_TryGetTypeArgument_ReturnsTrueForValidIndex()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public interface IGeneric<T> { }
            public class TestClass : IGeneric<string> { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");
        var iface = typeSymbol!.AllInterfaces.First();
        var match = new InterfaceMatch(iface);

        var result = match.TryGetTypeArgument(0, out var typeArg);

        await Assert.That(result).IsTrue();
        await Assert.That(typeArg).IsNotNull();
    }

    [Test]
    public async Task InterfaceMatch_TryGetTypeArgument_ReturnsFalseForInvalidIndex()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public interface IGeneric<T> { }
            public class TestClass : IGeneric<string> { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");
        var iface = typeSymbol!.AllInterfaces.First();
        var match = new InterfaceMatch(iface);

        var result = match.TryGetTypeArgument(5, out var typeArg);

        await Assert.That(result).IsFalse();
        await Assert.That(typeArg).IsNull();
    }

    [Test]
    public async Task InterfaceMatch_TypeArguments_ReturnsAllAsReadOnlyList()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public interface ITriple<T1, T2, T3> { }
            public class TestClass : ITriple<string, int, bool> { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");
        var iface = typeSymbol!.AllInterfaces.First();
        var match = new InterfaceMatch(iface);

        var typeArgs = match.TypeArguments;

        await Assert.That(typeArgs.Count).IsEqualTo(3);
        await Assert.That(typeArgs[0].Name).IsEqualTo("String");
        await Assert.That(typeArgs[1].Name).IsEqualTo("Int32");
        await Assert.That(typeArgs[2].Name).IsEqualTo("Boolean");
    }

    [Test]
    public async Task InterfaceMatch_Symbol_ReturnsUnderlyingSymbol()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public interface IMyInterface { }
            public class TestClass : IMyInterface { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");
        var iface = typeSymbol!.AllInterfaces.First();
        var match = new InterfaceMatch(iface);

        await Assert.That(match.Symbol).IsNotNull();
        await Assert.That(match.Symbol.Name).IsEqualTo("IMyInterface");
    }

    #endregion

    #region InterfaceMatch.ExtractVariantTypes Tests

    [Test]
    public async Task ExtractVariantTypes_ExtractsTypesFromVariantInterface()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public interface IVariant<T1, T2, T3> { }
            public class ErrorType : IVariant<string, int, bool> { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("ErrorType");
        await Assert.That(typeSymbol).IsNotNull();

        var variants = InterfaceMatch.ExtractVariantTypes(typeSymbol!, "IVariant<");

        await Assert.That(variants.Count).IsEqualTo(3);
        await Assert.That(variants[0].Name).IsEqualTo("String");
        await Assert.That(variants[1].Name).IsEqualTo("Int32");
        await Assert.That(variants[2].Name).IsEqualTo("Boolean");
    }

    [Test]
    public async Task ExtractVariantTypes_ReturnsEmptyList_WhenNoMatchingInterface()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public interface IOther { }
            public class MyType : IOther { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyType");
        await Assert.That(typeSymbol).IsNotNull();

        var variants = InterfaceMatch.ExtractVariantTypes(typeSymbol!, "IVariant<");

        await Assert.That(variants.Count).IsEqualTo(0);
    }

    #endregion
}
