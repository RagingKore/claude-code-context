namespace FluentSourceGen;

/// <summary>
/// Flags for filtering types by kind.
/// </summary>
[Flags]
public enum TypeFilter
{
    None = 0,

    // Base type kinds
    Class = 1 << 0,
    Struct = 1 << 1,
    Record = 1 << 2,
    RecordStruct = 1 << 3,
    Interface = 1 << 4,
    Enum = 1 << 5,

    // Modifiers
    Partial = 1 << 10,
    Static = 1 << 11,
    Abstract = 1 << 12,
    Sealed = 1 << 13,
    Readonly = 1 << 14,

    // Visibility
    Public = 1 << 20,
    Internal = 1 << 21,
    Private = 1 << 22,
    Protected = 1 << 23,

    // Common combinations
    AnyClass = Class | Record,
    AnyStruct = Struct | RecordStruct,
    AnyRecord = Record | RecordStruct,
    AnyType = Class | Struct | Record | RecordStruct | Interface | Enum,
}
