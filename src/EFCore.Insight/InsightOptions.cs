namespace EFCore.Insight;

/// <summary>
/// Configuration options for EFCore.Insight.
/// </summary>
public sealed class InsightOptions
{
    /// <summary>
    /// The URL prefix for the insight dashboard and API endpoints.
    /// Default: "_ef-insight"
    /// </summary>
    public string RoutePrefix { get; set; } = "_ef-insight";

    /// <summary>
    /// Maximum number of query events to store in memory.
    /// Older events are automatically removed when this limit is exceeded.
    /// Default: 1000
    /// </summary>
    public int MaxStoredQueries { get; set; } = 1000;

    /// <summary>
    /// Enable correlation between HTTP requests and EF Core queries.
    /// When enabled, each query will include the request path and ID.
    /// Default: true
    /// </summary>
    public bool EnableRequestCorrelation { get; set; } = true;

    /// <summary>
    /// Enable query plan analysis (EXPLAIN) for captured queries.
    /// When enabled, you can analyze execution plans for individual queries.
    /// Default: true
    /// </summary>
    public bool EnableQueryPlanAnalysis { get; set; } = true;

    /// <summary>
    /// Minimum query duration in milliseconds to automatically trigger plan analysis.
    /// Only queries exceeding this threshold will have their plans captured automatically.
    /// Set to 0 to disable automatic plan capture (manual analysis only).
    /// Default: 100
    /// </summary>
    public int QueryPlanAnalysisThresholdMs { get; set; } = 100;

    /// <summary>
    /// Enable query history tracking for before/after comparison.
    /// When enabled, query patterns are persisted and can be compared over time.
    /// Default: false
    /// </summary>
    public bool EnableQueryHistory { get; set; }

    /// <summary>
    /// Number of days to retain query history data.
    /// Default: 7
    /// </summary>
    public int HistoryRetentionDays { get; set; } = 7;

    /// <summary>
    /// Path to store history data. If not specified, uses ".ef-insight" in the application's content root.
    /// </summary>
    public string? HistoryStoragePath { get; set; }

    /// <summary>
    /// Enable endpoint-level query analysis.
    /// When enabled, queries are grouped and analyzed by HTTP endpoint.
    /// Default: true
    /// </summary>
    public bool EnableEndpointAnalysis { get; set; } = true;

    /// <summary>
    /// Gets the normalized route prefix (without leading/trailing slashes).
    /// </summary>
    internal string NormalizedRoutePrefix => RoutePrefix.Trim('/');
}
