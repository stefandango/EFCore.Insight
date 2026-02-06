using System.Collections.Concurrent;

namespace EFCore.Insight.History;

/// <summary>
/// In-memory implementation of query history storage.
/// Useful for testing or when persistence is not needed.
/// </summary>
public sealed class InMemoryQueryHistoryStore : IQueryHistoryStore
{
    private readonly ConcurrentDictionary<string, PatternData> _patterns = new();
    private const int MaxSamplesPerPattern = 100;

    public Task RecordExecutionAsync(string patternHash, string normalizedSql, double durationMs, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        _patterns.AddOrUpdate(
            patternHash,
            _ => new PatternData
            {
                PatternHash = patternHash,
                NormalizedSql = normalizedSql,
                ExecutionCount = 1,
                TotalDurationMs = durationMs,
                MinDurationMs = durationMs,
                MaxDurationMs = durationMs,
                FirstSeen = now,
                LastSeen = now,
                Samples = [(now, durationMs)]
            },
            (_, existing) =>
            {
                var samples = existing.Samples.ToList();
                samples.Add((now, durationMs));

                if (samples.Count > MaxSamplesPerPattern)
                {
                    samples = samples.Skip(samples.Count - MaxSamplesPerPattern).ToList();
                }

                return existing with
                {
                    ExecutionCount = existing.ExecutionCount + 1,
                    TotalDurationMs = existing.TotalDurationMs + durationMs,
                    MinDurationMs = Math.Min(existing.MinDurationMs, durationMs),
                    MaxDurationMs = Math.Max(existing.MaxDurationMs, durationMs),
                    LastSeen = now,
                    Samples = samples
                };
            });

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<QueryPattern>> GetPatternsAsync(CancellationToken cancellationToken = default)
    {
        var patterns = _patterns.Values
            .Select(ToQueryPattern)
            .OrderByDescending(p => p.TotalDurationMs)
            .ToList();

        return Task.FromResult<IReadOnlyList<QueryPattern>>(patterns);
    }

    public Task<QueryPattern?> GetPatternAsync(string patternHash, CancellationToken cancellationToken = default)
    {
        if (_patterns.TryGetValue(patternHash, out var data))
        {
            return Task.FromResult<QueryPattern?>(ToQueryPattern(data));
        }

        return Task.FromResult<QueryPattern?>(null);
    }

    public Task SetBaselineAsync(string patternHash, CancellationToken cancellationToken = default)
    {
        if (_patterns.TryGetValue(patternHash, out var existing))
        {
            var avgDuration = existing.ExecutionCount > 0 ? existing.TotalDurationMs / existing.ExecutionCount : 0;

            _patterns[patternHash] = existing with
            {
                BaselineAvgDurationMs = avgDuration,
                BaselineSetAt = DateTime.UtcNow
            };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<QueryRegression>> GetRegressionsAsync(double thresholdPercent = 20, CancellationToken cancellationToken = default)
    {
        var regressions = new List<QueryRegression>();

        foreach (var data in _patterns.Values)
        {
            if (!data.BaselineAvgDurationMs.HasValue || data.BaselineAvgDurationMs.Value <= 0)
            {
                continue;
            }

            var currentAvg = data.ExecutionCount > 0 ? data.TotalDurationMs / data.ExecutionCount : 0;
            var baselineAvg = data.BaselineAvgDurationMs.Value;
            var percentChange = ((currentAvg - baselineAvg) / baselineAvg) * 100;

            if (percentChange >= thresholdPercent)
            {
                var severity = percentChange switch
                {
                    >= 100 => RegressionSeverity.Severe,
                    >= 50 => RegressionSeverity.Moderate,
                    _ => RegressionSeverity.Minor
                };

                regressions.Add(new QueryRegression
                {
                    Pattern = ToQueryPattern(data),
                    PercentChange = percentChange,
                    AbsoluteChangeMs = currentAvg - baselineAvg,
                    Severity = severity
                });
            }
        }

        return Task.FromResult<IReadOnlyList<QueryRegression>>(
            regressions.OrderByDescending(r => r.PercentChange).ToList());
    }

    public Task CleanupAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        var keysToRemove = _patterns
            .Where(kvp => kvp.Value.LastSeen < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            _patterns.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    private static QueryPattern ToQueryPattern(PatternData data)
    {
        var avgDuration = data.ExecutionCount > 0 ? data.TotalDurationMs / data.ExecutionCount : 0;

        var sortedDurations = data.Samples.Select(s => s.DurationMs).OrderBy(d => d).ToList();
        var p95Index = (int)(sortedDurations.Count * 0.95);
        var p95 = sortedDurations.Count > 0 ? sortedDurations[Math.Min(p95Index, sortedDurations.Count - 1)] : 0;

        return new QueryPattern
        {
            PatternHash = data.PatternHash,
            NormalizedSql = data.NormalizedSql,
            ExecutionCount = data.ExecutionCount,
            AvgDurationMs = avgDuration,
            MinDurationMs = data.MinDurationMs,
            MaxDurationMs = data.MaxDurationMs,
            P95DurationMs = p95,
            TotalDurationMs = data.TotalDurationMs,
            FirstSeen = data.FirstSeen,
            LastSeen = data.LastSeen,
            BaselineAvgDurationMs = data.BaselineAvgDurationMs,
            BaselineSetAt = data.BaselineSetAt,
            RecentSamples = data.Samples.TakeLast(20).Select(s => new DurationSample
            {
                Timestamp = s.Timestamp,
                DurationMs = s.DurationMs
            }).ToList()
        };
    }

    private sealed record PatternData
    {
        public required string PatternHash { get; init; }
        public required string NormalizedSql { get; init; }
        public int ExecutionCount { get; init; }
        public double TotalDurationMs { get; init; }
        public double MinDurationMs { get; init; }
        public double MaxDurationMs { get; init; }
        public DateTime FirstSeen { get; init; }
        public DateTime LastSeen { get; init; }
        public double? BaselineAvgDurationMs { get; init; }
        public DateTime? BaselineSetAt { get; init; }
        public List<(DateTime Timestamp, double DurationMs)> Samples { get; init; } = [];
    }
}
