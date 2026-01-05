using FluentSourceGen;
using Microsoft.CodeAnalysis;

namespace Examples;

/// <summary>
/// Generates value object implementations for types marked with [ValueObject&lt;T&gt;].
///
/// Example usage:
/// <code>
/// [ValueObject&lt;string&gt;]
/// public readonly partial record struct CustomerId;
///
/// // Generates:
/// // readonly partial record struct CustomerId
/// // {
/// //     public string Value { get; private init; }
/// //     public static implicit operator string(CustomerId _) => _.Value;
/// //     public static implicit operator CustomerId(string _) => new() { Value = _ };
/// // }
/// </code>
/// </summary>
[Generator]
public class ValueObjectGenerator : FluentGenerator
{
    protected override void Configure(GeneratorContext ctx)
    {
        ctx.Types
            .ThatArePartial()
            .WithAttribute("Kurrent.ValueObjectAttribute<>")
            .ForEach((type, attr, emit) =>
            {
                var valueType = attr.TypeArgument(0);

                emit.Source(
                    new FileNamingOptions { Prefix = "ValueObjects" },
                    $$"""
                    {{type.GetNamespaceDeclaration()}}

                    {{type.GetModifiers()}} {{type.GetTypeKeyword()}} {{type.Name}}
                    {
                        public {{valueType.FullName()}} Value { get; private init; }

                        public static implicit operator {{valueType.FullName()}}({{type.Name}} _) => _.Value;
                        public static implicit operator {{type.Name}}({{valueType.FullName()}} _) => new() { Value = _ };

                        public override string ToString() => Value?.ToString() ?? string.Empty;
                    }
                    """,
                    valueType);
            });
    }
}
