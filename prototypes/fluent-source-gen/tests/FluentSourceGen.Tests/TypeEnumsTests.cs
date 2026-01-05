using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace FluentSourceGen.Tests;

public class TypeEnumsTests
{
    #region TypeAccessibility Tests

    [Test]
    public async Task TypeAccessibility_PublicOrInternal_CombinesFlags()
    {
        var combined = TypeAccessibility.PublicOrInternal;

        await Assert.That((combined & TypeAccessibility.Public) != 0).IsTrue();
        await Assert.That((combined & TypeAccessibility.Internal) != 0).IsTrue();
        await Assert.That((combined & TypeAccessibility.Private) != 0).IsFalse();
    }

    [Test]
    public async Task TypeAccessibility_AnyProtected_IncludesAllProtectedVariants()
    {
        var anyProtected = TypeAccessibility.AnyProtected;

        await Assert.That((anyProtected & TypeAccessibility.Protected) != 0).IsTrue();
        await Assert.That((anyProtected & TypeAccessibility.ProtectedInternal) != 0).IsTrue();
        await Assert.That((anyProtected & TypeAccessibility.PrivateProtected) != 0).IsTrue();
        await Assert.That((anyProtected & TypeAccessibility.Public) != 0).IsFalse();
    }

    [Test]
    public async Task TypeAccessibility_Any_IncludesAllLevels()
    {
        var any = TypeAccessibility.Any;

        await Assert.That((any & TypeAccessibility.Private) != 0).IsTrue();
        await Assert.That((any & TypeAccessibility.Protected) != 0).IsTrue();
        await Assert.That((any & TypeAccessibility.Internal) != 0).IsTrue();
        await Assert.That((any & TypeAccessibility.Public) != 0).IsTrue();
    }

    [Test]
    public async Task TypeAccessibility_CanCombineArbitraryFlags()
    {
        var custom = TypeAccessibility.Public | TypeAccessibility.Private;

        await Assert.That((custom & TypeAccessibility.Public) != 0).IsTrue();
        await Assert.That((custom & TypeAccessibility.Private) != 0).IsTrue();
        await Assert.That((custom & TypeAccessibility.Internal) != 0).IsFalse();
    }

    #endregion

    #region TypeModifiers Tests

    [Test]
    public async Task TypeModifiers_CanCombineMultipleModifiers()
    {
        var modifiers = TypeModifiers.Partial | TypeModifiers.Static | TypeModifiers.Sealed;

        await Assert.That((modifiers & TypeModifiers.Partial) != 0).IsTrue();
        await Assert.That((modifiers & TypeModifiers.Static) != 0).IsTrue();
        await Assert.That((modifiers & TypeModifiers.Sealed) != 0).IsTrue();
        await Assert.That((modifiers & TypeModifiers.Abstract) != 0).IsFalse();
    }

    [Test]
    public async Task TypeModifiers_None_IsZero()
    {
        await Assert.That((int)TypeModifiers.None).IsEqualTo(0);
    }

    #endregion

    #region TypeKind Tests

    [Test]
    public async Task TypeKind_AnyClass_IncludesClassAndRecordClass()
    {
        var anyClass = TypeKind.AnyClass;

        await Assert.That((anyClass & TypeKind.Class) != 0).IsTrue();
        await Assert.That((anyClass & TypeKind.RecordClass) != 0).IsTrue();
        await Assert.That((anyClass & TypeKind.Struct) != 0).IsFalse();
    }

    [Test]
    public async Task TypeKind_AnyStruct_IncludesStructAndRecordStruct()
    {
        var anyStruct = TypeKind.AnyStruct;

        await Assert.That((anyStruct & TypeKind.Struct) != 0).IsTrue();
        await Assert.That((anyStruct & TypeKind.RecordStruct) != 0).IsTrue();
        await Assert.That((anyStruct & TypeKind.Class) != 0).IsFalse();
    }

    [Test]
    public async Task TypeKind_AnyRecord_IncludesRecordClassAndRecordStruct()
    {
        var anyRecord = TypeKind.AnyRecord;

        await Assert.That((anyRecord & TypeKind.RecordClass) != 0).IsTrue();
        await Assert.That((anyRecord & TypeKind.RecordStruct) != 0).IsTrue();
        await Assert.That((anyRecord & TypeKind.Class) != 0).IsFalse();
        await Assert.That((anyRecord & TypeKind.Struct) != 0).IsFalse();
    }

    [Test]
    public async Task TypeKind_AnyReferenceType_IncludesExpectedTypes()
    {
        var anyRef = TypeKind.AnyReferenceType;

        await Assert.That((anyRef & TypeKind.Class) != 0).IsTrue();
        await Assert.That((anyRef & TypeKind.RecordClass) != 0).IsTrue();
        await Assert.That((anyRef & TypeKind.Interface) != 0).IsTrue();
        await Assert.That((anyRef & TypeKind.Delegate) != 0).IsTrue();
        await Assert.That((anyRef & TypeKind.Struct) != 0).IsFalse();
    }

    [Test]
    public async Task TypeKind_AnyValueType_IncludesExpectedTypes()
    {
        var anyValue = TypeKind.AnyValueType;

        await Assert.That((anyValue & TypeKind.Struct) != 0).IsTrue();
        await Assert.That((anyValue & TypeKind.RecordStruct) != 0).IsTrue();
        await Assert.That((anyValue & TypeKind.Enum) != 0).IsTrue();
        await Assert.That((anyValue & TypeKind.Class) != 0).IsFalse();
    }

    #endregion
}
