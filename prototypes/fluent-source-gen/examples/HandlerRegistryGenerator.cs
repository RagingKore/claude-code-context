using FluentSourceGen;
using Microsoft.CodeAnalysis;

namespace Examples;

/// <summary>
/// Generates a handler registry for CQRS pattern by scanning ICommandHandler and IQueryHandler implementations.
/// Demonstrates projection and grouping features.
///
/// Example usage:
/// <code>
/// public interface ICommandHandler&lt;TCommand, TResult&gt; { }
/// public interface IQueryHandler&lt;TQuery, TResult&gt; { }
///
/// public class CreateOrderHandler : ICommandHandler&lt;CreateOrderCommand, OrderId&gt; { }
/// public class GetOrderHandler : IQueryHandler&lt;GetOrderQuery, Order&gt; { }
///
/// // Generates:
/// // public static class HandlerRegistry
/// // {
/// //     public static void RegisterHandlers(IServiceCollection services)
/// //     {
/// //         // Command Handlers
/// //         services.AddScoped&lt;ICommandHandler&lt;CreateOrderCommand, OrderId&gt;, CreateOrderHandler&gt;();
/// //
/// //         // Query Handlers
/// //         services.AddScoped&lt;IQueryHandler&lt;GetOrderQuery, Order&gt;, GetOrderHandler&gt;();
/// //     }
/// // }
/// </code>
/// </summary>
[Generator]
public class HandlerRegistryGenerator : FluentGenerator
{
    protected override void Configure(GeneratorContext ctx)
    {
        // Project handlers to extract their handler type and interface
        ctx.Types
            .ThatAreClasses()
            .Not.ThatAreAbstract()
            .ImplementingAny("Kurrent.ICommandHandler<>", "Kurrent.IQueryHandler<>")
            .Select(type => new
            {
                Type = type,
                CommandInterface = type.FindInterface("Kurrent.ICommandHandler<>"),
                QueryInterface = type.FindInterface("Kurrent.IQueryHandler<>"),
                IsCommand = type.ImplementsInterface("Kurrent.ICommandHandler<>")
            })
            .ForAll((handlers, emit) =>
            {
                var commandHandlers = handlers
                    .Where(h => h.IsCommand && h.CommandInterface is not null)
                    .Select(h => $"services.AddScoped<{h.CommandInterface!.GlobalName()}, {h.Type.GlobalName()}>();")
                    .ToList();

                var queryHandlers = handlers
                    .Where(h => !h.IsCommand && h.QueryInterface is not null)
                    .Select(h => $"services.AddScoped<{h.QueryInterface!.GlobalName()}, {h.Type.GlobalName()}>();")
                    .ToList();

                var commandCode = commandHandlers.Count > 0
                    ? "// Command Handlers\n            " + string.Join("\n            ", commandHandlers)
                    : "// No command handlers found";

                var queryCode = queryHandlers.Count > 0
                    ? "// Query Handlers\n            " + string.Join("\n            ", queryHandlers)
                    : "// No query handlers found";

                emit.Source("HandlerRegistry.g.cs", $$"""
                    using Microsoft.Extensions.DependencyInjection;

                    namespace Kurrent.Generated;

                    /// <summary>
                    /// Auto-generated handler registry for CQRS pattern.
                    /// Found {{commandHandlers.Count}} command handlers and {{queryHandlers.Count}} query handlers.
                    /// </summary>
                    public static class HandlerRegistry
                    {
                        /// <summary>
                        /// Registers all discovered command and query handlers.
                        /// </summary>
                        public static IServiceCollection AddHandlers(this IServiceCollection services)
                        {
                            {{commandCode}}

                            {{queryCode}}

                            return services;
                        }
                    }
                    """);
            });
    }
}

/// <summary>
/// Alternative handler registry using SelectMany to extract all handler interfaces.
/// </summary>
[Generator]
public class FlattenedHandlerRegistryGenerator : FluentGenerator
{
    protected override void Configure(GeneratorContext ctx)
    {
        // Use SelectMany to extract all handler interfaces from all types
        ctx.Types
            .ThatAreClasses()
            .Not.ThatAreAbstract()
            .SelectMany(type => type.AllInterfaces
                .Where(i => i.Name.StartsWith("ICommandHandler") || i.Name.StartsWith("IQueryHandler"))
                .Select(iface => new
                {
                    Handler = type,
                    Interface = iface,
                    Kind = iface.Name.StartsWith("ICommand") ? "Command" : "Query"
                }))
            .ForAll((registrations, emit) =>
            {
                var grouped = registrations
                    .GroupBy(r => r.Kind)
                    .ToDictionary(g => g.Key, g => g.ToList());

                var commands = grouped.GetValueOrDefault("Command", []);
                var queries = grouped.GetValueOrDefault("Query", []);

                emit.Source("FlattenedHandlerRegistry.g.cs", $$"""
                    using Microsoft.Extensions.DependencyInjection;

                    namespace Kurrent.Generated;

                    /// <summary>
                    /// Handler registry using flattened interface extraction.
                    /// </summary>
                    public static class FlattenedHandlerRegistry
                    {
                        public static IServiceCollection AddAllHandlers(this IServiceCollection services)
                        {
                            // {{commands.Count}} Command Handlers
                            {{string.Join("\n            ", commands.Select(c => $"services.AddScoped<{c.Interface.GlobalName()}, {c.Handler.GlobalName()}>();"))}}

                            // {{queries.Count}} Query Handlers
                            {{string.Join("\n            ", queries.Select(q => $"services.AddScoped<{q.Interface.GlobalName()}, {q.Handler.GlobalName()}>();"))}}

                            return services;
                        }
                    }
                    """);
            });
    }
}
