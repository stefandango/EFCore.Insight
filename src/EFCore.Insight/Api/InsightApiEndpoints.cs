using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EFCore.Insight.Cost;
using EFCore.Insight.History;
using EFCore.Insight.QueryCapture;
using EFCore.Insight.QueryPlan;
using Microsoft.AspNetCore.Http;

namespace EFCore.Insight.Api;

/// <summary>
/// Handles API requests for the EFCore.Insight dashboard.
/// </summary>
internal static partial class InsightApiEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static async Task HandleApiRequest(HttpContext context, QueryStore store, QueryPlanService planService, InsightOptions options, string path, IQueryHistoryStore? historyStore = null, CostCalculator? costCalculator = null)
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

            case "endpoints" when context.Request.Method == HttpMethods.Get:
                await HandleGetEndpoints(context, store);
                break;

            case "patterns" when context.Request.Method == HttpMethods.Get:
                await HandleGetPatterns(context, store);
                break;

            case "history/patterns" when context.Request.Method == HttpMethods.Get:
                await HandleGetHistoryPatterns(context, historyStore);
                break;

            case "regressions" when context.Request.Method == HttpMethods.Get:
                await HandleGetRegressions(context, historyStore);
                break;

            case "cost-report" when context.Request.Method == HttpMethods.Get:
                await HandleGetCostReport(context, store, costCalculator);
                break;

            default:
                // Check for queries/{id} pattern
                if (path.StartsWith("queries/", StringComparison.Ordinal))
                {
                    var remainder = path["queries/".Length..];

                    // Check for queries/{id}/plan
                    if (remainder.EndsWith("/plan", StringComparison.Ordinal))
                    {
                        var idPart = remainder[..^"/plan".Length];
                        if (Guid.TryParse(idPart, out var planId))
                        {
                            await HandleGetQueryPlan(context, store, planService, planId);
                            return;
                        }
                    }
                    // Check for queries/{id}/analyze (POST)
                    else if (remainder.EndsWith("/analyze", StringComparison.Ordinal) && context.Request.Method == HttpMethods.Post)
                    {
                        var idPart = remainder[..^"/analyze".Length];
                        if (Guid.TryParse(idPart, out var analyzeId))
                        {
                            await HandleAnalyzeQuery(context, store, planService, analyzeId);
                            return;
                        }
                    }
                    else if (context.Request.Method == HttpMethods.Get && Guid.TryParse(remainder, out var id))
                    {
                        await HandleGetQueryById(context, store, id);
                        return;
                    }
                }

                // Check for endpoints/{path}/breakdown
                if (path.StartsWith("endpoints/", StringComparison.Ordinal) && path.EndsWith("/breakdown", StringComparison.Ordinal))
                {
                    var endpointPath = path["endpoints/".Length..^"/breakdown".Length];
                    await HandleGetEndpointBreakdown(context, store, Uri.UnescapeDataString(endpointPath));
                    return;
                }

                // Check for history/patterns/{hash}/baseline (POST)
                if (path.StartsWith("history/patterns/", StringComparison.Ordinal) && path.EndsWith("/baseline", StringComparison.Ordinal) && context.Request.Method == HttpMethods.Post)
                {
                    var hash = path["history/patterns/".Length..^"/baseline".Length];
                    await HandleSetBaseline(context, historyStore, hash);
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync("""{"error":"Not found"}""");
                break;
        }
    }

    private static async Task HandleGetQueries(HttpContext context, QueryStore store)
    {
        var queries = store.GetAll();
        var stats = store.GetStats();

        // Build a set of query IDs that are part of N+1 patterns
        var n1QueryIds = stats.N1Patterns
            .SelectMany(p => p.QueryIds)
            .ToHashSet();

        // Build a set of query IDs that are part of split query groups
        var splitQueryIds = stats.SplitQueryGroups
            .SelectMany(g => g.QueryIds)
            .ToHashSet();

        // Support filtering
        var requestId = context.Request.Query["requestId"].FirstOrDefault();
        var minDuration = context.Request.Query["minDurationMs"].FirstOrDefault();
        var path = context.Request.Query["path"].FirstOrDefault();
        var errorsOnly = context.Request.Query["errorsOnly"].FirstOrDefault();
        var n1Only = context.Request.Query["n1Only"].FirstOrDefault();
        var splitOnly = context.Request.Query["splitOnly"].FirstOrDefault();

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

        if (n1Only == "true")
        {
            filtered = filtered.Where(q => n1QueryIds.Contains(q.Id));
        }

        if (splitOnly == "true")
        {
            filtered = filtered.Where(q => splitQueryIds.Contains(q.Id));
        }

        // Find N+1 count for each query
        var queryN1Counts = stats.N1Patterns
            .SelectMany(p => p.QueryIds.Select(id => (Id: id, Count: p.Count)))
            .GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => g.Max(x => x.Count));

        // Find split group info for each query (include group index as ID)
        var querySplitInfo = stats.SplitQueryGroups
            .SelectMany((g, idx) => g.QueryIds.Select(id => (Id: id, GroupId: idx, GroupSize: g.QueryCount, Tables: g.Tables)))
            .GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => g.First());

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
                q.ErrorMessage,
                IsN1 = n1QueryIds.Contains(q.Id),
                N1Count = queryN1Counts.GetValueOrDefault(q.Id, 0),
                IsSplit = splitQueryIds.Contains(q.Id),
                SplitGroupId = querySplitInfo.TryGetValue(q.Id, out var grpInfo) ? grpInfo.GroupId : (int?)null,
                SplitGroupSize = querySplitInfo.TryGetValue(q.Id, out var info) ? info.GroupSize : 0,
                SplitTables = querySplitInfo.TryGetValue(q.Id, out var tableInfo) ? tableInfo.Tables : null,
                q.CallSite,
                q.CallingMethod,
                q.StackTrace,
                Suggestions = QueryAnalyzer.Analyze(q).Select(s => new
                {
                    Type = s.Type.ToString(),
                    Severity = s.Severity.ToString(),
                    s.Title,
                    s.Message,
                    s.SuggestedFix,
                    s.Table,
                    s.Column
                })
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
            stats.QueriesPerRequest,
            N1PatternCount = stats.N1Patterns.Count,
            N1Patterns = stats.N1Patterns.Select(p => new
            {
                p.NormalizedSql,
                p.Count,
                p.RequestId,
                p.RequestPath,
                p.QueryIds,
                p.TotalDurationMs,
                p.AverageDurationMs
            }),
            SplitQueryGroupCount = stats.SplitQueryGroups.Count,
            SplitQueryGroups = stats.SplitQueryGroups.Select(g => new
            {
                g.RequestId,
                g.RequestPath,
                g.QueryCount,
                g.QueryIds,
                g.TotalDurationMs,
                g.Tables
            }),
            Connections = stats.Connections
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOptions));
    }

    private static async Task HandleGetQueryPlan(HttpContext context, QueryStore store, QueryPlanService planService, Guid id)
    {
        var query = store.GetById(id);

        if (query is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("""{"error":"Query not found"}""");
            return;
        }

        if (string.IsNullOrEmpty(query.ProviderName) || string.IsNullOrEmpty(query.ConnectionString))
        {
            var missing = new List<string>();
            if (string.IsNullOrEmpty(query.ProviderName)) missing.Add("provider name");
            if (string.IsNullOrEmpty(query.ConnectionString)) missing.Add("connection string");

            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                error = $"Query plan analysis requires: {string.Join(", ", missing)}. This query was captured without the necessary database connection details. Make sure the query is executed through Entity Framework Core."
            }, JsonOptions));
            return;
        }

        var plan = await planService.GetPlanAsync(query.Sql, query.ProviderName, query.ConnectionString, context.RequestAborted);

        if (plan is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync($$$"""{"error":"Provider '{{{query.ProviderName}}}' is not supported for plan analysis"}""");
            return;
        }

        var response = new
        {
            QueryId = query.Id,
            plan.Sql,
            plan.RawPlan,
            plan.Provider,
            plan.IsSuccess,
            plan.ErrorMessage,
            plan.EstimatedCost,
            plan.EstimatedRows,
            plan.ActualTimeMs,
            Nodes = plan.Nodes.Select(MapPlanNode),
            Issues = plan.Issues.Select(i => new
            {
                Type = i.Type.ToString(),
                Severity = i.Severity.ToString(),
                i.Title,
                i.Message,
                i.SuggestedFix,
                i.Table,
                i.Column
            })
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOptions));
    }

    private static async Task HandleAnalyzeQuery(HttpContext context, QueryStore store, QueryPlanService planService, Guid id)
    {
        var query = store.GetById(id);

        if (query is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("""{"error":"Query not found"}""");
            return;
        }

        if (string.IsNullOrEmpty(query.ProviderName) || string.IsNullOrEmpty(query.ConnectionString))
        {
            var missing = new List<string>();
            if (string.IsNullOrEmpty(query.ProviderName)) missing.Add("provider name");
            if (string.IsNullOrEmpty(query.ConnectionString)) missing.Add("connection string");

            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                error = $"Query plan analysis requires: {string.Join(", ", missing)}. This query was captured without the necessary database connection details. Make sure the query is executed through Entity Framework Core."
            }, JsonOptions));
            return;
        }

        var plan = await planService.GetPlanAsync(query.Sql, query.ProviderName, query.ConnectionString, context.RequestAborted);

        if (plan is null || !plan.IsSuccess)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = plan?.ErrorMessage ?? "Plan analysis failed" }, JsonOptions));
            return;
        }

        var response = new
        {
            QueryId = query.Id,
            plan.Sql,
            plan.RawPlan,
            plan.Provider,
            plan.IsSuccess,
            plan.EstimatedCost,
            plan.EstimatedRows,
            plan.ActualTimeMs,
            Nodes = plan.Nodes.Select(MapPlanNode),
            Issues = plan.Issues.Select(i => new
            {
                Type = i.Type.ToString(),
                Severity = i.Severity.ToString(),
                i.Title,
                i.Message,
                i.SuggestedFix,
                i.Table,
                i.Column
            })
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOptions));
    }

    private static object MapPlanNode(PlanNode node)
    {
        return new
        {
            node.Operation,
            node.ObjectName,
            node.Details,
            node.EstimatedCost,
            node.EstimatedRows,
            node.ActualTimeMs,
            node.ActualRows,
            node.IsTableScan,
            node.UsesIndex,
            node.IndexName,
            node.Depth,
            Children = node.Children.Select(MapPlanNode)
        };
    }

    private static async Task HandleGetEndpoints(HttpContext context, QueryStore store)
    {
        var queries = store.GetAll();

        var endpoints = queries
            .Where(q => q.RequestPath is not null && q.HttpMethod is not null)
            .GroupBy(q => $"{q.HttpMethod} {NormalizeEndpointPath(q.RequestPath!)}")
            .Select(g =>
            {
                var queriesList = g.ToList();
                var n1Queries = queriesList.Where(q => IsPartOfN1Pattern(q, store)).ToList();
                var slowQueries = queriesList.Where(q => q.Duration.TotalMilliseconds >= 100).ToList();

                return new
                {
                    Endpoint = g.Key,
                    QueryCount = queriesList.Count,
                    TotalDurationMs = queriesList.Sum(q => q.Duration.TotalMilliseconds),
                    AvgDurationMs = queriesList.Average(q => q.Duration.TotalMilliseconds),
                    N1QueryCount = n1Queries.Count,
                    N1DurationMs = n1Queries.Sum(q => q.Duration.TotalMilliseconds),
                    SlowQueryCount = slowQueries.Count,
                    SlowDurationMs = slowQueries.Sum(q => q.Duration.TotalMilliseconds),
                    NormalDurationMs = queriesList.Sum(q => q.Duration.TotalMilliseconds) -
                                       n1Queries.Sum(q => q.Duration.TotalMilliseconds) -
                                       slowQueries.Where(q => !n1Queries.Contains(q)).Sum(q => q.Duration.TotalMilliseconds),
                    UniquePatterns = queriesList.Select(q => q.PatternHash).Distinct().Count(),
                    RequestCount = queriesList.Select(q => q.RequestId).Distinct().Count()
                };
            })
            .OrderByDescending(e => e.TotalDurationMs)
            .ToList();

        var response = new
        {
            Count = endpoints.Count,
            Endpoints = endpoints
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOptions));
    }

    private static async Task HandleGetEndpointBreakdown(HttpContext context, QueryStore store, string endpointPath)
    {
        var queries = store.GetAll();

        var endpointQueries = queries
            .Where(q => q.RequestPath is not null && q.HttpMethod is not null &&
                       $"{q.HttpMethod} {NormalizeEndpointPath(q.RequestPath!)}" == endpointPath)
            .ToList();

        if (endpointQueries.Count == 0)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("""{"error":"Endpoint not found"}""");
            return;
        }

        var n1Queries = endpointQueries.Where(q => IsPartOfN1Pattern(q, store)).ToList();
        var slowQueries = endpointQueries.Where(q => q.Duration.TotalMilliseconds >= 100 && !n1Queries.Contains(q)).ToList();
        var normalQueries = endpointQueries.Except(n1Queries).Except(slowQueries).ToList();

        var patterns = endpointQueries
            .GroupBy(q => q.PatternHash)
            .Select(g => new
            {
                PatternHash = g.Key,
                NormalizedSql = g.First().NormalizedSql,
                Count = g.Count(),
                TotalDurationMs = g.Sum(q => q.Duration.TotalMilliseconds),
                AvgDurationMs = g.Average(q => q.Duration.TotalMilliseconds),
                IsN1 = g.Any(q => n1Queries.Contains(q))
            })
            .OrderByDescending(p => p.TotalDurationMs)
            .ToList();

        var response = new
        {
            Endpoint = endpointPath,
            QueryCount = endpointQueries.Count,
            TotalDurationMs = endpointQueries.Sum(q => q.Duration.TotalMilliseconds),
            Breakdown = new
            {
                N1 = new
                {
                    Count = n1Queries.Count,
                    DurationMs = n1Queries.Sum(q => q.Duration.TotalMilliseconds),
                    Percentage = endpointQueries.Sum(q => q.Duration.TotalMilliseconds) > 0
                        ? n1Queries.Sum(q => q.Duration.TotalMilliseconds) / endpointQueries.Sum(q => q.Duration.TotalMilliseconds) * 100
                        : 0
                },
                Slow = new
                {
                    Count = slowQueries.Count,
                    DurationMs = slowQueries.Sum(q => q.Duration.TotalMilliseconds),
                    Percentage = endpointQueries.Sum(q => q.Duration.TotalMilliseconds) > 0
                        ? slowQueries.Sum(q => q.Duration.TotalMilliseconds) / endpointQueries.Sum(q => q.Duration.TotalMilliseconds) * 100
                        : 0
                },
                Normal = new
                {
                    Count = normalQueries.Count,
                    DurationMs = normalQueries.Sum(q => q.Duration.TotalMilliseconds),
                    Percentage = endpointQueries.Sum(q => q.Duration.TotalMilliseconds) > 0
                        ? normalQueries.Sum(q => q.Duration.TotalMilliseconds) / endpointQueries.Sum(q => q.Duration.TotalMilliseconds) * 100
                        : 0
                }
            },
            Patterns = patterns
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOptions));
    }

    private static async Task HandleGetPatterns(HttpContext context, QueryStore store)
    {
        var queries = store.GetAll();

        var patterns = queries
            .GroupBy(q => q.PatternHash)
            .Select(g =>
            {
                var list = g.ToList();
                var durations = list.Select(q => q.Duration.TotalMilliseconds).OrderBy(d => d).ToList();
                var p95Index = (int)(durations.Count * 0.95);

                return new
                {
                    PatternHash = g.Key,
                    NormalizedSql = g.First().NormalizedSql,
                    ExecutionCount = list.Count,
                    TotalDurationMs = list.Sum(q => q.Duration.TotalMilliseconds),
                    AvgDurationMs = list.Average(q => q.Duration.TotalMilliseconds),
                    MinDurationMs = list.Min(q => q.Duration.TotalMilliseconds),
                    MaxDurationMs = list.Max(q => q.Duration.TotalMilliseconds),
                    P95DurationMs = durations.Count > 0 ? durations[Math.Min(p95Index, durations.Count - 1)] : 0,
                    FirstSeen = list.Min(q => q.Timestamp),
                    LastSeen = list.Max(q => q.Timestamp),
                    UniqueEndpoints = list.Where(q => q.RequestPath is not null)
                        .Select(q => $"{q.HttpMethod} {q.RequestPath}").Distinct().Take(5).ToList(),
                    HasErrors = list.Any(q => q.IsError),
                    ErrorCount = list.Count(q => q.IsError)
                };
            })
            .OrderByDescending(p => p.TotalDurationMs)
            .ToList();

        var response = new
        {
            Count = patterns.Count,
            Patterns = patterns
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOptions));
    }

    private static bool IsPartOfN1Pattern(QueryEvent query, QueryStore store)
    {
        var stats = store.GetStats();
        return stats.N1Patterns.Any(p => p.QueryIds.Contains(query.Id));
    }

    private static string NormalizeEndpointPath(string path)
    {
        // Replace numeric segments with {id} placeholder
        // e.g., /api/users/123/orders/456 -> /api/users/{id}/orders/{id}
        return NumericPathSegmentRegex().Replace(path, "/{id}");
    }

    private static async Task HandleGetHistoryPatterns(HttpContext context, IQueryHistoryStore? historyStore)
    {
        if (historyStore is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("""{"error":"Query history is not enabled. Enable it with options.EnableQueryHistory = true"}""");
            return;
        }

        var patterns = await historyStore.GetPatternsAsync(context.RequestAborted);

        var response = new
        {
            Count = patterns.Count,
            Patterns = patterns.Select(p => new
            {
                p.PatternHash,
                p.NormalizedSql,
                p.ExecutionCount,
                p.AvgDurationMs,
                p.MinDurationMs,
                p.MaxDurationMs,
                p.P95DurationMs,
                p.TotalDurationMs,
                p.FirstSeen,
                p.LastSeen,
                p.BaselineAvgDurationMs,
                p.BaselineSetAt,
                HasBaseline = p.BaselineAvgDurationMs.HasValue,
                PercentChangeFromBaseline = p.BaselineAvgDurationMs.HasValue && p.BaselineAvgDurationMs.Value > 0
                    ? ((p.AvgDurationMs - p.BaselineAvgDurationMs.Value) / p.BaselineAvgDurationMs.Value) * 100
                    : (double?)null,
                RecentSamples = p.RecentSamples.Select(s => new
                {
                    s.Timestamp,
                    s.DurationMs
                })
            })
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOptions));
    }

    private static async Task HandleGetRegressions(HttpContext context, IQueryHistoryStore? historyStore)
    {
        if (historyStore is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("""{"error":"Query history is not enabled. Enable it with options.EnableQueryHistory = true"}""");
            return;
        }

        var thresholdParam = context.Request.Query["threshold"].FirstOrDefault();
        var threshold = double.TryParse(thresholdParam, out var t) ? t : 20;

        var regressions = await historyStore.GetRegressionsAsync(threshold, context.RequestAborted);

        var response = new
        {
            Count = regressions.Count,
            ThresholdPercent = threshold,
            Regressions = regressions.Select(r => new
            {
                r.Pattern.PatternHash,
                r.Pattern.NormalizedSql,
                r.Pattern.ExecutionCount,
                CurrentAvgMs = r.Pattern.AvgDurationMs,
                BaselineAvgMs = r.Pattern.BaselineAvgDurationMs,
                r.PercentChange,
                r.AbsoluteChangeMs,
                Severity = r.Severity.ToString(),
                r.Pattern.FirstSeen,
                r.Pattern.LastSeen,
                r.Pattern.BaselineSetAt
            })
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOptions));
    }

    private static async Task HandleSetBaseline(HttpContext context, IQueryHistoryStore? historyStore, string patternHash)
    {
        if (historyStore is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("""{"error":"Query history is not enabled. Enable it with options.EnableQueryHistory = true"}""");
            return;
        }

        var pattern = await historyStore.GetPatternAsync(patternHash, context.RequestAborted);

        if (pattern is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("""{"error":"Pattern not found"}""");
            return;
        }

        await historyStore.SetBaselineAsync(patternHash, context.RequestAborted);

        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            Success = true,
            PatternHash = patternHash,
            BaselineSetAt = DateTime.UtcNow,
            BaselineAvgDurationMs = pattern.AvgDurationMs
        }, JsonOptions));
    }

    private static async Task HandleGetCostReport(HttpContext context, QueryStore store, CostCalculator? costCalculator)
    {
        _ = costCalculator; // Unused parameter kept for consistent API signature

        var report = CostCalculator.Calculate(store);

        var response = new
        {
            report.GeneratedAt,
            report.TimeWindowMinutes,
            report.TotalQueryCount,
            report.TotalQueryTimeMs,
            report.TotalTimeSavedPerMinMs,
            report.TotalTimeSpentPerMinMs,
            report.PotentialSavingsPercent,
            RecommendationCount = report.Recommendations.Count,
            Recommendations = report.Recommendations.Select(r => new
            {
                r.IssueType,
                r.PatternHash,
                r.NormalizedSql,
                r.Description,
                r.ExecutionCount,
                r.ExecutionsPerMinute,
                r.AvgDurationMs,
                r.TotalDurationMs,
                r.EstimatedSavingsPercent,
                r.EstimatedTimeSavedPerMinMs,
                TimeSavedPerMinFormatted = FormatTimeSaved(r.EstimatedTimeSavedPerMinMs),
                r.RequestPath,
                r.SuggestedFix,
                Severity = r.Severity.ToString(),
                AffectedQueryCount = r.AffectedQueryIds.Count,
                r.Table,
                r.Column
            })
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOptions));
    }

    private static string FormatTimeSaved(double milliseconds)
    {
        return milliseconds switch
        {
            >= 60000 => $"{milliseconds / 60000:F1} min/min",
            >= 1000 => $"{milliseconds / 1000:F1} sec/min",
            >= 10 => $"{milliseconds:F0} ms/min",
            >= 0.1 => $"{milliseconds:F1} ms/min",
            > 0 => "< 0.1 ms/min",
            _ => "0 ms/min"
        };
    }

    [GeneratedRegex(@"/\d+")]
    private static partial Regex NumericPathSegmentRegex();
}
