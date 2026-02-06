namespace EFCore.Insight.History;

/// <summary>
/// Represents an aggregated query pattern with historical statistics.
/// </summary>
public sealed record QueryPattern
{
    /// <summary>
    /// Hash of the normalized SQL pattern.
    /// </summary>
    public required string PatternHash { get; init; }

    /// <summary>
    /// The normalized SQL (with parameters replaced by placeholders).
    /// </summary>
    public required string NormalizedSql { get; init; }

    /// <summary>
    /// Total number of times this pattern has been executed.
    /// </summary>
    public int ExecutionCount { get; init; }

    /// <summary>
    /// Average execution duration in milliseconds.
    /// </summary>
    public double AvgDurationMs { get; init; }

    /// <summary>
    /// Minimum execution duration in milliseconds.
    /// </summary>
    public double MinDurationMs { get; init; }

    /// <summary>
    /// Maximum execution duration in milliseconds.
    /// </summary>
    public double MaxDurationMs { get; init; }

    /// <summary>
    /// 95th percentile execution duration in milliseconds.
    /// </summary>
    public double P95DurationMs { get; init; }

    /// <summary>
    /// Total execution duration across all executions.
    /// </summary>
    public double TotalDurationMs { get; init; }

    /// <summary>
    /// When this pattern was first seen.
    /// </summary>
    public DateTime FirstSeen { get; init; }

    /// <summary>
    /// When this pattern was last executed.
    /// </summary>
    public DateTime LastSeen { get; init; }

    /// <summary>
    /// Baseline average duration (when baseline was set).
    /// </summary>
    public double? BaselineAvgDurationMs { get; init; }

    /// <summary>
    /// When the baseline was set.
    /// </summary>
    public DateTime? BaselineSetAt { get; init; }

    /// <summary>
    /// Recent execution durations (for trend analysis).
    /// </summary>
    public IReadOnlyList<DurationSample> RecentSamples { get; init; } = [];
}

/// <summary>
/// A timestamped duration sample for trend analysis.
/// </summary>
public sealed record DurationSample
{
    /// <summary>
    /// When the sample was recorded.
    /// </summary>
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// The duration in milliseconds.
    /// </summary>
    public double DurationMs { get; init; }
}

/// <summary>
/// Represents a performance regression for a query pattern.
/// </summary>
public sealed record QueryRegression
{
    /// <summary>
    /// The pattern that regressed.
    /// </summary>
    public required QueryPattern Pattern { get; init; }

    /// <summary>
    /// Percentage change from baseline (positive = slower).
    /// </summary>
    public double PercentChange { get; init; }

    /// <summary>
    /// Absolute change in milliseconds.
    /// </summary>
    public double AbsoluteChangeMs { get; init; }

    /// <summary>
    /// Severity of the regression.
    /// </summary>
    public RegressionSeverity Severity { get; init; }
}

/// <summary>
/// Severity levels for performance regressions.
/// </summary>
public enum RegressionSeverity
{
    /// <summary>
    /// Minor regression (20-50% slower).
    /// </summary>
    Minor,

    /// <summary>
    /// Moderate regression (50-100% slower).
    /// </summary>
    Moderate,

    /// <summary>
    /// Severe regression (>100% slower).
    /// </summary>
    Severe
}
