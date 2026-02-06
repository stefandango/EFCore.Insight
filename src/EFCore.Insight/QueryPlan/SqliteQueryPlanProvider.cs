using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace EFCore.Insight.QueryPlan;

/// <summary>
/// Query plan provider for SQLite databases using EXPLAIN QUERY PLAN.
/// </summary>
public sealed partial class SqliteQueryPlanProvider : IQueryPlanProvider
{
    public string ProviderName => "Microsoft.EntityFrameworkCore.Sqlite";

    public async Task<QueryPlanResult> GetPlanAsync(string sql, string connectionString, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            var planSql = $"EXPLAIN QUERY PLAN {sql}";
            await using var command = new SqliteCommand(planSql, connection);

            var rawPlanLines = new List<string>();
            var nodes = new List<(int id, int parent, int notused, string detail)>();

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt32(0);
                var parent = reader.GetInt32(1);
                var notused = reader.GetInt32(2);
                var detail = reader.GetString(3);

                nodes.Add((id, parent, notused, detail));
                rawPlanLines.Add($"{id}|{parent}|{notused}|{detail}");
            }

            var rawPlan = string.Join("\n", rawPlanLines);
            var planNodes = BuildPlanTree(nodes);
            var issues = DetectIssues(planNodes);

            return new QueryPlanResult
            {
                Sql = sql,
                RawPlan = rawPlan,
                Provider = ProviderName,
                Nodes = planNodes,
                Issues = issues,
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

    private static List<PlanNode> BuildPlanTree(List<(int id, int parent, int notused, string detail)> rows)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        // First pass: create all nodes with mutable children lists
        var nodeData = new Dictionary<int, (PlanNode node, List<PlanNode> children, int parent, int depth)>();

        foreach (var (id, parent, _, detail) in rows)
        {
            var (operation, obj, details, isTableScan, usesIndex, indexName) = ParseDetail(detail);

            var node = new PlanNode
            {
                Operation = operation,
                ObjectName = obj,
                Details = details,
                IsTableScan = isTableScan,
                UsesIndex = usesIndex,
                IndexName = indexName,
                Depth = 0,
                Children = []
            };

            nodeData[id] = (node, [], parent, 0);
        }

        // Second pass: calculate depths and build parent-child relationships
        var rootIds = new List<int>();
        foreach (var (id, (node, _, parent, _)) in nodeData)
        {
            if (parent == 0 || !nodeData.TryGetValue(parent, out var parentData))
            {
                rootIds.Add(id);
            }
            else
            {
                parentData.children.Add(node);
            }
        }

        // Third pass: build final tree with correct depths and children
        List<PlanNode> BuildNodesWithDepth(List<int> ids, int depth)
        {
            var result = new List<PlanNode>();
            foreach (var id in ids)
            {
                var (node, children, _, _) = nodeData[id];
                var childIds = rows.Where(r => r.parent == id).Select(r => r.id).ToList();
                var builtChildren = BuildNodesWithDepth(childIds, depth + 1);

                result.Add(node with
                {
                    Depth = depth,
                    Children = builtChildren
                });
            }
            return result;
        }

        return BuildNodesWithDepth(rootIds, 0);
    }

    private static (string operation, string? obj, string? details, bool isTableScan, bool usesIndex, string? indexName) ParseDetail(string detail)
    {
        // SQLite EXPLAIN QUERY PLAN output examples:
        // "SCAN Users" or "SCAN TABLE orders"
        // "SEARCH Users USING INDEX idx_name (column=?)" or "SEARCH TABLE orders USING INDEX ..."
        // "SEARCH Users USING COVERING INDEX idx_name (column>?)"
        // "USE TEMP B-TREE FOR ORDER BY"
        // "CO-ROUTINE"
        // "COMPOUND SUBQUERIES"

        var scanMatch = ScanTableRegex().Match(detail);
        if (scanMatch.Success)
        {
            var tableName = scanMatch.Groups[1].Value;
            return ("SCAN", tableName, detail, true, false, null);
        }

        var searchMatch = SearchTableRegex().Match(detail);
        if (searchMatch.Success)
        {
            var table = searchMatch.Groups[1].Value;
            var indexName = searchMatch.Groups[3].Value;

            return ("SEARCH", table, detail, false, true, indexName);
        }

        var tempBTreeMatch = TempBTreeRegex().Match(detail);
        if (tempBTreeMatch.Success)
        {
            return ("TEMP B-TREE", null, detail, false, false, null);
        }

        // Default case
        return (detail, null, null, false, false, null);
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
            if (node.IsTableScan && node.ObjectName is not null)
            {
                issues.Add(new PlanIssue
                {
                    Type = PlanIssueType.TableScan,
                    Severity = PlanIssueSeverity.Warning,
                    Title = "Full Table Scan Detected",
                    Message = $"Table '{node.ObjectName}' is being scanned without using an index. " +
                              "Consider adding an index on the columns used in WHERE, JOIN, or ORDER BY clauses.",
                    SuggestedFix = $"CREATE INDEX IX_{node.ObjectName}_<column> ON {node.ObjectName} (<column>);",
                    Table = node.ObjectName,
                    SourceNode = node
                });
            }

            if (node.Operation == "TEMP B-TREE")
            {
                issues.Add(new PlanIssue
                {
                    Type = PlanIssueType.SortSpill,
                    Severity = PlanIssueSeverity.Warning,
                    Title = "Temporary B-Tree for Sorting",
                    Message = "A temporary B-tree is being used for sorting. " +
                              "Consider adding an index on the ORDER BY columns to avoid this.",
                    SourceNode = node
                });
            }

            DetectIssuesRecursive(node.Children, issues);
        }
    }

    // Match "SCAN Users" or "SCAN TABLE Users" - TABLE keyword is optional in some SQLite versions
    [GeneratedRegex(@"SCAN\s+(?:TABLE\s+)?(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex ScanTableRegex();

    // Match "SEARCH Users USING INDEX idx" or "SEARCH TABLE Users USING COVERING INDEX idx"
    [GeneratedRegex(@"SEARCH\s+(?:TABLE\s+)?(\w+)\s+USING\s+(COVERING\s+)?INDEX\s+(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex SearchTableRegex();

    [GeneratedRegex(@"USE TEMP B-TREE", RegexOptions.IgnoreCase)]
    private static partial Regex TempBTreeRegex();
}
