namespace EFCore.Insight.QueryPlan;

/// <summary>
/// Represents the result of a query plan analysis.
/// </summary>
public sealed record QueryPlanResult
{
    /// <summary>
    /// The original SQL query that was analyzed.
    /// </summary>
    public required string Sql { get; init; }

    /// <summary>
    /// The raw plan output from the database.
    /// </summary>
    public required string RawPlan { get; init; }

    /// <summary>
    /// The database provider that generated this plan.
    /// </summary>
    public required string Provider { get; init; }

    /// <summary>
    /// The root nodes of the parsed execution plan tree.
    /// </summary>
    public IReadOnlyList<PlanNode> Nodes { get; init; } = [];

    /// <summary>
    /// Issues detected in the execution plan.
    /// </summary>
    public IReadOnlyList<PlanIssue> Issues { get; init; } = [];

    /// <summary>
    /// Estimated total cost (if available from the provider).
    /// </summary>
    public double? EstimatedCost { get; init; }

    /// <summary>
    /// Estimated row count (if available from the provider).
    /// </summary>
    public long? EstimatedRows { get; init; }

    /// <summary>
    /// Actual execution time in milliseconds (if ANALYZE was used).
    /// </summary>
    public double? ActualTimeMs { get; init; }

    /// <summary>
    /// Indicates if the plan analysis was successful.
    /// </summary>
    public bool IsSuccess { get; init; } = true;

    /// <summary>
    /// Error message if the plan analysis failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Represents a node in the execution plan tree.
/// </summary>
public sealed record PlanNode
{
    /// <summary>
    /// The operation type (e.g., "SCAN", "SEARCH", "NESTED LOOP", "HASH JOIN").
    /// </summary>
    public required string Operation { get; init; }

    /// <summary>
    /// The object being operated on (table name, index name, etc.).
    /// </summary>
    public string? ObjectName { get; init; }

    /// <summary>
    /// Additional details about the operation.
    /// </summary>
    public string? Details { get; init; }

    /// <summary>
    /// Estimated cost for this node.
    /// </summary>
    public double? EstimatedCost { get; init; }

    /// <summary>
    /// Estimated row count for this node.
    /// </summary>
    public long? EstimatedRows { get; init; }

    /// <summary>
    /// Actual execution time in milliseconds (if available).
    /// </summary>
    public double? ActualTimeMs { get; init; }

    /// <summary>
    /// Actual row count (if available).
    /// </summary>
    public long? ActualRows { get; init; }

    /// <summary>
    /// Child nodes in the execution plan tree.
    /// </summary>
    public IReadOnlyList<PlanNode> Children { get; init; } = [];

    /// <summary>
    /// Indicates if this node represents a table scan (full scan without index).
    /// </summary>
    public bool IsTableScan { get; init; }

    /// <summary>
    /// Indicates if this node uses an index.
    /// </summary>
    public bool UsesIndex { get; init; }

    /// <summary>
    /// The index name if an index is used.
    /// </summary>
    public string? IndexName { get; init; }

    /// <summary>
    /// Depth level in the plan tree (for formatting).
    /// </summary>
    public int Depth { get; init; }
}

/// <summary>
/// Represents an issue detected in the execution plan.
/// </summary>
public sealed record PlanIssue
{
    /// <summary>
    /// The type of issue detected.
    /// </summary>
    public required PlanIssueType Type { get; init; }

    /// <summary>
    /// Severity of the issue.
    /// </summary>
    public required PlanIssueSeverity Severity { get; init; }

    /// <summary>
    /// Human-readable title for the issue.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Detailed description of the issue.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Suggested fix for the issue.
    /// </summary>
    public string? SuggestedFix { get; init; }

    /// <summary>
    /// The table involved in this issue.
    /// </summary>
    public string? Table { get; init; }

    /// <summary>
    /// The column involved in this issue.
    /// </summary>
    public string? Column { get; init; }

    /// <summary>
    /// The index involved in this issue.
    /// </summary>
    public string? IndexName { get; init; }

    /// <summary>
    /// The plan node that caused this issue.
    /// </summary>
    public PlanNode? SourceNode { get; init; }
}

/// <summary>
/// Types of issues that can be detected in an execution plan.
/// </summary>
public enum PlanIssueType
{
    /// <summary>
    /// A full table scan without using an index.
    /// </summary>
    TableScan,

    /// <summary>
    /// A potentially missing index that could improve performance.
    /// </summary>
    MissingIndex,

    /// <summary>
    /// An implicit type conversion that may prevent index usage.
    /// </summary>
    ImplicitConversion,

    /// <summary>
    /// A sort operation that spills to disk due to memory pressure.
    /// </summary>
    SortSpill,

    /// <summary>
    /// A hash operation that spills to disk.
    /// </summary>
    HashSpill,

    /// <summary>
    /// A key lookup (bookmark lookup) that may indicate a missing covering index.
    /// </summary>
    KeyLookup,

    /// <summary>
    /// A nested loop join that may be inefficient for large datasets.
    /// </summary>
    NestedLoopWarning,

    /// <summary>
    /// An index scan instead of an index seek.
    /// </summary>
    IndexScan,

    /// <summary>
    /// Estimated vs actual row count mismatch indicating stale statistics.
    /// </summary>
    CardinalityMismatch
}

/// <summary>
/// Severity levels for plan issues.
/// </summary>
public enum PlanIssueSeverity
{
    Info,
    Warning,
    Critical
}
