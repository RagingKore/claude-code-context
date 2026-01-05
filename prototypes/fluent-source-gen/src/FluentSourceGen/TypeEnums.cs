namespace FluentSourceGen;

/// <summary>
/// Flags for filtering types by accessibility level.
/// </summary>
[Flags]
public enum TypeAccessibility
{
    /// <summary>No accessibility filter.</summary>
    None = 0,

    /// <summary>Private types.</summary>
    Private = 1 << 0,

    /// <summary>Protected types.</summary>
    Protected = 1 << 1,

    /// <summary>Internal types.</summary>
    Internal = 1 << 2,

    /// <summary>Protected internal types (protected OR internal).</summary>
    ProtectedInternal = 1 << 3,

    /// <summary>Private protected types (protected AND internal).</summary>
    PrivateProtected = 1 << 4,

    /// <summary>Public types.</summary>
    Public = 1 << 5,

    // Common combinations

    /// <summary>Public or internal types.</summary>
    PublicOrInternal = Public | Internal,

    /// <summary>Any protected variant (protected, protected internal, private protected).</summary>
    AnyProtected = Protected | ProtectedInternal | PrivateProtected,

    /// <summary>Publicly accessible types (public or internal via InternalsVisibleTo).</summary>
    PubliclyAccessible = Public | Internal | ProtectedInternal,

    /// <summary>Any accessibility level.</summary>
    Any = Private | Protected | Internal | ProtectedInternal | PrivateProtected | Public,
}

/// <summary>
/// Flags for filtering types by modifiers.
/// </summary>
[Flags]
public enum TypeModifiers
{
    /// <summary>No modifier filter.</summary>
    None = 0,

    /// <summary>Partial types.</summary>
    Partial = 1 << 0,

    /// <summary>Static types.</summary>
    Static = 1 << 1,

    /// <summary>Abstract types.</summary>
    Abstract = 1 << 2,

    /// <summary>Sealed types.</summary>
    Sealed = 1 << 3,

    /// <summary>Readonly struct types.</summary>
    Readonly = 1 << 4,

    /// <summary>Ref struct types.</summary>
    Ref = 1 << 5,

    /// <summary>Unsafe types.</summary>
    Unsafe = 1 << 6,

    /// <summary>File-scoped types (C# 11+).</summary>
    File = 1 << 7,
}

/// <summary>
/// Flags for filtering types by kind.
/// </summary>
[Flags]
public enum TypeKind
{
    /// <summary>No type kind filter.</summary>
    None = 0,

    /// <summary>Class types (non-record).</summary>
    Class = 1 << 0,

    /// <summary>Struct types (non-record).</summary>
    Struct = 1 << 1,

    /// <summary>Record class types.</summary>
    RecordClass = 1 << 2,

    /// <summary>Record struct types.</summary>
    RecordStruct = 1 << 3,

    /// <summary>Interface types.</summary>
    Interface = 1 << 4,

    /// <summary>Enum types.</summary>
    Enum = 1 << 5,

    /// <summary>Delegate types.</summary>
    Delegate = 1 << 6,

    // Common combinations

    /// <summary>Any class type (class or record class).</summary>
    AnyClass = Class | RecordClass,

    /// <summary>Any struct type (struct or record struct).</summary>
    AnyStruct = Struct | RecordStruct,

    /// <summary>Any record type (record class or record struct).</summary>
    AnyRecord = RecordClass | RecordStruct,

    /// <summary>Any reference type (class, record class, interface, delegate).</summary>
    AnyReferenceType = Class | RecordClass | Interface | Delegate,

    /// <summary>Any value type (struct, record struct, enum).</summary>
    AnyValueType = Struct | RecordStruct | Enum,

    /// <summary>Any type kind.</summary>
    Any = Class | Struct | RecordClass | RecordStruct | Interface | Enum | Delegate,
}
