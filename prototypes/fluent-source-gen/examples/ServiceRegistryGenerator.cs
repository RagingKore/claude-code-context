using FluentSourceGen;
using Microsoft.CodeAnalysis;

namespace Examples;

/// <summary>
/// Generates service registration code for types marked with [AutoRegister].
/// Uses GenerateAll to collect all services and generate a single registry file.
///
/// Example usage:
/// <code>
/// [AutoRegister]
/// public class OrderService : IOrderService { }
///
/// [AutoRegister(ServiceLifetime.Singleton)]
/// public class CacheService : ICacheService { }
///
/// // Generates:
/// // public static class ServiceRegistry
/// // {
/// //     public static IServiceCollection AddGeneratedServices(this IServiceCollection services)
/// //     {
/// //         services.AddScoped&lt;IOrderService, OrderService&gt;();
/// //         services.AddSingleton&lt;ICacheService, CacheService&gt;();
/// //         return services;
/// //     }
/// // }
/// </code>
/// </summary>
[Generator]
public class ServiceRegistryGenerator : FluentGenerator
{
    protected override void Execute(GeneratorContext ctx)
    {
        ctx.Types
            .ThatAreClasses()
            .ThatAreNotAbstract()
            .ThatArePublic()
            .WithAttribute("Kurrent.AutoRegisterAttribute")
            .GenerateAll(types =>
            {
                var registrations = types
                    .Select(item => GenerateRegistration(item.Symbol, item.Attribute))
                    .ToList();

                var registrationCode = string.Join("\n            ", registrations);

                return ("ServiceRegistry.g.cs", $$"""
                    using Microsoft.Extensions.DependencyInjection;

                    namespace Kurrent.Generated;

                    /// <summary>
                    /// Auto-generated service registrations.
                    /// </summary>
                    public static class ServiceRegistry
                    {
                        /// <summary>
                        /// Registers all auto-discovered services.
                        /// </summary>
                        public static IServiceCollection AddGeneratedServices(this IServiceCollection services)
                        {
                            {{registrationCode}}
                            return services;
                        }
                    }
                    """);
            });
    }

    static string GenerateRegistration(INamedTypeSymbol type, AttributeMatch attr)
    {
        // Try to get lifetime from attribute, default to Scoped
        var lifetime = "Scoped";
        if (attr.TryGetConstructorArgument<int>(0, out var lifetimeValue))
        {
            lifetime = lifetimeValue switch
            {
                0 => "Singleton",
                1 => "Scoped",
                2 => "Transient",
                _ => "Scoped"
            };
        }

        // Find the primary interface (first one that's not IDisposable, etc.)
        var serviceInterface = type.AllInterfaces
            .FirstOrDefault(i =>
                !i.Name.StartsWith("IDisposable") &&
                !i.Name.StartsWith("IAsyncDisposable") &&
                !i.Name.StartsWith("IEquatable"));

        if (serviceInterface is not null)
        {
            return $"services.Add{lifetime}<{serviceInterface.GlobalName()}, {type.GlobalName()}>();";
        }

        // If no interface, register as self
        return $"services.Add{lifetime}<{type.GlobalName()}>();";
    }
}

/// <summary>
/// Generates service registrations grouped by namespace.
/// Creates one partial registry class per namespace.
/// </summary>
[Generator]
public class NamespacedServiceRegistryGenerator : FluentGenerator
{
    protected override void Execute(GeneratorContext ctx)
    {
        ctx.Types
            .ThatAreClasses()
            .ThatAreNotAbstract()
            .ThatArePublic()
            .WithAttribute("Kurrent.AutoRegisterAttribute")
            .GroupByNamespace()
            .Generate((ns, types) =>
            {
                if (string.IsNullOrEmpty(ns)) return null;

                var registrations = types.Select(t =>
                {
                    var serviceInterface = t.AllInterfaces
                        .FirstOrDefault(i => !i.Name.StartsWith("IDisposable"));

                    return serviceInterface is not null
                        ? $"services.AddScoped<{serviceInterface.GlobalName()}, {t.GlobalName()}>();"
                        : $"services.AddScoped<{t.GlobalName()}>();";
                });

                var registrationCode = string.Join("\n            ", registrations);
                var className = ns.Split('.').Last() + "Services";
                var hintName = SourceGeneratorFileNaming.GetNamespaceGroupHintName(ns, FileNamingOptions.Default);

                return (hintName, $$"""
                    using Microsoft.Extensions.DependencyInjection;

                    namespace {{ns}};

                    /// <summary>
                    /// Auto-generated service registrations for {{ns}}.
                    /// </summary>
                    public static partial class {{className}}
                    {
                        /// <summary>
                        /// Registers services from {{ns}}.
                        /// </summary>
                        public static IServiceCollection Add{{className}}(this IServiceCollection services)
                        {
                            {{registrationCode}}
                            return services;
                        }
                    }
                    """);
            });
    }
}
