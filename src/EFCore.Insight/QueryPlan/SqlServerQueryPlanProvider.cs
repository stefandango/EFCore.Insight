using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace EFCore.Insight.QueryPlan;

/// <summary>
/// Query plan provider for SQL Server databases using SET SHOWPLAN_XML.
/// </summary>
public sealed partial class SqlServerQueryPlanProvider : IQueryPlanProvider
{
    public string ProviderName => "Microsoft.EntityFrameworkCore.SqlServer";

    public async Task<QueryPlanResult> GetPlanAsync(string sql, string connectionString, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            // Enable XML plan output
            await using (var enableCmd = new SqlCommand("SET SHOWPLAN_XML ON", connection))
            {
                await enableCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            string rawPlan;
            try
            {
                await using var command = new SqlCommand(sql, connection);
                var result = await command.ExecuteScalarAsync(cancellationToken);
                rawPlan = result?.ToString() ?? string.Empty;
            }
            finally
            {
                // Always disable SHOWPLAN_XML
                await using var disableCmd = new SqlCommand("SET SHOWPLAN_XML OFF", connection);
                await disableCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var (nodes, totalCost, totalRows) = ParseXmlPlan(rawPlan);
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

    private static (List<PlanNode> nodes, double? totalCost, long? totalRows) ParseXmlPlan(string xmlPlan)
    {
        var nodes = new List<PlanNode>();
        double? totalCost = null;
        long? totalRows = null;

        if (string.IsNullOrEmpty(xmlPlan))
        {
            return (nodes, totalCost, totalRows);
        }

        try
        {
            var doc = XDocument.Parse(xmlPlan);
            XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

            var stmtSimple = doc.Descendants(ns + "StmtSimple").FirstOrDefault();
            if (stmtSimple is not null)
            {
                totalCost = ParseDouble(stmtSimple.Attribute("StatementSubTreeCost")?.Value);
                totalRows = ParseLong(stmtSimple.Attribute("StatementEstRows")?.Value);
            }

            var relOps = doc.Descendants(ns + "RelOp");
            foreach (var relOp in relOps)
            {
                var node = ParseRelOp(relOp, ns, 0);
                if (node is not null)
                {
                    nodes.Add(node);
                    break; // Only take the root RelOp
                }
            }
        }
        catch
        {
            // If XML parsing fails, return empty results
        }

        return (nodes, totalCost, totalRows);
    }

    private static PlanNode? ParseRelOp(XElement relOp, XNamespace ns, int depth)
    {
        var physicalOp = relOp.Attribute("PhysicalOp")?.Value ?? "Unknown";
        var logicalOp = relOp.Attribute("LogicalOp")?.Value;
        var estimatedRows = ParseLong(relOp.Attribute("EstimateRows")?.Value);
        var estimatedCost = ParseDouble(relOp.Attribute("EstimatedTotalSubtreeCost")?.Value);

        string? tableName = null;
        string? indexName = null;
        var isTableScan = false;
        var usesIndex = false;

        // Parse specific operation types
        var scanElement = relOp.Descendants(ns + "TableScan").FirstOrDefault();
        if (scanElement is not null)
        {
            isTableScan = true;
            var objElement = scanElement.Element(ns + "Object");
            tableName = objElement?.Attribute("Table")?.Value?.Trim('[', ']');
        }

        var indexScan = relOp.Descendants(ns + "IndexScan").FirstOrDefault();
        if (indexScan is not null)
        {
            usesIndex = true;
            var objElement = indexScan.Element(ns + "Object");
            tableName = objElement?.Attribute("Table")?.Value?.Trim('[', ']');
            indexName = objElement?.Attribute("Index")?.Value?.Trim('[', ']');

            // Check if it's a seek vs scan
            var isSeek = indexScan.Attribute("ScanType")?.Value == "Seek";
            if (!isSeek)
            {
                isTableScan = true; // Index scan is similar to table scan
            }
        }

        var indexSeek = relOp.Descendants(ns + "IndexSeek").FirstOrDefault();
        if (indexSeek is not null)
        {
            usesIndex = true;
            var objElement = indexSeek.Element(ns + "Object");
            tableName = objElement?.Attribute("Table")?.Value?.Trim('[', ']');
            indexName = objElement?.Attribute("Index")?.Value?.Trim('[', ']');
        }

        var nestedLoops = relOp.Descendants(ns + "NestedLoops").FirstOrDefault();
        var hashMatch = relOp.Descendants(ns + "Hash").FirstOrDefault();
        var sort = relOp.Descendants(ns + "Sort").FirstOrDefault();

        var details = BuildDetails(logicalOp, tableName, indexName);

        // Parse child RelOps
        var children = new List<PlanNode>();
        var childRelOps = relOp.Elements(ns + "RelOp");
        foreach (var childRelOp in childRelOps)
        {
            var childNode = ParseRelOp(childRelOp, ns, depth + 1);
            if (childNode is not null)
            {
                children.Add(childNode);
            }
        }

        return new PlanNode
        {
            Operation = physicalOp,
            ObjectName = tableName,
            Details = details,
            EstimatedCost = estimatedCost,
            EstimatedRows = estimatedRows,
            IsTableScan = isTableScan,
            UsesIndex = usesIndex,
            IndexName = indexName,
            Depth = depth,
            Children = children
        };
    }

    private static string? BuildDetails(string? logicalOp, string? tableName, string? indexName)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(logicalOp))
        {
            parts.Add($"Logical: {logicalOp}");
        }

        if (!string.IsNullOrEmpty(indexName))
        {
            parts.Add($"Index: {indexName}");
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
            // Detect table scans
            if (node.Operation == "Table Scan" && node.ObjectName is not null)
            {
                issues.Add(new PlanIssue
                {
                    Type = PlanIssueType.TableScan,
                    Severity = node.EstimatedRows > 10000 ? PlanIssueSeverity.Critical : PlanIssueSeverity.Warning,
                    Title = "Table Scan Detected",
                    Message = $"Table '{node.ObjectName}' is being fully scanned" +
                              (node.EstimatedRows.HasValue ? $" ({node.EstimatedRows:N0} estimated rows)" : "") +
                              ". Consider adding an index on the columns used in WHERE or JOIN clauses.",
                    SuggestedFix = $"CREATE NONCLUSTERED INDEX IX_{node.ObjectName}_<column> ON {node.ObjectName} (<column>);",
                    Table = node.ObjectName,
                    SourceNode = node
                });
            }

            // Detect clustered index scans (often indicates missing indexes)
            if (node.Operation == "Clustered Index Scan" && node.ObjectName is not null)
            {
                issues.Add(new PlanIssue
                {
                    Type = PlanIssueType.IndexScan,
                    Severity = node.EstimatedRows > 10000 ? PlanIssueSeverity.Warning : PlanIssueSeverity.Info,
                    Title = "Clustered Index Scan",
                    Message = $"Clustered index on '{node.ObjectName}' is being scanned. " +
                              "Consider adding a nonclustered index for better selectivity.",
                    Table = node.ObjectName,
                    IndexName = node.IndexName,
                    SourceNode = node
                });
            }

            // Detect key lookups (indicates missing covering index)
            if (node.Operation == "Key Lookup" && node.ObjectName is not null)
            {
                issues.Add(new PlanIssue
                {
                    Type = PlanIssueType.KeyLookup,
                    Severity = PlanIssueSeverity.Warning,
                    Title = "Key Lookup Detected",
                    Message = $"A key lookup is being performed on '{node.ObjectName}'. " +
                              "Consider creating a covering index that includes all required columns.",
                    SuggestedFix = $"-- Add INCLUDE clause to existing index\nCREATE NONCLUSTERED INDEX IX_{node.ObjectName}_covering ON {node.ObjectName} (<key_columns>) INCLUDE (<select_columns>);",
                    Table = node.ObjectName,
                    SourceNode = node
                });
            }

            // Detect sort operations (may indicate missing index for ORDER BY)
            if (node.Operation == "Sort")
            {
                issues.Add(new PlanIssue
                {
                    Type = PlanIssueType.SortSpill,
                    Severity = PlanIssueSeverity.Info,
                    Title = "Sort Operation",
                    Message = "A sort operation is being performed. " +
                              "Consider adding an index on ORDER BY columns to eliminate the sort.",
                    SourceNode = node
                });
            }

            // Detect hash matches on large datasets
            if (node.Operation == "Hash Match" && node.EstimatedRows > 100000)
            {
                issues.Add(new PlanIssue
                {
                    Type = PlanIssueType.HashSpill,
                    Severity = PlanIssueSeverity.Warning,
                    Title = "Large Hash Match",
                    Message = $"Hash match operation on {node.EstimatedRows:N0} estimated rows. " +
                              "This may spill to disk if memory is insufficient.",
                    SourceNode = node
                });
            }

            DetectIssuesRecursive(node.Children, issues);
        }
    }

    private static double? ParseDouble(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return double.TryParse(value, out var result) ? result : null;
    }

    private static long? ParseLong(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (double.TryParse(value, out var doubleResult))
        {
            return (long)doubleResult;
        }
        return long.TryParse(value, out var result) ? result : null;
    }
}
