using System.Text.Json;
using System.Text.Json.Serialization;
using EFCore.Insight.QueryCapture;
using Microsoft.AspNetCore.Http;

namespace EFCore.Insight.Api;

/// <summary>
/// Handles API requests for the EFCore.Insight dashboard.
/// </summary>
internal static class InsightApiEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static async Task HandleApiRequest(HttpContext context, QueryStore store, string path)
    {
        context.Response.ContentType = "application/json";

        switch (path)
        {
            case "queries" when context.Request.Method == HttpMethods.Get:
                await HandleGetQueries(context, store);
                break;

            case "queries" when context.Request.Method == HttpMethods.Delete:
                await HandleClearQueries(context, store);
                break;

            case "stats" when context.Request.Method == HttpMethods.Get:
                await HandleGetStats(context, store);
                break;

            default:
                // Check for queries/{id} pattern
                if (path.StartsWith("queries/", StringComparison.Ordinal) && context.Request.Method == HttpMethods.Get)
                {
                    var idPart = path["queries/".Length..];
                    if (Guid.TryParse(idPart, out var id))
                    {
                        await HandleGetQueryById(context, store, id);
                        return;
                    }
                }
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync("""{"error":"Not found"}""");
                break;
        }
    }

    private static async Task HandleGetQueries(HttpContext context, QueryStore store)
    {
        var queries = store.GetAll();

        // Support filtering
        var requestId = context.Request.Query["requestId"].FirstOrDefault();
        var minDuration = context.Request.Query["minDurationMs"].FirstOrDefault();
        var path = context.Request.Query["path"].FirstOrDefault();
        var errorsOnly = context.Request.Query["errorsOnly"].FirstOrDefault();

        IEnumerable<QueryEvent> filtered = queries;

        if (!string.IsNullOrEmpty(requestId))
        {
            filtered = filtered.Where(q => q.RequestId == requestId);
        }

        if (double.TryParse(minDuration, out var minMs))
        {
            filtered = filtered.Where(q => q.Duration.TotalMilliseconds >= minMs);
        }

        if (!string.IsNullOrEmpty(path))
        {
            filtered = filtered.Where(q => q.RequestPath?.Contains(path, StringComparison.OrdinalIgnoreCase) == true);
        }

        if (errorsOnly == "true")
        {
            filtered = filtered.Where(q => q.IsError);
        }

        var response = new
        {
            Count = filtered.Count(),
            Queries = filtered.Select(q => new
            {
                q.Id,
                q.Timestamp,
                q.Sql,
                q.Parameters,
                DurationMs = q.Duration.TotalMilliseconds,
                q.RowsAffected,
                q.RequestPath,
                q.RequestId,
                q.CommandType,
                q.IsError,
                q.ErrorMessage
            })
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOptions));
    }

    private static async Task HandleGetQueryById(HttpContext context, QueryStore store, Guid id)
    {
        var query = store.GetById(id);

        if (query is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("""{"error":"Query not found"}""");
            return;
        }

        var response = new
        {
            query.Id,
            query.Timestamp,
            query.Sql,
            query.Parameters,
            DurationMs = query.Duration.TotalMilliseconds,
            query.RowsAffected,
            query.RequestPath,
            query.RequestId,
            query.CommandType,
            query.IsError,
            query.ErrorMessage
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOptions));
    }

    private static async Task HandleClearQueries(HttpContext context, QueryStore store)
    {
        store.Clear();
        await context.Response.WriteAsync("""{"success":true}""");
    }

    private static async Task HandleGetStats(HttpContext context, QueryStore store)
    {
        var stats = store.GetStats();

        var response = new
        {
            stats.TotalQueries,
            stats.ErrorCount,
            stats.AverageDurationMs,
            stats.MinDurationMs,
            stats.MaxDurationMs,
            stats.TotalDurationMs,
            stats.QueriesPerRequest
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOptions));
    }
}
