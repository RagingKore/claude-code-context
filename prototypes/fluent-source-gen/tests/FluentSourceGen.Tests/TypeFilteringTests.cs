using FluentSourceGen.Tests.TestHelpers;
using Microsoft.CodeAnalysis;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace FluentSourceGen.Tests;

/// <summary>
/// Tests for type filtering logic used by TypeQuery.
/// Tests the helper methods and predicates for filtering types.
/// </summary>
public class TypeFilteringTests
{
    #region Type Kind Tests

    [Test]
    public async Task TypeKindFiltering_Class_MatchesClass()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class TestClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestClass");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.TypeKind).IsEqualTo(Microsoft.CodeAnalysis.TypeKind.Class);
        await Assert.That(typeSymbol.IsRecord).IsFalse();
    }

    [Test]
    public async Task TypeKindFiltering_Struct_MatchesStruct()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public struct TestStruct { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestStruct");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.TypeKind).IsEqualTo(Microsoft.CodeAnalysis.TypeKind.Struct);
        await Assert.That(typeSymbol.IsRecord).IsFalse();
    }

    [Test]
    public async Task TypeKindFiltering_RecordClass_MatchesRecordClass()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public record TestRecord;
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestRecord");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.TypeKind).IsEqualTo(Microsoft.CodeAnalysis.TypeKind.Class);
        await Assert.That(typeSymbol.IsRecord).IsTrue();
    }

    [Test]
    public async Task TypeKindFiltering_RecordStruct_MatchesRecordStruct()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public record struct TestRecordStruct;
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestRecordStruct");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.TypeKind).IsEqualTo(Microsoft.CodeAnalysis.TypeKind.Struct);
        await Assert.That(typeSymbol.IsRecord).IsTrue();
    }

    [Test]
    public async Task TypeKindFiltering_Interface_MatchesInterface()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public interface ITestInterface { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("ITestInterface");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.TypeKind).IsEqualTo(Microsoft.CodeAnalysis.TypeKind.Interface);
    }

    [Test]
    public async Task TypeKindFiltering_Enum_MatchesEnum()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public enum TestEnum { A, B, C }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestEnum");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.TypeKind).IsEqualTo(Microsoft.CodeAnalysis.TypeKind.Enum);
    }

    [Test]
    public async Task TypeKindFiltering_Delegate_MatchesDelegate()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public delegate void TestDelegate();
            """);

        var typeSymbol = compilation.GetTypeSymbol("TestDelegate");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.TypeKind).IsEqualTo(Microsoft.CodeAnalysis.TypeKind.Delegate);
    }

    #endregion

    #region Modifier Tests

    [Test]
    public async Task ModifierFiltering_Static_MatchesStaticClass()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public static class StaticClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("StaticClass");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.IsStatic).IsTrue();
    }

    [Test]
    public async Task ModifierFiltering_Abstract_MatchesAbstractClass()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public abstract class AbstractClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("AbstractClass");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.IsAbstract).IsTrue();
    }

    [Test]
    public async Task ModifierFiltering_Sealed_MatchesSealedClass()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public sealed class SealedClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("SealedClass");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.IsSealed).IsTrue();
    }

    [Test]
    public async Task ModifierFiltering_Readonly_MatchesReadonlyStruct()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public readonly struct ReadonlyStruct { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("ReadonlyStruct");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.IsReadOnly).IsTrue();
    }

    [Test]
    public async Task ModifierFiltering_RefStruct_MatchesRefStruct()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public ref struct RefStruct { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("RefStruct");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.IsRefLikeType).IsTrue();
    }

    #endregion

    #region Accessibility Tests

    [Test]
    public async Task AccessibilityFiltering_Public_MatchesPublicType()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class PublicClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("PublicClass");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.DeclaredAccessibility).IsEqualTo(Accessibility.Public);
    }

    [Test]
    public async Task AccessibilityFiltering_Internal_MatchesInternalType()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            internal class InternalClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("InternalClass");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.DeclaredAccessibility).IsEqualTo(Accessibility.Internal);
    }

    [Test]
    public async Task AccessibilityFiltering_NestedPrivate_MatchesPrivateNestedType()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class Outer
            {
                private class PrivateNested { }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("Outer+PrivateNested");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.DeclaredAccessibility).IsEqualTo(Accessibility.Private);
    }

    #endregion

    #region Inheritance Tests

    [Test]
    public async Task InheritanceFiltering_DerivedFrom_MatchesDerivedType()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class BaseClass { }
            public class DerivedClass : BaseClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("DerivedClass");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.BaseType).IsNotNull();
        await Assert.That(typeSymbol.BaseType!.Name).IsEqualTo("BaseClass");
    }

    [Test]
    public async Task InheritanceFiltering_TransitiveInheritance_MatchesGrandChild()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class GrandParent { }
            public class Parent : GrandParent { }
            public class Child : Parent { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("Child");
        await Assert.That(typeSymbol).IsNotNull();

        // Check transitive inheritance
        var current = typeSymbol!.BaseType;
        var inheritanceChain = new List<string>();
        while (current is not null && current.SpecialType != SpecialType.System_Object)
        {
            inheritanceChain.Add(current.Name);
            current = current.BaseType;
        }

        await Assert.That(inheritanceChain).Contains("Parent");
        await Assert.That(inheritanceChain).Contains("GrandParent");
    }

    #endregion

    #region Interface Implementation Tests

    [Test]
    public async Task InterfaceFiltering_Implementing_MatchesImplementingType()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public interface IMyInterface { }
            public class MyClass : IMyInterface { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyClass");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.AllInterfaces.Any(i => i.Name == "IMyInterface")).IsTrue();
    }

    [Test]
    public async Task InterfaceFiltering_GenericInterface_MatchesImplementingType()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public interface IGeneric<T> { }
            public class MyClass : IGeneric<string> { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyClass");
        await Assert.That(typeSymbol).IsNotNull();

        var genericInterface = typeSymbol!.AllInterfaces.FirstOrDefault(i =>
            i.OriginalDefinition.Name == "IGeneric");

        await Assert.That(genericInterface).IsNotNull();
        await Assert.That(genericInterface!.TypeArguments.Length).IsEqualTo(1);
    }

    [Test]
    public async Task InterfaceFiltering_MultipleInterfaces_MatchesAll()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public interface IFirst { }
            public interface ISecond { }
            public class MyClass : IFirst, ISecond { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyClass");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.AllInterfaces.Length).IsEqualTo(2);
    }

    #endregion

    #region Member Tests

    [Test]
    public async Task MemberFiltering_WithMethods_MatchesTypeWithMethods()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class MyClass
            {
                public void MyMethod() { }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyClass");
        await Assert.That(typeSymbol).IsNotNull();

        var methods = typeSymbol!.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && !m.IsImplicitlyDeclared);

        await Assert.That(methods.Any()).IsTrue();
    }

    [Test]
    public async Task MemberFiltering_WithProperties_MatchesTypeWithProperties()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class MyClass
            {
                public string MyProperty { get; set; }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyClass");
        await Assert.That(typeSymbol).IsNotNull();

        var properties = typeSymbol!.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => !p.IsImplicitlyDeclared);

        await Assert.That(properties.Any()).IsTrue();
    }

    [Test]
    public async Task MemberFiltering_WithFields_MatchesTypeWithFields()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class MyClass
            {
                private string _field;
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyClass");
        await Assert.That(typeSymbol).IsNotNull();

        var fields = typeSymbol!.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => !f.IsImplicitlyDeclared);

        await Assert.That(fields.Any()).IsTrue();
    }

    [Test]
    public async Task MemberFiltering_WithConstructor_MatchesTypeWithExplicitConstructor()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class MyClass
            {
                public MyClass(string value) { }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyClass");
        await Assert.That(typeSymbol).IsNotNull();

        var explicitConstructors = typeSymbol!.Constructors
            .Where(c => !c.IsImplicitlyDeclared);

        await Assert.That(explicitConstructors.Any()).IsTrue();
    }

    #endregion

    #region Generic Type Tests

    [Test]
    public async Task GenericFiltering_IsGeneric_MatchesGenericType()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class GenericClass<T> { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("GenericClass`1");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.IsGenericType).IsTrue();
    }

    [Test]
    public async Task GenericFiltering_TypeParameterCount_MatchesExpectedCount()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class TwoParams<T1, T2> { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TwoParams`2");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.TypeParameters.Length).IsEqualTo(2);
    }

    [Test]
    public async Task GenericFiltering_ConstrainedTypeParameter_MatchesConstrained()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class Constrained<T> where T : class, new() { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("Constrained`1");
        await Assert.That(typeSymbol).IsNotNull();

        var typeParam = typeSymbol!.TypeParameters.First();
        await Assert.That(typeParam.HasReferenceTypeConstraint).IsTrue();
        await Assert.That(typeParam.HasConstructorConstraint).IsTrue();
    }

    #endregion

    #region Nesting Tests

    [Test]
    public async Task NestingFiltering_NestedType_HasContainingType()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class Outer
            {
                public class Inner { }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("Outer+Inner");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.ContainingType).IsNotNull();
        await Assert.That(typeSymbol.ContainingType!.Name).IsEqualTo("Outer");
    }

    [Test]
    public async Task NestingFiltering_TopLevelType_HasNoContainingType()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class TopLevel { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("TopLevel");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.ContainingType).IsNull();
    }

    [Test]
    public async Task NestingFiltering_TypeWithNestedTypes_HasNestedTypes()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public class Outer
            {
                public class Inner1 { }
                public class Inner2 { }
            }
            """);

        var typeSymbol = compilation.GetTypeSymbol("Outer");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.GetTypeMembers().Length).IsEqualTo(2);
    }

    #endregion

    #region Attribute Tests

    [Test]
    public async Task AttributeFiltering_WithAttribute_MatchesAttributedType()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            using System;

            [Serializable]
            public class MyClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyClass");
        await Assert.That(typeSymbol).IsNotNull();
        await Assert.That(typeSymbol!.GetAttributes().Any(a =>
            a.AttributeClass?.Name == "SerializableAttribute")).IsTrue();
    }

    [Test]
    public async Task AttributeFiltering_GenericAttribute_ExtractsTypeArguments()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            using System;

            public class MyAttribute<T> : Attribute { }

            [MyAttribute<string>]
            public class MyClass { }
            """);

        var typeSymbol = compilation.GetTypeSymbol("MyClass");
        await Assert.That(typeSymbol).IsNotNull();

        var attr = typeSymbol!.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.Name == "MyAttribute");

        await Assert.That(attr).IsNotNull();
        await Assert.That(attr!.AttributeClass!.TypeArguments.Length).IsEqualTo(1);
        await Assert.That(attr.AttributeClass.TypeArguments[0].Name).IsEqualTo("String");
    }

    #endregion

    #region Negation Method Tests

    [Test]
    public async Task ThatAreNotPartial_FiltersOutPartialTypes()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public partial class PartialClass { }
            public class NonPartialClass { }
            public partial struct PartialStruct { }
            public struct NonPartialStruct { }
            """);

        var types = compilation.GetDeclaredTypes().ToList();

        // Simulate ThatAreNotPartial predicate
        var filtered = types.Where(t =>
        {
            var syntax = t.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            return syntax is not Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax tds ||
                   !tds.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword));
        }).ToList();

        await Assert.That(filtered.Count).IsEqualTo(2);
        await Assert.That(filtered.Select(t => t.Name)).Contains("NonPartialClass");
        await Assert.That(filtered.Select(t => t.Name)).Contains("NonPartialStruct");
    }

    [Test]
    public async Task ThatAreNotStatic_FiltersOutStaticTypes()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public static class StaticClass { }
            public class NonStaticClass { }
            public static class AnotherStaticClass { }
            """);

        var types = compilation.GetDeclaredTypes().ToList();

        // Simulate ThatAreNotStatic predicate
        var filtered = types.Where(t => !t.IsStatic).ToList();

        await Assert.That(filtered.Count).IsEqualTo(1);
        await Assert.That(filtered[0].Name).IsEqualTo("NonStaticClass");
    }

    [Test]
    public async Task ThatAreNotAbstract_FiltersOutAbstractClasses()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public abstract class AbstractClass { }
            public class ConcreteClass { }
            public abstract class AnotherAbstractClass { }
            public sealed class SealedClass { }
            """);

        var types = compilation.GetDeclaredTypes().ToList();

        // Simulate ThatAreNotAbstract predicate (allows interfaces since they're technically abstract)
        var filtered = types.Where(t => !t.IsAbstract || t.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface).ToList();

        await Assert.That(filtered.Count).IsEqualTo(2);
        await Assert.That(filtered.Select(t => t.Name)).Contains("ConcreteClass");
        await Assert.That(filtered.Select(t => t.Name)).Contains("SealedClass");
    }

    [Test]
    public async Task ThatAreNotAbstract_AllowsInterfaces()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public abstract class AbstractClass { }
            public interface IMyInterface { }
            public class ConcreteClass { }
            """);

        var types = compilation.GetDeclaredTypes().ToList();

        // Simulate ThatAreNotAbstract predicate
        var filtered = types.Where(t => !t.IsAbstract || t.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface).ToList();

        await Assert.That(filtered.Count).IsEqualTo(2);
        await Assert.That(filtered.Select(t => t.Name)).Contains("IMyInterface");
        await Assert.That(filtered.Select(t => t.Name)).Contains("ConcreteClass");
    }

    [Test]
    public async Task ThatAreNotSealed_FiltersOutSealedTypes()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public sealed class SealedClass { }
            public class UnsealedClass { }
            public sealed class AnotherSealedClass { }
            public abstract class AbstractClass { }
            """);

        var types = compilation.GetDeclaredTypes().ToList();

        // Simulate ThatAreNotSealed predicate
        var filtered = types.Where(t => !t.IsSealed).ToList();

        await Assert.That(filtered.Count).IsEqualTo(2);
        await Assert.That(filtered.Select(t => t.Name)).Contains("UnsealedClass");
        await Assert.That(filtered.Select(t => t.Name)).Contains("AbstractClass");
    }

    [Test]
    public async Task WithoutModifiers_Static_FiltersOutStaticTypes()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public static class StaticClass { }
            public class RegularClass { }
            public abstract class AbstractClass { }
            """);

        var types = compilation.GetDeclaredTypes().ToList();

        // Simulate WithoutModifiers(TypeModifiers.Static) predicate
        var filtered = types.Where(t => !t.IsStatic).ToList();

        await Assert.That(filtered.Count).IsEqualTo(2);
        await Assert.That(filtered.Select(t => t.Name)).Contains("RegularClass");
        await Assert.That(filtered.Select(t => t.Name)).Contains("AbstractClass");
    }

    [Test]
    public async Task WithoutModifiers_Abstract_FiltersOutAbstractTypes()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public abstract class AbstractClass { }
            public class ConcreteClass { }
            public sealed class SealedClass { }
            """);

        var types = compilation.GetDeclaredTypes().ToList();

        // Simulate WithoutModifiers(TypeModifiers.Abstract) predicate
        var filtered = types.Where(t => !t.IsAbstract).ToList();

        await Assert.That(filtered.Count).IsEqualTo(2);
        await Assert.That(filtered.Select(t => t.Name)).Contains("ConcreteClass");
        await Assert.That(filtered.Select(t => t.Name)).Contains("SealedClass");
    }

    [Test]
    public async Task WithoutModifiers_Sealed_FiltersOutSealedTypes()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public sealed class SealedClass { }
            public class RegularClass { }
            public abstract class AbstractClass { }
            """);

        var types = compilation.GetDeclaredTypes().ToList();

        // Simulate WithoutModifiers(TypeModifiers.Sealed) predicate
        var filtered = types.Where(t => !t.IsSealed).ToList();

        await Assert.That(filtered.Count).IsEqualTo(2);
        await Assert.That(filtered.Select(t => t.Name)).Contains("RegularClass");
        await Assert.That(filtered.Select(t => t.Name)).Contains("AbstractClass");
    }

    [Test]
    public async Task WithoutModifiers_Combined_FiltersMultipleModifiers()
    {
        var compilation = CompilationHelper.CreateCompilation("""
            public static class StaticClass { }
            public abstract class AbstractClass { }
            public sealed class SealedClass { }
            public class RegularClass { }
            """);

        var types = compilation.GetDeclaredTypes().ToList();

        // Simulate WithoutModifiers(TypeModifiers.Static | TypeModifiers.Abstract) predicate
        var filtered = types.Where(t => !t.IsStatic && !t.IsAbstract).ToList();

        await Assert.That(filtered.Count).IsEqualTo(2);
        await Assert.That(filtered.Select(t => t.Name)).Contains("SealedClass");
        await Assert.That(filtered.Select(t => t.Name)).Contains("RegularClass");
    }

    #endregion
}
