using FluentSourceGen.Tests.TestHelpers;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace FluentSourceGen.Tests;

/// <summary>
/// Tests for file naming and SourceEmitter functionality.
/// </summary>
public class FileNamingTests
{
    #region GenerateHintName Tests

    [Test]
    public async Task GenerateHintName_BasicType_GeneratesValidName()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            namespace MyNamespace
            {
                public class TestClass { }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyNamespace.TestClass");
        await Assert.That(typeSymbol).IsNotNull();

        var hintName = SourceEmitter.GenerateHintName(typeSymbol!);

        await Assert.That(hintName).EndsWith(".g.cs");
        await Assert.That(hintName).Contains("TestClass");
    }

    [Test]
    public async Task GenerateHintName_WithSuffix_IncludesSuffix()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            namespace MyNamespace
            {
                public class TestClass { }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyNamespace.TestClass");
        await Assert.That(typeSymbol).IsNotNull();

        var hintName = SourceEmitter.GenerateHintName(typeSymbol!, ".Operators");

        await Assert.That(hintName).Contains(".Operators");
        await Assert.That(hintName).EndsWith(".g.cs");
    }

    [Test]
    public async Task GenerateHintName_GenericType_SanitizesAngleBrackets()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            namespace MyNamespace
            {
                public class GenericClass<T> { }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyNamespace.GenericClass`1");
        await Assert.That(typeSymbol).IsNotNull();

        var hintName = SourceEmitter.GenerateHintName(typeSymbol!);

        await Assert.That(hintName).DoesNotContain("<");
        await Assert.That(hintName).DoesNotContain(">");
        await Assert.That(hintName).EndsWith(".g.cs");
    }

    #endregion

    #region GenerateHintName with Options Tests

    [Test]
    public async Task GenerateHintName_WithPrefix_IncludesPrefix()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            namespace MyNamespace
            {
                public class TestClass { }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyNamespace.TestClass");
        await Assert.That(typeSymbol).IsNotNull();

        var options = new FileNamingOptions
        {
            Prefix = "ValueObjects",
            UseFoldersForPrefix = true
        };

        var hintName = SourceEmitter.GenerateHintName(typeSymbol!, options);

        await Assert.That(hintName).StartsWith("ValueObjects/");
    }

    [Test]
    public async Task GenerateHintName_WithNamespaceFolders_IncludesNamespace()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            namespace MyNamespace
            {
                public class TestClass { }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyNamespace.TestClass");
        await Assert.That(typeSymbol).IsNotNull();

        var options = new FileNamingOptions
        {
            UseFoldersForNamespace = true
        };

        var hintName = SourceEmitter.GenerateHintName(typeSymbol!, options);

        await Assert.That(hintName).Contains("MyNamespace");
    }

    [Test]
    public async Task GenerateHintName_FlatOptions_NoFolders()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            namespace MyNamespace
            {
                public class TestClass { }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyNamespace.TestClass");
        await Assert.That(typeSymbol).IsNotNull();

        var options = FileNamingOptions.Flat;

        var hintName = SourceEmitter.GenerateHintName(typeSymbol!, options);

        await Assert.That(hintName).DoesNotContain("/");
    }

    [Test]
    public async Task GenerateHintName_WithTypeArgs_GeneratesDifferentHashes()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            namespace MyNamespace
            {
                public class TestClass { }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyNamespace.TestClass");
        await Assert.That(typeSymbol).IsNotNull();

        var stringSymbol = compilation.GetTypeByMetadataName("System.String");
        var intSymbol = compilation.GetTypeByMetadataName("System.Int32");

        await Assert.That(stringSymbol).IsNotNull();
        await Assert.That(intSymbol).IsNotNull();

        var options = new FileNamingOptions();

        var hintName1 = SourceEmitter.GenerateHintName(typeSymbol!, options, [stringSymbol!]);
        var hintName2 = SourceEmitter.GenerateHintName(typeSymbol!, options, [intSymbol!]);

        // Different type args should produce different hashes
        await Assert.That(hintName1).IsNotEqualTo(hintName2);
    }

    [Test]
    public async Task GenerateHintName_LowercaseOption_GeneratesLowercasePath()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            namespace MyNamespace
            {
                public class TestClass { }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyNamespace.TestClass");
        await Assert.That(typeSymbol).IsNotNull();

        var options = new FileNamingOptions
        {
            LowercasePath = true,
            UseFoldersForNamespace = true
        };

        var hintName = SourceEmitter.GenerateHintName(typeSymbol!, options);

        // The namespace/prefix parts should be lowercase
        await Assert.That(hintName).Contains("mynamespace");
    }

    #endregion

    #region FileNamingOptions Tests

    [Test]
    public async Task FileNamingOptions_Default_HasExpectedValues()
    {
        var options = FileNamingOptions.Default;

        await Assert.That(options.UseFoldersForNamespace).IsTrue();
        await Assert.That(options.UseFoldersForPrefix).IsTrue();
        await Assert.That(options.LowercasePath).IsFalse();
        await Assert.That(options.Prefix).IsNull();
    }

    [Test]
    public async Task FileNamingOptions_Flat_HasExpectedValues()
    {
        var options = FileNamingOptions.Flat;

        await Assert.That(options.UseFoldersForNamespace).IsFalse();
        await Assert.That(options.UseFoldersForPrefix).IsFalse();
    }

    [Test]
    public async Task FileNamingOptions_CanSetPrefix()
    {
        var options = new FileNamingOptions
        {
            Prefix = "Generated"
        };

        await Assert.That(options.Prefix).IsEqualTo("Generated");
    }

    #endregion
}
