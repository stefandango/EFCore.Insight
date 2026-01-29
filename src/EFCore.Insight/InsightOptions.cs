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
    /// Gets the normalized route prefix (without leading/trailing slashes).
    /// </summary>
    internal string NormalizedRoutePrefix => RoutePrefix.Trim('/');
}
