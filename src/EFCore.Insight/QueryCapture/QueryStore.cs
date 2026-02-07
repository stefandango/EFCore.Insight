using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace EFCore.Insight.QueryCapture;

/// <summary>
/// Thread-safe in-memory store for captured query events using a ring buffer.
/// </summary>
public sealed partial class QueryStore
{
    private readonly ConcurrentQueue<QueryEvent> _queries = new();
    private readonly int _maxSize;
    private int _count;

    public QueryStore(int maxSize = 1000)
    {
        _maxSize = maxSize;
    }

    /// <summary>
    /// Adds a query event to the store. If the store is full, the oldest event is removed.
    /// </summary>
    public void Add(QueryEvent queryEvent)
    {
        _queries.Enqueue(queryEvent);

        var newCount = Interlocked.Increment(ref _count);

        // Remove oldest entries if we exceed max size
        while (newCount > _maxSize && _queries.TryDequeue(out _))
        {
            newCount = Interlocked.Decrement(ref _count);
        }
    }

    /// <summary>
    /// Gets all stored query events, newest first.
    /// </summary>
    public IReadOnlyList<QueryEvent> GetAll()
    {
        return _queries.Reverse().ToList();
    }

    /// <summary>
    /// Gets a specific query event by ID.
    /// </summary>
    public QueryEvent? GetById(Guid id)
    {
        return _queries.FirstOrDefault(q => q.Id == id);
    }

    /// <summary>
    /// Gets all query events for a specific request ID.
    /// </summary>
    public IReadOnlyList<QueryEvent> GetByRequestId(string requestId)
    {
        return _queries
            .Where(q => q.RequestId == requestId)
            .Reverse()
            .ToList();
    }

    /// <summary>
    /// Clears all stored query events.
    /// </summary>
    public void Clear()
    {
        while (_queries.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _count);
        }
    }

    /// <summary>
    /// Gets the current number of stored events.
    /// </summary>
    public int Count => _count;

    /// <summary>
    /// Gets summary statistics for stored queries.
    /// </summary>
    public QueryStats GetStats()
    {
        var queries = _queries.ToArray();

        if (queries.Length == 0)
        {
            return new QueryStats();
        }

        var durations = queries.Select(q => q.Duration.TotalMilliseconds).ToArray();
        var errorCount = queries.Count(q => q.IsError);
        var n1Patterns = DetectN1Patterns(queries);
        var splitQueryGroups = DetectSplitQueries(queries);

        var connections = queries
            .Where(q => q.ProviderName is not null)
            .GroupBy(q => (
                Provider: ConnectionStringHelper.GetFriendlyProviderName(q.ProviderName),
                Database: ConnectionStringHelper.SanitizeDatabaseId(q.ConnectionString, q.ProviderName)
            ))
            .Select(g => new ConnectionInfo
            {
                Provider = g.Key.Provider,
                Database = g.Key.Database,
                QueryCount = g.Count()
            })
            .ToList();

        return new QueryStats
        {
            TotalQueries = queries.Length,
            ErrorCount = errorCount,
            AverageDurationMs = durations.Average(),
            MinDurationMs = durations.Min(),
            MaxDurationMs = durations.Max(),
            TotalDurationMs = durations.Sum(),
            QueriesPerRequest = queries
                .Where(q => q.RequestId is not null)
                .GroupBy(q => q.RequestId)
                .ToDictionary(g => g.Key!, g => g.Count()),
            N1Patterns = n1Patterns,
            SplitQueryGroups = splitQueryGroups,
            Connections = connections
        };
    }

    /// <summary>
    /// Detects N+1 query patterns within the stored queries.
    /// An N+1 pattern occurs when the same SQL query (with different parameters)
    /// is executed multiple times within a single HTTP request.
    /// </summary>
    /// <param name="threshold">Minimum number of similar queries to consider it an N+1 pattern.</param>
    public IReadOnlyList<N1Pattern> DetectN1Patterns(int threshold = 3)
    {
        return DetectN1Patterns(_queries.ToArray(), threshold);
    }

    private static List<N1Pattern> DetectN1Patterns(QueryEvent[] queries, int threshold = 3)
    {
        return queries
            .Where(q => q.RequestId is not null)
            .GroupBy(q => (q.RequestId, NormalizedSql: NormalizeSql(q.Sql)))
            .Where(g => g.Count() >= threshold)
            .Select(g => new N1Pattern
            {
                NormalizedSql = g.Key.NormalizedSql,
                Count = g.Count(),
                RequestId = g.Key.RequestId!,
                RequestPath = g.First().RequestPath,
                QueryIds = g.Select(q => q.Id).ToList(),
                TotalDurationMs = g.Sum(q => q.Duration.TotalMilliseconds),
                AverageDurationMs = g.Average(q => q.Duration.TotalMilliseconds)
            })
            .OrderByDescending(p => p.Count)
            .ToList();
    }

    /// <summary>
    /// Detects split query groups within the stored queries.
    /// A split query group occurs when EF Core's AsSplitQuery() executes multiple
    /// different SELECT statements in rapid succession for the same logical query.
    /// </summary>
    /// <param name="maxGapMs">Maximum time gap between queries to be considered part of the same split group.</param>
    public IReadOnlyList<SplitQueryGroup> DetectSplitQueries(double maxGapMs = 50)
    {
        return DetectSplitQueries(_queries.ToArray(), maxGapMs);
    }

    private static List<SplitQueryGroup> DetectSplitQueries(QueryEvent[] queries, double maxGapMs = 50)
    {
        var results = new List<SplitQueryGroup>();

        // Group queries by request ID
        var byRequest = queries
            .Where(q => q.RequestId is not null)
            .GroupBy(q => q.RequestId!);

        foreach (var requestGroup in byRequest)
        {
            // Sort by timestamp
            var orderedQueries = requestGroup.OrderBy(q => q.Timestamp).ToList();
            if (orderedQueries.Count < 2) continue;

            // Find clusters of queries that execute in rapid succession with different SQL
            var currentGroup = new List<QueryEvent> { orderedQueries[0] };

            for (var i = 1; i < orderedQueries.Count; i++)
            {
                var current = orderedQueries[i];
                var previous = orderedQueries[i - 1];

                // Calculate gap (accounting for previous query's duration)
                var previousEnd = previous.Timestamp.Add(previous.Duration);
                var gap = (current.Timestamp - previousEnd).TotalMilliseconds;

                // Check if this query is within the time window
                if (gap <= maxGapMs)
                {
                    currentGroup.Add(current);
                }
                else
                {
                    // Gap too large - finalize current group if it qualifies as a split query
                    TryAddSplitGroup(currentGroup, results);
                    currentGroup = [current];
                }
            }

            // Don't forget the last group
            TryAddSplitGroup(currentGroup, results);
        }

        return results.OrderByDescending(g => g.QueryCount).ToList();
    }

    private static void TryAddSplitGroup(List<QueryEvent> group, List<SplitQueryGroup> results)
    {
        if (group.Count < 2) return;

        // Split queries have DIFFERENT SQL patterns (unlike N+1 which has same pattern)
        var distinctPatterns = group.Select(q => NormalizeSql(q.Sql)).Distinct().Count();
        if (distinctPatterns < 2) return;

        results.Add(new SplitQueryGroup
        {
            RequestId = group[0].RequestId!,
            RequestPath = group[0].RequestPath,
            QueryCount = group.Count,
            QueryIds = group.Select(q => q.Id).ToList(),
            TotalDurationMs = group.Sum(q => q.Duration.TotalMilliseconds),
            Tables = ExtractTables(group)
        });
    }

    private static List<string> ExtractTables(List<QueryEvent> queries)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var q in queries)
        {
            // Simple extraction of table names from FROM and JOIN clauses
            var matches = TableNameRegex().Matches(q.Sql);
            foreach (Match match in matches)
            {
                var table = match.Groups[1].Value;
                // Skip common keywords that might be captured
                if (!string.Equals(table, "SELECT", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(table, "WHERE", StringComparison.OrdinalIgnoreCase))
                {
                    tables.Add(table);
                }
            }
        }

        return tables.ToList();
    }

    /// <summary>
    /// Normalizes SQL by replacing parameter values with placeholders.
    /// This allows grouping queries that differ only in parameter values.
    /// </summary>
    private static string NormalizeSql(string sql)
    {
        // Replace named parameters (@p0, @p1, @param_name, etc.)
        var normalized = ParameterRegex().Replace(sql, "?");
        // Replace string literals
        normalized = StringLiteralRegex().Replace(normalized, "'?'");
        // Replace numeric literals (but not in identifiers)
        normalized = NumericLiteralRegex().Replace(normalized, "?");
        return normalized;
    }

    [GeneratedRegex(@"@\w+")]
    private static partial Regex ParameterRegex();

    [GeneratedRegex(@"'[^']*'")]
    private static partial Regex StringLiteralRegex();

    [GeneratedRegex(@"(?<![a-zA-Z_])\d+(?![a-zA-Z_])")]
    private static partial Regex NumericLiteralRegex();

    [GeneratedRegex(@"(?:FROM|JOIN)\s+[""'\[]?(\w+)[""'\]]?", RegexOptions.IgnoreCase)]
    private static partial Regex TableNameRegex();
}

/// <summary>
/// Summary statistics for captured queries.
/// </summary>
public sealed record QueryStats
{
    public int TotalQueries { get; init; }
    public int ErrorCount { get; init; }
    public double AverageDurationMs { get; init; }
    public double MinDurationMs { get; init; }
    public double MaxDurationMs { get; init; }
    public double TotalDurationMs { get; init; }
    public IReadOnlyDictionary<string, int> QueriesPerRequest { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<N1Pattern> N1Patterns { get; init; } = [];
    public IReadOnlyList<SplitQueryGroup> SplitQueryGroups { get; init; } = [];
    public IReadOnlyList<ConnectionInfo> Connections { get; init; } = [];
}

/// <summary>
/// Represents a detected N+1 query pattern.
/// </summary>
public sealed record N1Pattern
{
    /// <summary>
    /// The normalized SQL pattern (with parameters replaced by placeholders).
    /// </summary>
    public required string NormalizedSql { get; init; }

    /// <summary>
    /// Number of times this query pattern was executed.
    /// </summary>
    public int Count { get; init; }

    /// <summary>
    /// The HTTP request ID where this pattern was detected.
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// The HTTP request path where this pattern was detected.
    /// </summary>
    public string? RequestPath { get; init; }

    /// <summary>
    /// IDs of the individual queries that make up this pattern.
    /// </summary>
    public IReadOnlyList<Guid> QueryIds { get; init; } = [];

    /// <summary>
    /// Total duration of all queries in this pattern.
    /// </summary>
    public double TotalDurationMs { get; init; }

    /// <summary>
    /// Average duration per query in this pattern.
    /// </summary>
    public double AverageDurationMs { get; init; }
}

/// <summary>
/// Represents a detected split query group.
/// Split queries occur when EF Core's AsSplitQuery() executes multiple
/// separate SELECT statements instead of a single query with JOINs.
/// </summary>
public sealed record SplitQueryGroup
{
    /// <summary>
    /// The HTTP request ID where this split query was detected.
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// The HTTP request path where this split query was detected.
    /// </summary>
    public string? RequestPath { get; init; }

    /// <summary>
    /// Number of queries in this split group.
    /// </summary>
    public int QueryCount { get; init; }

    /// <summary>
    /// IDs of the individual queries in this split group.
    /// </summary>
    public IReadOnlyList<Guid> QueryIds { get; init; } = [];

    /// <summary>
    /// Total duration of all queries in this split group.
    /// </summary>
    public double TotalDurationMs { get; init; }

    /// <summary>
    /// Tables involved in the split query.
    /// </summary>
    public IReadOnlyList<string> Tables { get; init; } = [];
}

/// <summary>
/// Represents a database connection used by captured queries.
/// </summary>
public sealed record ConnectionInfo
{
    public required string Provider { get; init; }
    public string? Database { get; init; }
    public int QueryCount { get; init; }
}
