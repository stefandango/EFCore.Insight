using EFCore.Insight.Cost;
using EFCore.Insight.History;
using EFCore.Insight.QueryCapture;
using EFCore.Insight.QueryPlan;
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
        services.AddSingleton(new QueryPlanService(options));
        services.AddSingleton<CostCalculator>();

        // Register history store
        if (options.EnableQueryHistory)
        {
            services.AddSingleton<IQueryHistoryStore>(
                new FileQueryHistoryStore(options.HistoryStoragePath));
        }
        else
        {
            // Use in-memory store when history is disabled (for endpoint analysis)
            services.AddSingleton<IQueryHistoryStore, InMemoryQueryHistoryStore>();
        }

        return services;
    }
}
