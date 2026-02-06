using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EFCore.Insight.QueryCapture;

/// <summary>
/// Represents a captured EF Core query execution event.
/// </summary>
public sealed partial record QueryEvent
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
    /// HTTP method (GET, POST, etc.) that triggered this query, if available.
    /// </summary>
    public string? HttpMethod { get; init; }

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

    /// <summary>
    /// The primary call site that triggered this query (e.g., "OrderService.cs:42").
    /// </summary>
    public string? CallSite { get; init; }

    /// <summary>
    /// The method name that triggered this query.
    /// </summary>
    public string? CallingMethod { get; init; }

    /// <summary>
    /// Abbreviated stack trace showing the call path (user code only).
    /// </summary>
    public IReadOnlyList<string> StackTrace { get; init; } = [];

    /// <summary>
    /// The database provider name (e.g., "Microsoft.EntityFrameworkCore.Sqlite").
    /// </summary>
    public string? ProviderName { get; init; }

    /// <summary>
    /// The connection string used for this query (for plan analysis).
    /// Note: This is not serialized to the API to avoid exposing credentials.
    /// </summary>
    internal string? ConnectionString { get; init; }

    /// <summary>
    /// Hash of the normalized SQL pattern for grouping similar queries.
    /// </summary>
    public string PatternHash => _patternHash ??= ComputePatternHash();
    private string? _patternHash;

    /// <summary>
    /// The normalized SQL pattern (with parameters replaced by placeholders).
    /// </summary>
    public string NormalizedSql => _normalizedSql ??= NormalizeSql(Sql);
    private string? _normalizedSql;

    private string ComputePatternHash()
    {
        var normalized = NormalizedSql;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string NormalizeSql(string sql)
    {
        // Replace named parameters (@p0, @p1, @param_name, etc.)
        var normalized = ParameterRegex().Replace(sql, "?");
        // Replace string literals
        normalized = StringLiteralRegex().Replace(normalized, "'?'");
        // Replace numeric literals (but not in identifiers)
        normalized = NumericLiteralRegex().Replace(normalized, "?");
        // Normalize whitespace
        normalized = WhitespaceRegex().Replace(normalized, " ").Trim();
        return normalized;
    }

    [GeneratedRegex(@"@\w+")]
    private static partial Regex ParameterRegex();

    [GeneratedRegex(@"'[^']*'")]
    private static partial Regex StringLiteralRegex();

    [GeneratedRegex(@"(?<![a-zA-Z_])\d+(?![a-zA-Z_])")]
    private static partial Regex NumericLiteralRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
