using EFCore.Insight.Dashboard;
using EFCore.Insight.QueryCapture;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.Insight;

/// <summary>
/// Extension methods for configuring EFCore.Insight middleware.
/// </summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the EFCore.Insight middleware to the application pipeline.
    /// This enables the dashboard at /_ef-insight and starts capturing EF Core queries.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IApplicationBuilder UseEFCoreInsight(this IApplicationBuilder app)
    {
        // Start the diagnostic listener to capture EF Core events
        var diagnosticListener = app.ApplicationServices.GetRequiredService<EFDiagnosticListener>();
        diagnosticListener.Subscribe();

        var options = app.ApplicationServices.GetRequiredService<InsightOptions>();

        // Add the dashboard middleware
        app.UseMiddleware<DashboardMiddleware>();

        return app;
    }
}
