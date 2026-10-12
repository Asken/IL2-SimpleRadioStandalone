using System;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Integrations
{
    /// <summary>A link added to the admin UI's side navigation.</summary>
    public sealed record AdminNavItem(string Href, string Label);

    /// <summary>A panel added to the bottom of the Settings page (a component with no required parameters).</summary>
    public sealed record AdminSettingsSection(Type Component);

    /// <summary>An assembly with routable admin UI pages (components with <c>@page</c>).</summary>
    public sealed record AdminRazorAssembly(Assembly Assembly);

    /// <summary>
    /// Extension points for hosts that build on this server (see <see cref="Program.Run"/>):
    /// integration actions, admin pages, navigation links and settings panels.
    /// </summary>
    public static class ServerExtensions
    {
        public static IServiceCollection AddIntegrationAction<T>(this IServiceCollection services)
            where T : class, IIntegrationAction
        {
            services.TryAddSingleton<T>();
            services.AddSingleton<IIntegrationAction>(provider => provider.GetRequiredService<T>());
            return services;
        }

        public static IServiceCollection AddAdminNavItem(this IServiceCollection services, string href, string label)
        {
            return services.AddSingleton(new AdminNavItem(href, label));
        }

        public static IServiceCollection AddAdminSettingsSection<T>(this IServiceCollection services)
            where T : Microsoft.AspNetCore.Components.IComponent
        {
            return services.AddSingleton(new AdminSettingsSection(typeof(T)));
        }

        public static IServiceCollection AddAdminPages(this IServiceCollection services, Assembly assembly)
        {
            return services.AddSingleton(new AdminRazorAssembly(assembly));
        }
    }
}
