using FluentSourceGen.Tests.TestHelpers;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace FluentSourceGen.Tests;

/// <summary>
/// Tests for assembly filtering logic used by TypeQuery.
/// Tests the helper methods for filtering types by assembly.
/// </summary>
public class AssemblyFilteringTests
{
    #region Assembly Name Tests

    [Test]
    public async Task Type_HasContainingAssembly()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            namespace MyNamespace
            {
                public class TestClass { }
            }
            """, "MyTestAssembly");

        var typeSymbol = compilation.GetTypeSymbol("MyNamespace.TestClass");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.ContainingAssembly).IsNotNull();
        await Assert.That(typeSymbol.ContainingAssembly.Name).IsEqualTo("MyTestAssembly");
    }

    [Test]
    public async Task Type_InDifferentAssemblies_HaveDifferentAssemblyNames()
    {
        var compilation1 = CompilationHelper.CreateCompilation("""
            public class TypeInAssembly1 { }
            """, "Assembly1");

        var compilation2 = CompilationHelper.CreateCompilation("""
            public class TypeInAssembly2 { }
            """, "Assembly2");

        var type1 = compilation1.GetTypeSymbol("TypeInAssembly1");
        var type2 = compilation2.GetTypeSymbol("TypeInAssembly2");

        await Assert.That(type1).IsNotNull();
        await Assert.That(type2).IsNotNull();
        await Assert.That(type1!.ContainingAssembly.Name).IsNotEqualTo(type2!.ContainingAssembly.Name);
    }

    #endregion

    #region System Assembly Detection Tests

    [Test]
    public async Task SystemType_HasSystemAssembly()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class TestClass { }
            """);

        // Get System.String type from the compilation
        var stringType = compilation.GetTypeByMetadataName("System.String");
        await Assert.That(stringType).IsNotNull();
        await Assert.That(stringType!.ContainingAssembly.Name)
            .Satisfies(name => name.StartsWith("System") || name == "mscorlib" || name == "netstandard");
    }

    [Test]
    public async Task UserType_NotInSystemAssembly()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class UserClass { }
            """, "UserAssembly");

        var typeSymbol = compilation.GetTypeSymbol("UserClass");
        await Assert.That(typeSymbol).IsNotNull();

        var assemblyName = typeSymbol!.ContainingAssembly.Name;
        await Assert.That(assemblyName.StartsWith("System")).IsFalse();
        await Assert.That(assemblyName.StartsWith("Microsoft")).IsFalse();
        await Assert.That(assemblyName).IsNotEqualTo("mscorlib");
        await Assert.That(assemblyName).IsNotEqualTo("netstandard");
    }

    #endregion

    #region Assembly Pattern Matching Tests

    [Test]
    public async Task AssemblyName_MatchesExactly()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class TestClass { }
            """, "MyCompany.MyProduct.Core");

        var typeSymbol = compilation.GetTypeSymbol("TestClass");
        await Assert.That(typeSymbol).IsNotNull();

        var assemblyName = typeSymbol!.ContainingAssembly.Name;
        await Assert.That(assemblyName.Equals("MyCompany.MyProduct.Core", StringComparison.OrdinalIgnoreCase)).IsTrue();
    }

    [Test]
    public async Task AssemblyName_CanMatchWildcard()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class TestClass { }
            """, "MyCompany.MyProduct.Core");

        var typeSymbol = compilation.GetTypeSymbol("TestClass");
        await Assert.That(typeSymbol).IsNotNull();

        var assemblyName = typeSymbol!.ContainingAssembly.Name;

        // Simulating wildcard pattern "MyCompany.*"
        await Assert.That(assemblyName.StartsWith("MyCompany.")).IsTrue();
    }

    #endregion

    #region Module and Assembly Comparison Tests

    [Test]
    public async Task Type_ContainingModuleMatchesAssembly()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class TestClass { }
            """, "TestAssembly");

        var typeSymbol = compilation.GetTypeSymbol("TestClass");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.ContainingModule).IsNotNull();
        await Assert.That(typeSymbol.ContainingModule.ContainingAssembly).IsNotNull();
        await Assert.That(typeSymbol.ContainingModule.ContainingAssembly.Name)
            .IsEqualTo(typeSymbol.ContainingAssembly.Name);
    }

    #endregion

    #region Multiple Types Same Assembly Tests

    [Test]
    public async Task MultipleTypes_InSameAssembly_ShareAssemblyName()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            namespace NS1 { public class Type1 { } }
            namespace NS2 { public class Type2 { } }
            namespace NS3 { public class Type3 { } }
            """, "SharedAssembly");

        var type1 = compilation.GetTypeSymbol("NS1.Type1");
        var type2 = compilation.GetTypeSymbol("NS2.Type2");
        var type3 = compilation.GetTypeSymbol("NS3.Type3");

        await Assert.That(type1).IsNotNull();
        await Assert.That(type2).IsNotNull();
        await Assert.That(type3).IsNotNull();

        await Assert.That(type1!.ContainingAssembly.Name).IsEqualTo("SharedAssembly");
        await Assert.That(type2!.ContainingAssembly.Name).IsEqualTo("SharedAssembly");
        await Assert.That(type3!.ContainingAssembly.Name).IsEqualTo("SharedAssembly");
    }

    #endregion
}
