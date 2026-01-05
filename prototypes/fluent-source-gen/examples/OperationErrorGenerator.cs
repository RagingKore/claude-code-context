using FluentSourceGen;
using Microsoft.CodeAnalysis;

namespace Examples;

/// <summary>
/// Generates operation error implementations with variant support.
///
/// Example usage:
/// <code>
/// // Define the base error interface with variants
/// public interface IOperationError&lt;out TVariant1, out TVariant2&gt; { }
///
/// // The error type implementing variants
/// [GenerateOperationError]
/// public partial record CreateOrderError : IOperationError&lt;ValidationError, ConflictError&gt;;
///
/// // Generates:
/// // partial record CreateOrderError
/// // {
/// //     private CreateOrderError() { }
/// //
/// //     public bool IsValidationError => _variant is ValidationError;
/// //     public bool IsConflictError => _variant is ConflictError;
/// //
/// //     public ValidationError? AsValidationError => _variant as ValidationError;
/// //     public ConflictError? AsConflictError => _variant as ConflictError;
/// //
/// //     public static implicit operator CreateOrderError(ValidationError error) => new() { _variant = error };
/// //     public static implicit operator CreateOrderError(ConflictError error) => new() { _variant = error };
/// //
/// //     public TResult Match&lt;TResult&gt;(
/// //         Func&lt;ValidationError, TResult&gt; onValidationError,
/// //         Func&lt;ConflictError, TResult&gt; onConflictError) => ...
/// // }
/// </code>
/// </summary>
[Generator]
public class OperationErrorGenerator : FluentGenerator
{
    protected override FileNamingOptions FileNaming => new()
    {
        Prefix = "OperationErrors",
        UseFoldersForPrefix = true,
        UseFoldersForNamespace = true
    };

    protected override void Configure(GeneratorContext ctx)
    {
        var query = ctx.Types
            .ThatAreRecords()
            .ThatArePartial()
            .WithAttribute("Kurrent.GenerateOperationErrorAttribute")
            .Implementing("Kurrent.IOperationError<>");

        ctx.Generate(query, (type, attr, iface) =>
        {
            var variants = iface.TypeArguments;

            if (variants.Count == 0)
            {
                // TODO: Report diagnostic - for now, skip generation
                return null;
            }

            var variantCode = GenerateVariantCode(type, variants);

            return $$"""
                {{type.GetNamespaceDeclaration()}}

                {{type.GetModifiers()}} {{type.GetTypeKeyword()}} {{type.Name}}
                {
                    private readonly object? _variant;

                    private {{type.Name}}() { }

                {{variantCode}}
                }
                """;
        });
    }

    static string GenerateVariantCode(INamedTypeSymbol errorType, IReadOnlyList<ITypeSymbol> variants)
    {
        var sb = new System.Text.StringBuilder();
        var indent = "    ";

        // Generate Is* properties
        foreach (var variant in variants)
        {
            var variantName = variant.Name;
            sb.AppendLine($"{indent}public bool Is{variantName} => _variant is {variant.GlobalName()};");
        }

        sb.AppendLine();

        // Generate As* properties
        foreach (var variant in variants)
        {
            var variantName = variant.Name;
            sb.AppendLine($"{indent}public {variant.GlobalName()}? As{variantName} => _variant as {variant.GlobalName()};");
        }

        sb.AppendLine();

        // Generate implicit operators
        foreach (var variant in variants)
        {
            sb.AppendLine($"{indent}public static implicit operator {errorType.Name}({variant.GlobalName()} error) => new() {{ _variant = error }};");
        }

        sb.AppendLine();

        // Generate Match method
        var matchParams = string.Join(",\n        ",
            variants.Select(v => $"Func<{v.GlobalName()}, TResult> on{v.Name}"));

        var matchCases = string.Join("\n            ",
            variants.Select(v => $"{v.GlobalName()} v{v.Name} => on{v.Name}(v{v.Name}),"));

        sb.AppendLine($"""
    {indent}public TResult Match<TResult>(
            {matchParams})
        {{
            return _variant switch
            {{
                {matchCases}
                _ => throw new InvalidOperationException("Variant is not set.")
            }};
        }}
    """);

        // Generate Switch method (void version)
        var switchParams = string.Join(",\n        ",
            variants.Select(v => $"Action<{v.GlobalName()}>? on{v.Name} = null"));

        var switchCases = string.Join("\n            ",
            variants.Select(v => $"{v.GlobalName()} v{v.Name} => on{v.Name}?.Invoke(v{v.Name}),"));

        sb.AppendLine();
        sb.AppendLine($"""
    {indent}public void Switch(
            {switchParams})
        {{
            _ = _variant switch
            {{
                {switchCases}
                _ => throw new InvalidOperationException("Variant is not set.")
            }};
        }}
    """);

        return sb.ToString();
    }
}
