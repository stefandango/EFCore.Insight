using System.Collections.Concurrent;
using System.Text.Json;

namespace EFCore.Insight.History;

/// <summary>
/// File-based implementation of query history storage.
/// Stores history data as JSON in the .ef-insight directory.
/// </summary>
public sealed class FileQueryHistoryStore : IQueryHistoryStore, IDisposable
{
    private readonly string _historyFilePath;
    private readonly ConcurrentDictionary<string, PatternData> _patterns = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly Timer _saveTimer;
    private bool _isDirty;
    private const int MaxSamplesPerPattern = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FileQueryHistoryStore(string? storagePath = null)
    {
        var basePath = storagePath ?? Path.Combine(Directory.GetCurrentDirectory(), ".ef-insight");
        Directory.CreateDirectory(basePath);
        _historyFilePath = Path.Combine(basePath, "history.json");

        LoadFromFile();

        // Save periodically (every 30 seconds if dirty)
        _saveTimer = new Timer(_ => SaveIfDirty(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

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
                Samples = [new SampleData { Timestamp = now, DurationMs = durationMs }]
            },
            (_, existing) =>
            {
                var samples = existing.Samples.ToList();
                samples.Add(new SampleData { Timestamp = now, DurationMs = durationMs });

                // Keep only recent samples
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

        _isDirty = true;
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

            _isDirty = true;
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

        if (keysToRemove.Count > 0)
        {
            _isDirty = true;
        }

        return Task.CompletedTask;
    }

    private QueryPattern ToQueryPattern(PatternData data)
    {
        var avgDuration = data.ExecutionCount > 0 ? data.TotalDurationMs / data.ExecutionCount : 0;

        // Calculate P95
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

    private void LoadFromFile()
    {
        if (!File.Exists(_historyFilePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_historyFilePath);
            var data = JsonSerializer.Deserialize<HistoryFileData>(json, JsonOptions);

            if (data?.Patterns is not null)
            {
                foreach (var pattern in data.Patterns)
                {
                    _patterns[pattern.PatternHash] = pattern;
                }
            }
        }
        catch
        {
            // Ignore load errors - start fresh
        }
    }

    private void SaveIfDirty()
    {
        if (!_isDirty)
        {
            return;
        }

        SaveToFileAsync().GetAwaiter().GetResult();
    }

    private async Task SaveToFileAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            var data = new HistoryFileData
            {
                Version = 1,
                LastUpdated = DateTime.UtcNow,
                Patterns = _patterns.Values.ToList()
            };

            var json = JsonSerializer.Serialize(data, JsonOptions);
            await File.WriteAllTextAsync(_historyFilePath, json);
            _isDirty = false;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        SaveIfDirty();
        _saveLock.Dispose();
    }

    // Internal data models for serialization
    private sealed record HistoryFileData
    {
        public int Version { get; init; }
        public DateTime LastUpdated { get; init; }
        public List<PatternData> Patterns { get; init; } = [];
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
        public List<SampleData> Samples { get; init; } = [];
    }

    private sealed record SampleData
    {
        public DateTime Timestamp { get; init; }
        public double DurationMs { get; init; }
    }
}
