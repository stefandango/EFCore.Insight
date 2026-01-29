using EFCore.Insight.QueryCapture;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EFCore.Insight;

/// <summary>
/// Extension methods for configuring EFCore.Insight services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds EFCore.Insight services to the specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configure">Optional action to configure <see cref="InsightOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddEFCoreInsight(
        this IServiceCollection services,
        Action<InsightOptions>? configure = null)
    {
        var options = new InsightOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton(new QueryStore(options.MaxStoredQueries));

        services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();

        services.AddSingleton<EFDiagnosticListener>();

        return services;
    }
}
