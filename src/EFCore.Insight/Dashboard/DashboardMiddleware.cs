using System.Reflection;
using EFCore.Insight.Api;
using EFCore.Insight.QueryCapture;
using Microsoft.AspNetCore.Http;

namespace EFCore.Insight.Dashboard;

/// <summary>
/// Middleware that serves the EFCore.Insight dashboard and API endpoints.
/// </summary>
internal sealed class DashboardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly InsightOptions _options;
    private readonly QueryStore _store;
    private readonly string? _dashboardHtml;

    public DashboardMiddleware(RequestDelegate next, InsightOptions options, QueryStore store)
    {
        _next = next;
        _options = options;
        _store = store;
        _dashboardHtml = LoadEmbeddedResource("Dashboard.assets.index.html");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        var prefix = $"/{_options.NormalizedRoutePrefix}";

        // Check if this request is for the insight dashboard/API
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var subPath = path[prefix.Length..].TrimStart('/');

        // Handle API requests
        if (subPath.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
        {
            var apiPath = subPath["api/".Length..];
            await InsightApiEndpoints.HandleApiRequest(context, _store, apiPath);
            return;
        }

        // Serve dashboard HTML for root path
        if (string.IsNullOrEmpty(subPath) || subPath == "/")
        {
            await ServeDashboard(context);
            return;
        }

        // Unknown path
        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private async Task ServeDashboard(HttpContext context)
    {
        if (_dashboardHtml is null)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("Dashboard HTML not found");
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(_dashboardHtml);
    }

    private static string? LoadEmbeddedResource(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var fullResourceName = $"EFCore.Insight.{resourceName}";

        using var stream = assembly.GetManifestResourceStream(fullResourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
