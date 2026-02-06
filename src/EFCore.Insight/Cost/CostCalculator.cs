using EFCore.Insight.QueryCapture;

namespace EFCore.Insight.Cost;

/// <summary>
/// Calculates estimated cost savings for fixing query performance issues.
/// </summary>
public sealed class CostCalculator
{
    /// <summary>
    /// Estimated savings percentages by issue type.
    /// Based on typical improvements from fixing these patterns.
    /// </summary>
    private static readonly Dictionary<string, double> SavingsEstimates = new()
    {
        ["N+1"] = 0.90,              // N+1 patterns typically save 90% by batching
        ["MissingIndex"] = 0.80,     // Missing indexes typically improve by 80%
        ["CartesianExplosion"] = 0.50, // AsSplitQuery typically saves 50%
        ["SelectAll"] = 0.20,        // Selecting specific columns saves ~20%
        ["TableScan"] = 0.75,        // Fixing table scans typically saves 75%
        ["KeyLookup"] = 0.40,        // Covering indexes save ~40%
        ["SortSpill"] = 0.30,        // Index on ORDER BY saves ~30%
        ["MissingPagination"] = 0.50 // Pagination reduces transfer by 50%+
    };

    /// <summary>
    /// Calculates the cost report for all captured queries.
    /// </summary>
    public static CostReport Calculate(QueryStore store)
    {
        var queries = store.GetAll();
        var stats = store.GetStats();

        // Calculate time window for executions per minute
        var timestamps = queries.Select(q => q.Timestamp).ToList();
        var timeWindowMinutes = timestamps.Count >= 2
            ? (timestamps.Max() - timestamps.Min()).TotalMinutes
            : 1;
        timeWindowMinutes = Math.Max(timeWindowMinutes, 1); // Avoid division by zero

        var recommendations = new List<CostRecommendation>();

        // Analyze N+1 patterns
        foreach (var pattern in stats.N1Patterns)
        {
            var executionsPerMinute = pattern.QueryIds.Count / timeWindowMinutes;
            var avgDurationMs = pattern.AverageDurationMs;
            var estimatedSavings = SavingsEstimates["N+1"];
            var timeSavedPerMin = executionsPerMinute * avgDurationMs * estimatedSavings;

            recommendations.Add(new CostRecommendation
            {
                IssueType = "N+1",
                PatternHash = queries.FirstOrDefault(q => pattern.QueryIds.Contains(q.Id))?.PatternHash ?? "",
                NormalizedSql = pattern.NormalizedSql,
                Description = $"N+1 query pattern detected with {pattern.Count} executions",
                ExecutionCount = pattern.Count,
                ExecutionsPerMinute = executionsPerMinute,
                AvgDurationMs = avgDurationMs,
                TotalDurationMs = pattern.TotalDurationMs,
                EstimatedSavingsPercent = estimatedSavings * 100,
                EstimatedTimeSavedPerMinMs = timeSavedPerMin,
                RequestPath = pattern.RequestPath,
                SuggestedFix = "Use .Include() to eager load related data, or batch queries using .AsSplitQuery()",
                Severity = timeSavedPerMin > 1000 ? RecommendationSeverity.High
                    : timeSavedPerMin > 100 ? RecommendationSeverity.Medium
                    : RecommendationSeverity.Low,
                AffectedQueryIds = pattern.QueryIds.ToList()
            });
        }

        // Analyze individual queries with issues
        var queriesWithSuggestions = queries
            .Select(q => (Query: q, Suggestions: QueryAnalyzer.Analyze(q)))
            .Where(x => x.Suggestions.Count > 0)
            .ToList();

        // Group by pattern hash to avoid duplicates
        var patternGroups = queriesWithSuggestions
            .GroupBy(x => x.Query.PatternHash)
            .ToList();

        foreach (var group in patternGroups)
        {
            var representativeQuery = group.First().Query;
            var suggestions = group.First().Suggestions;
            var groupQueries = group.Select(x => x.Query).ToList();
            var totalExecutions = groupQueries.Count;
            var executionsPerMinute = totalExecutions / timeWindowMinutes;
            var avgDuration = groupQueries.Average(q => q.Duration.TotalMilliseconds);
            var totalDuration = groupQueries.Sum(q => q.Duration.TotalMilliseconds);

            // Skip if already covered by N+1
            if (stats.N1Patterns.Any(p => p.QueryIds.Contains(representativeQuery.Id)))
            {
                continue;
            }

            foreach (var suggestion in suggestions)
            {
                var issueType = suggestion.Type.ToString();
                var estimatedSavings = SavingsEstimates.GetValueOrDefault(issueType, 0.20);
                var timeSavedPerMin = executionsPerMinute * avgDuration * estimatedSavings;

                // Skip NoTracking suggestions - they're low value
                if (suggestion.Type == SuggestionType.NoTracking)
                {
                    continue;
                }

                recommendations.Add(new CostRecommendation
                {
                    IssueType = issueType,
                    PatternHash = representativeQuery.PatternHash,
                    NormalizedSql = representativeQuery.NormalizedSql,
                    Description = suggestion.Message,
                    ExecutionCount = totalExecutions,
                    ExecutionsPerMinute = executionsPerMinute,
                    AvgDurationMs = avgDuration,
                    TotalDurationMs = totalDuration,
                    EstimatedSavingsPercent = estimatedSavings * 100,
                    EstimatedTimeSavedPerMinMs = timeSavedPerMin,
                    RequestPath = representativeQuery.RequestPath,
                    SuggestedFix = suggestion.SuggestedFix ?? "",
                    Severity = timeSavedPerMin > 1000 ? RecommendationSeverity.High
                        : timeSavedPerMin > 100 ? RecommendationSeverity.Medium
                        : RecommendationSeverity.Low,
                    AffectedQueryIds = groupQueries.Select(q => q.Id).ToList(),
                    Table = suggestion.Table,
                    Column = suggestion.Column
                });
            }
        }

        // Sort by impact (time saved per minute)
        recommendations = recommendations
            .OrderByDescending(r => r.EstimatedTimeSavedPerMinMs)
            .ToList();

        // Calculate totals
        var totalTimeSavedPerMin = recommendations.Sum(r => r.EstimatedTimeSavedPerMinMs);
        var totalTimeSpentPerMin = queries.Sum(q => q.Duration.TotalMilliseconds) / timeWindowMinutes;

        return new CostReport
        {
            GeneratedAt = DateTime.UtcNow,
            TimeWindowMinutes = timeWindowMinutes,
            TotalQueryCount = queries.Count,
            TotalQueryTimeMs = queries.Sum(q => q.Duration.TotalMilliseconds),
            TotalTimeSavedPerMinMs = totalTimeSavedPerMin,
            TotalTimeSpentPerMinMs = totalTimeSpentPerMin,
            PotentialSavingsPercent = totalTimeSpentPerMin > 0
                ? (totalTimeSavedPerMin / totalTimeSpentPerMin) * 100
                : 0,
            Recommendations = recommendations
        };
    }
}
