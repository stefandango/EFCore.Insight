namespace EFCore.Insight.History;

/// <summary>
/// Abstraction for persisting query pattern history.
/// </summary>
public interface IQueryHistoryStore
{
    /// <summary>
    /// Records a query execution for historical tracking.
    /// </summary>
    Task RecordExecutionAsync(string patternHash, string normalizedSql, double durationMs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all tracked query patterns.
    /// </summary>
    Task<IReadOnlyList<QueryPattern>> GetPatternsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific pattern by its hash.
    /// </summary>
    Task<QueryPattern?> GetPatternAsync(string patternHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the baseline for a pattern (for before/after comparison).
    /// </summary>
    Task SetBaselineAsync(string patternHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets patterns that have regressed from their baseline.
    /// </summary>
    Task<IReadOnlyList<QueryRegression>> GetRegressionsAsync(double thresholdPercent = 20, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleans up old history data based on retention policy.
    /// </summary>
    Task CleanupAsync(int retentionDays, CancellationToken cancellationToken = default);
}
