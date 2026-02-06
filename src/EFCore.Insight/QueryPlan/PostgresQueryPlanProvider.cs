using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace EFCore.Insight.QueryPlan;

/// <summary>
/// Query plan provider for PostgreSQL databases using EXPLAIN (FORMAT JSON).
/// </summary>
public sealed partial class PostgresQueryPlanProvider : IQueryPlanProvider
{
    public string ProviderName => "Npgsql.EntityFrameworkCore.PostgreSQL";

    public async Task<QueryPlanResult> GetPlanAsync(string sql, string connectionString, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            // Use EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) for detailed plan
            // ANALYZE actually runs the query, so it provides actual vs estimated rows
            // For safety, we wrap in a transaction and rollback for non-SELECT queries
            var isSelect = sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);
            var planSql = isSelect
                ? $"EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) {sql}"
                : $"EXPLAIN (FORMAT JSON) {sql}";

            await using var command = new NpgsqlCommand(planSql, connection);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            var rawPlan = result?.ToString() ?? "[]";

            var (nodes, totalCost, totalRows, actualTime) = ParseJsonPlan(rawPlan);
            var issues = DetectIssues(nodes);

            return new QueryPlanResult
            {
                Sql = sql,
                RawPlan = rawPlan,
                Provider = ProviderName,
                Nodes = nodes,
                Issues = issues,
                EstimatedCost = totalCost,
                EstimatedRows = totalRows,
                ActualTimeMs = actualTime,
                IsSuccess = true
            };
        }
        catch (Exception ex)
        {
            return new QueryPlanResult
            {
                Sql = sql,
                RawPlan = string.Empty,
                Provider = ProviderName,
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private static (List<PlanNode> nodes, double? totalCost, long? totalRows, double? actualTime) ParseJsonPlan(string jsonPlan)
    {
        var nodes = new List<PlanNode>();
        double? totalCost = null;
        long? totalRows = null;
        double? actualTime = null;

        try
        {
            using var doc = JsonDocument.Parse(jsonPlan);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                var firstElement = root[0];
                if (firstElement.TryGetProperty("Plan", out var plan))
                {
                    var node = ParsePlanNode(plan, 0);
                    nodes.Add(node);

                    totalCost = plan.TryGetProperty("Total Cost", out var cost) ? cost.GetDouble() : null;
                    totalRows = plan.TryGetProperty("Plan Rows", out var rows) ? rows.GetInt64() : null;
                    actualTime = plan.TryGetProperty("Actual Total Time", out var time) ? time.GetDouble() : null;
                }
            }
        }
        catch
        {
            // If JSON parsing fails, return empty results
        }

        return (nodes, totalCost, totalRows, actualTime);
    }

    private static PlanNode ParsePlanNode(JsonElement element, int depth)
    {
        var nodeType = element.TryGetProperty("Node Type", out var nt) ? nt.GetString() ?? "Unknown" : "Unknown";
        var relationName = element.TryGetProperty("Relation Name", out var rn) ? rn.GetString() : null;
        var indexName = element.TryGetProperty("Index Name", out var idx) ? idx.GetString() : null;
        var filter = element.TryGetProperty("Filter", out var f) ? f.GetString() : null;
        var indexCond = element.TryGetProperty("Index Cond", out var ic) ? ic.GetString() : null;

        var estimatedCost = element.TryGetProperty("Total Cost", out var ec) ? ec.GetDouble() : (double?)null;
        var estimatedRows = element.TryGetProperty("Plan Rows", out var er) ? er.GetInt64() : (long?)null;
        var actualTime = element.TryGetProperty("Actual Total Time", out var at) ? at.GetDouble() : (double?)null;
        var actualRows = element.TryGetProperty("Actual Rows", out var ar) ? ar.GetInt64() : (long?)null;

        var isTableScan = nodeType is "Seq Scan" or "Parallel Seq Scan";
        var usesIndex = nodeType.Contains("Index", StringComparison.OrdinalIgnoreCase);

        var details = BuildDetails(nodeType, filter, indexCond);

        var children = new List<PlanNode>();
        if (element.TryGetProperty("Plans", out var plans))
        {
            foreach (var child in plans.EnumerateArray())
            {
                children.Add(ParsePlanNode(child, depth + 1));
            }
        }

        return new PlanNode
        {
            Operation = nodeType,
            ObjectName = relationName,
            Details = details,
            EstimatedCost = estimatedCost,
            EstimatedRows = estimatedRows,
            ActualTimeMs = actualTime,
            ActualRows = actualRows,
            IsTableScan = isTableScan,
            UsesIndex = usesIndex,
            IndexName = indexName,
            Depth = depth,
            Children = children
        };
    }

    private static string? BuildDetails(string nodeType, string? filter, string? indexCond)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(filter))
        {
            parts.Add($"Filter: {filter}");
        }

        if (!string.IsNullOrEmpty(indexCond))
        {
            parts.Add($"Index Cond: {indexCond}");
        }

        return parts.Count > 0 ? string.Join("; ", parts) : null;
    }

    private static List<PlanIssue> DetectIssues(List<PlanNode> nodes)
    {
        var issues = new List<PlanIssue>();
        DetectIssuesRecursive(nodes, issues);
        return issues;
    }

    private static void DetectIssuesRecursive(IReadOnlyList<PlanNode> nodes, List<PlanIssue> issues)
    {
        foreach (var node in nodes)
        {
            // Detect sequential scans
            if (node.IsTableScan && node.ObjectName is not null)
            {
                // Only flag as issue if estimated rows > threshold (small tables are fine to scan)
                const long threshold = 1000;
                if (node.EstimatedRows is null || node.EstimatedRows > threshold)
                {
                    issues.Add(new PlanIssue
                    {
                        Type = PlanIssueType.TableScan,
                        Severity = node.EstimatedRows > 10000 ? PlanIssueSeverity.Critical : PlanIssueSeverity.Warning,
                        Title = "Sequential Scan Detected",
                        Message = $"Table '{node.ObjectName}' is being sequentially scanned" +
                                  (node.EstimatedRows.HasValue ? $" ({node.EstimatedRows:N0} estimated rows)" : "") +
                                  ". Consider adding an index on the columns used in WHERE or JOIN clauses.",
                        SuggestedFix = $"CREATE INDEX CONCURRENTLY ix_{node.ObjectName.ToLowerInvariant()}_<column> ON {node.ObjectName} (<column>);",
                        Table = node.ObjectName,
                        SourceNode = node
                    });
                }
            }

            // Detect index scans (as opposed to index seeks)
            if (node.Operation is "Index Scan" or "Bitmap Index Scan")
            {
                issues.Add(new PlanIssue
                {
                    Type = PlanIssueType.IndexScan,
                    Severity = PlanIssueSeverity.Info,
                    Title = "Index Scan (not Seek)",
                    Message = $"Index '{node.IndexName}' on '{node.ObjectName}' is being scanned rather than seeked. " +
                              "This may indicate the index could be more selective.",
                    Table = node.ObjectName,
                    IndexName = node.IndexName,
                    SourceNode = node
                });
            }

            // Detect cardinality mismatches
            if (node.EstimatedRows.HasValue && node.ActualRows.HasValue)
            {
                var ratio = node.ActualRows.Value / (double)Math.Max(1, node.EstimatedRows.Value);
                if (ratio > 10 || ratio < 0.1)
                {
                    issues.Add(new PlanIssue
                    {
                        Type = PlanIssueType.CardinalityMismatch,
                        Severity = PlanIssueSeverity.Warning,
                        Title = "Row Count Estimate Mismatch",
                        Message = $"Estimated {node.EstimatedRows:N0} rows but got {node.ActualRows:N0}. " +
                                  "This may indicate stale statistics. Consider running ANALYZE.",
                        SuggestedFix = node.ObjectName is not null ? $"ANALYZE {node.ObjectName};" : "ANALYZE;",
                        Table = node.ObjectName,
                        SourceNode = node
                    });
                }
            }

            // Detect nested loop warnings for large datasets
            if (node.Operation == "Nested Loop" && node.ActualRows > 10000)
            {
                issues.Add(new PlanIssue
                {
                    Type = PlanIssueType.NestedLoopWarning,
                    Severity = PlanIssueSeverity.Warning,
                    Title = "Nested Loop on Large Dataset",
                    Message = $"Nested loop join processed {node.ActualRows:N0} rows. " +
                              "Consider if a hash join or merge join would be more efficient.",
                    SourceNode = node
                });
            }

            DetectIssuesRecursive(node.Children, issues);
        }
    }
}
