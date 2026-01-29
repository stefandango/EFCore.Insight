namespace EFCore.Insight.QueryCapture;

/// <summary>
/// Represents a captured EF Core query execution event.
/// </summary>
public sealed record QueryEvent
{
    /// <summary>
    /// Unique identifier for this query event.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Timestamp when the query was executed.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// The SQL command text that was executed.
    /// </summary>
    public required string Sql { get; init; }

    /// <summary>
    /// Parameters passed to the SQL command.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } = new Dictionary<string, object?>();

    /// <summary>
    /// Duration of the query execution.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Number of rows affected or returned by the query.
    /// </summary>
    public int? RowsAffected { get; init; }

    /// <summary>
    /// HTTP request path that triggered this query, if available.
    /// </summary>
    public string? RequestPath { get; init; }

    /// <summary>
    /// HTTP request ID for correlation, if available.
    /// </summary>
    public string? RequestId { get; init; }

    /// <summary>
    /// The type of command (Query, NonQuery, Scalar).
    /// </summary>
    public string? CommandType { get; init; }

    /// <summary>
    /// Indicates if this was a failed query.
    /// </summary>
    public bool IsError { get; init; }

    /// <summary>
    /// Error message if the query failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
}
