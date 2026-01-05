using FluentSourceGen;
using Microsoft.CodeAnalysis;

namespace Examples;

/// <summary>
/// Generates implicit operators for Result types implementing IResultBase&lt;TValue, TError&gt;.
///
/// Example usage:
/// <code>
/// // Base interface
/// public interface IResultBase&lt;TValue, TError&gt; { }
///
/// // Result type
/// public partial record CreateOrderResult : IResultBase&lt;Order, CreateOrderError&gt;;
///
/// // Generates:
/// // partial record CreateOrderResult
/// // {
/// //     public static implicit operator CreateOrderResult(Order value) =>
/// //         new() { Value = value, IsSuccess = true };
/// //
/// //     public static implicit operator CreateOrderResult(CreateOrderError error) =>
/// //         new() { Error = error, IsSuccess = false };
/// // }
/// </code>
/// </summary>
[Generator]
public class ImplicitOperatorsGenerator : FluentGenerator
{
    protected override FileNamingOptions FileNaming => new()
    {
        Prefix = "ImplicitOperators",
        UseFoldersForPrefix = true,
        UseFoldersForNamespace = true
    };

    protected override void Configure(GeneratorContext ctx)
    {
        ctx.Types
            .ThatAreRecords()
            .ThatArePartial()
            .Implementing("Kurrent.IResultBase<>")
            .Generate((type, iface) =>
            {
                if (iface.TypeArgumentCount < 2)
                {
                    // TODO: Report diagnostic - for now, skip generation
                    return null;
                }

                var valueType = iface.TypeArgument(0);
                var errorType = iface.TypeArgument(1);

                return $$"""
                    {{type.GetNamespaceDeclaration()}}

                    {{type.GetModifiers()}} {{type.GetTypeKeyword()}} {{type.Name}}
                    {
                        /// <summary>
                        /// Creates a successful result from a value.
                        /// </summary>
                        public static implicit operator {{type.Name}}({{valueType.GlobalName()}} value) =>
                            new() { Value = value, IsSuccess = true };

                        /// <summary>
                        /// Creates a failed result from an error.
                        /// </summary>
                        public static implicit operator {{type.Name}}({{errorType.GlobalName()}} error) =>
                            new() { Error = error, IsSuccess = false };

                        /// <summary>
                        /// Extracts the value from a successful result.
                        /// </summary>
                        public static explicit operator {{valueType.GlobalName()}}({{type.Name}} result) =>
                            result.IsSuccess
                                ? result.Value
                                : throw new InvalidOperationException("Cannot extract value from failed result.");

                        /// <summary>
                        /// Extracts the error from a failed result.
                        /// </summary>
                        public static explicit operator {{errorType.GlobalName()}}({{type.Name}} result) =>
                            !result.IsSuccess
                                ? result.Error
                                : throw new InvalidOperationException("Cannot extract error from successful result.");
                    }
                    """;
            });
    }
}
