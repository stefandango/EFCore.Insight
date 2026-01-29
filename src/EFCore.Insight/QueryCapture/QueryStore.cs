using System.Collections.Concurrent;

namespace EFCore.Insight.QueryCapture;

/// <summary>
/// Thread-safe in-memory store for captured query events using a ring buffer.
/// </summary>
public sealed class QueryStore
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
                .ToDictionary(g => g.Key!, g => g.Count())
        };
    }
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
}
