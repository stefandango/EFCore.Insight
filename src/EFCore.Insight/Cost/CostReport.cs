namespace EFCore.Insight.Cost;

/// <summary>
/// A report of estimated cost savings from fixing query performance issues.
/// </summary>
public sealed record CostReport
{
    /// <summary>
    /// When this report was generated.
    /// </summary>
    public DateTime GeneratedAt { get; init; }

    /// <summary>
    /// The time window (in minutes) used for calculating executions per minute.
    /// </summary>
    public double TimeWindowMinutes { get; init; }

    /// <summary>
    /// Total number of queries analyzed.
    /// </summary>
    public int TotalQueryCount { get; init; }

    /// <summary>
    /// Total time spent on all queries (in milliseconds).
    /// </summary>
    public double TotalQueryTimeMs { get; init; }

    /// <summary>
    /// Total estimated time that could be saved per minute by fixing all issues.
    /// </summary>
    public double TotalTimeSavedPerMinMs { get; init; }

    /// <summary>
    /// Total time spent on queries per minute.
    /// </summary>
    public double TotalTimeSpentPerMinMs { get; init; }

    /// <summary>
    /// Potential percentage savings from fixing all issues.
    /// </summary>
    public double PotentialSavingsPercent { get; init; }

    /// <summary>
    /// Prioritized list of recommendations, sorted by impact.
    /// </summary>
    public IReadOnlyList<CostRecommendation> Recommendations { get; init; } = [];
}

/// <summary>
/// A single recommendation for improving query performance.
/// </summary>
public sealed record CostRecommendation
{
    /// <summary>
    /// The type of issue (e.g., "N+1", "MissingIndex", "CartesianExplosion").
    /// </summary>
    public required string IssueType { get; init; }

    /// <summary>
    /// Hash of the query pattern.
    /// </summary>
    public required string PatternHash { get; init; }

    /// <summary>
    /// The normalized SQL pattern.
    /// </summary>
    public required string NormalizedSql { get; init; }

    /// <summary>
    /// Human-readable description of the issue.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Number of times this pattern was executed.
    /// </summary>
    public int ExecutionCount { get; init; }

    /// <summary>
    /// Executions per minute.
    /// </summary>
    public double ExecutionsPerMinute { get; init; }

    /// <summary>
    /// Average execution duration in milliseconds.
    /// </summary>
    public double AvgDurationMs { get; init; }

    /// <summary>
    /// Total execution duration across all executions.
    /// </summary>
    public double TotalDurationMs { get; init; }

    /// <summary>
    /// Estimated percentage savings from fixing this issue.
    /// </summary>
    public double EstimatedSavingsPercent { get; init; }

    /// <summary>
    /// Estimated time saved per minute in milliseconds.
    /// </summary>
    public double EstimatedTimeSavedPerMinMs { get; init; }

    /// <summary>
    /// The request path where this issue was detected (if available).
    /// </summary>
    public string? RequestPath { get; init; }

    /// <summary>
    /// Suggested fix for this issue.
    /// </summary>
    public string SuggestedFix { get; init; } = "";

    /// <summary>
    /// Severity of the recommendation.
    /// </summary>
    public RecommendationSeverity Severity { get; init; }

    /// <summary>
    /// IDs of queries affected by this issue.
    /// </summary>
    public IReadOnlyList<Guid> AffectedQueryIds { get; init; } = [];

    /// <summary>
    /// The table involved (if applicable).
    /// </summary>
    public string? Table { get; init; }

    /// <summary>
    /// The column involved (if applicable).
    /// </summary>
    public string? Column { get; init; }
}

/// <summary>
/// Severity levels for cost recommendations.
/// </summary>
public enum RecommendationSeverity
{
    /// <summary>
    /// Low priority - saves less than 100ms/min.
    /// </summary>
    Low,

    /// <summary>
    /// Medium priority - saves 100ms-1s/min.
    /// </summary>
    Medium,

    /// <summary>
    /// High priority - saves more than 1s/min.
    /// </summary>
    High
}
