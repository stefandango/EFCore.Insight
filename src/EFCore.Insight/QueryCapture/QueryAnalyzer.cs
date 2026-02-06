using System.Text.RegularExpressions;

namespace EFCore.Insight.QueryCapture;

/// <summary>
/// Analyzes SQL queries and generates optimization suggestions.
/// </summary>
public static partial class QueryAnalyzer
{
    /// <summary>
    /// Analyzes a query and returns optimization suggestions.
    /// </summary>
    public static List<QuerySuggestion> Analyze(QueryEvent query)
    {
        var suggestions = new List<QuerySuggestion>();

        // Only analyze SELECT queries for now
        if (!query.Sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            return suggestions;
        }

        // Check for missing index opportunities
        var indexSuggestions = AnalyzeForMissingIndexes(query.Sql, query.Duration.TotalMilliseconds);
        suggestions.AddRange(indexSuggestions);

        // Check for missing pagination
        var paginationSuggestion = AnalyzeForMissingPagination(query.Sql, query.RowsAffected);
        if (paginationSuggestion is not null)
        {
            suggestions.Add(paginationSuggestion);
        }

        // Check for SELECT *
        var selectAllSuggestion = AnalyzeForSelectAll(query.Sql);
        if (selectAllSuggestion is not null)
        {
            suggestions.Add(selectAllSuggestion);
        }

        // Check for missing AsNoTracking (no FOR UPDATE, and is a simple SELECT)
        var noTrackingSuggestion = AnalyzeForNoTracking(query.Sql);
        if (noTrackingSuggestion is not null)
        {
            suggestions.Add(noTrackingSuggestion);
        }

        // Check for potential cartesian explosion
        var cartesianSuggestion = AnalyzeForCartesianExplosion(query.Sql, query.RowsAffected, query.Duration.TotalMilliseconds);
        if (cartesianSuggestion is not null)
        {
            suggestions.Add(cartesianSuggestion);
        }

        return suggestions;
    }

    private static List<QuerySuggestion> AnalyzeForMissingIndexes(string sql, double durationMs)
    {
        var suggestions = new List<QuerySuggestion>();

        // Only suggest indexes for slower queries (>50ms) to avoid noise
        if (durationMs < 50)
        {
            return suggestions;
        }

        // Extract table name from FROM clause
        var tables = ExtractTables(sql);

        // Extract columns from WHERE clause
        var whereColumns = ExtractWhereColumns(sql);

        // Extract columns from JOIN conditions
        var joinColumns = ExtractJoinColumns(sql);

        // Extract columns from ORDER BY
        var orderByColumns = ExtractOrderByColumns(sql);

        // Generate index suggestions for WHERE columns
        foreach (var (table, column) in whereColumns)
        {
            var tableName = ResolveTableName(table, tables);
            suggestions.Add(new QuerySuggestion
            {
                Type = SuggestionType.MissingIndex,
                Severity = durationMs > 500 ? SuggestionSeverity.High : SuggestionSeverity.Medium,
                Title = "Consider adding an index",
                Message = $"Column \"{column}\" is used in WHERE clause. An index could improve query performance.",
                SuggestedFix = $"CREATE INDEX IX_{tableName}_{column} ON {tableName} ({column});",
                Column = column,
                Table = tableName
            });
        }

        // Generate index suggestions for JOIN columns (if query is slow)
        if (durationMs > 100)
        {
            foreach (var (table, column) in joinColumns)
            {
                var tableName = ResolveTableName(table, tables);

                // Avoid duplicates
                if (suggestions.Any(s => s.Table == tableName && s.Column == column))
                    continue;

                suggestions.Add(new QuerySuggestion
                {
                    Type = SuggestionType.MissingIndex,
                    Severity = SuggestionSeverity.Medium,
                    Title = "Consider adding an index for JOIN",
                    Message = $"Column \"{column}\" is used in a JOIN condition. An index could improve join performance.",
                    SuggestedFix = $"CREATE INDEX IX_{tableName}_{column} ON {tableName} ({column});",
                    Column = column,
                    Table = tableName
                });
            }
        }

        // Generate index suggestions for ORDER BY (if query is slow and returns many rows)
        if (durationMs > 200 && orderByColumns.Count > 0)
        {
            var columns = string.Join(", ", orderByColumns.Select(c => c.Column));
            var tableName = orderByColumns.First().Table;
            tableName = ResolveTableName(tableName, tables);

            // Avoid duplicates
            if (!suggestions.Any(s => s.Table == tableName && orderByColumns.Any(c => c.Column == s.Column)))
            {
                suggestions.Add(new QuerySuggestion
                {
                    Type = SuggestionType.MissingIndex,
                    Severity = SuggestionSeverity.Low,
                    Title = "Consider adding an index for ORDER BY",
                    Message = $"Sorting by \"{columns}\" without an index may cause a full table scan.",
                    SuggestedFix = $"CREATE INDEX IX_{tableName}_{orderByColumns.First().Column} ON {tableName} ({columns});",
                    Column = orderByColumns.First().Column,
                    Table = tableName
                });
            }
        }

        // Deduplicate and limit suggestions
        return suggestions
            .GroupBy(s => $"{s.Table}.{s.Column}")
            .Select(g => g.First())
            .Take(3)
            .ToList();
    }

    private static QuerySuggestion? AnalyzeForMissingPagination(string sql, int? rowsAffected)
    {
        // Check if query has LIMIT/OFFSET/TOP/FETCH
        var hasPagination = LimitRegex().IsMatch(sql) ||
                           OffsetRegex().IsMatch(sql) ||
                           TopRegex().IsMatch(sql) ||
                           FetchRegex().IsMatch(sql);

        if (hasPagination)
        {
            return null;
        }

        // Only warn if we know it returned many rows, or it's a simple SELECT without WHERE
        var hasWhereClause = WhereRegex().IsMatch(sql);

        if (rowsAffected > 100 || (!hasWhereClause && rowsAffected is null))
        {
            return new QuerySuggestion
            {
                Type = SuggestionType.MissingPagination,
                Severity = rowsAffected > 1000 ? SuggestionSeverity.High : SuggestionSeverity.Low,
                Title = "Consider adding pagination",
                Message = rowsAffected.HasValue
                    ? $"Query returned {rowsAffected} rows without pagination. Consider using Skip/Take."
                    : "Query may return many rows without pagination. Consider using Skip/Take.",
                SuggestedFix = ".Skip(0).Take(100) // Add pagination"
            };
        }

        return null;
    }

    private static QuerySuggestion? AnalyzeForSelectAll(string sql)
    {
        // Check for SELECT * pattern
        if (!SelectAllRegex().IsMatch(sql))
        {
            return null;
        }

        return new QuerySuggestion
        {
            Type = SuggestionType.SelectAll,
            Severity = SuggestionSeverity.Low,
            Title = "Avoid SELECT *",
            Message = "Query selects all columns. Consider selecting only the columns you need using .Select().",
            SuggestedFix = ".Select(x => new { x.Id, x.Name, ... }) // Select specific columns"
        };
    }

    private static QuerySuggestion? AnalyzeForNoTracking(string sql)
    {
        // This is a heuristic - we can't know for sure if tracking is needed
        // Skip if query has subqueries or complex patterns
        if (sql.Contains("INSERT", StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("DELETE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Only suggest for simple SELECT queries
        if (!sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new QuerySuggestion
        {
            Type = SuggestionType.NoTracking,
            Severity = SuggestionSeverity.Info,
            Title = "Consider AsNoTracking()",
            Message = "If entities are read-only, AsNoTracking() can improve performance by skipping change tracking.",
            SuggestedFix = ".AsNoTracking() // Add before ToList/FirstOrDefault"
        };
    }

    private static QuerySuggestion? AnalyzeForCartesianExplosion(string sql, int? rowsAffected, double durationMs)
    {
        // Count ALL JOINs in the query (including those in subqueries)
        // EF Core often generates: LEFT JOIN (SELECT ... INNER JOIN ...) AS t
        var joinCount = AllJoinsRegex().Matches(sql).Count;

        // Need at least 2 JOINs for a potential cartesian explosion
        if (joinCount < 2)
        {
            return null;
        }

        // Count LEFT JOINs specifically (commonly used for Include)
        var leftJoinCount = LeftJoinRegex().Matches(sql).Count;

        // Heuristics for detecting potential cartesian explosion:
        // 1. Multiple LEFT JOINs (typical of Include() on collections)
        // 2. High row count suggests multiplication
        // 3. Slow query with multiple JOINs

        var isLikelyCartesian = false;
        var severity = SuggestionSeverity.Low;

        // Strong indicator: 3+ JOINs with known high row count
        if (joinCount >= 3 && rowsAffected > 100)
        {
            isLikelyCartesian = true;
            severity = rowsAffected > 1000 ? SuggestionSeverity.High : SuggestionSeverity.Medium;
        }
        // Multiple LEFT JOINs with known high row count
        else if (leftJoinCount >= 2 && rowsAffected > 50)
        {
            isLikelyCartesian = true;
            severity = SuggestionSeverity.Medium;
        }
        // 2+ LEFT JOINs is a strong indicator of Include() on collections
        // Even without row count, this pattern is worth flagging
        else if (leftJoinCount >= 2)
        {
            isLikelyCartesian = true;
            severity = SuggestionSeverity.Low;
        }
        // 2+ JOINs with at least 1 LEFT JOIN - typical EF Core Include pattern
        // EF Core generates: LEFT JOIN (SELECT ... INNER JOIN ...) for Include().ThenInclude()
        else if (joinCount >= 2 && leftJoinCount >= 1)
        {
            isLikelyCartesian = true;
            severity = SuggestionSeverity.Low;
        }
        // Slow query with multiple JOINs and known row count
        else if (joinCount >= 2 && durationMs > 200 && rowsAffected > 50)
        {
            isLikelyCartesian = true;
            severity = SuggestionSeverity.Medium;
        }
        // Multiple JOINs returning many more rows than expected
        else if (joinCount >= 2 && rowsAffected > 500)
        {
            isLikelyCartesian = true;
            severity = SuggestionSeverity.Medium;
        }
        // 3+ JOINs even without row count info is worth a warning
        else if (joinCount >= 3)
        {
            isLikelyCartesian = true;
            severity = SuggestionSeverity.Low;
        }

        if (!isLikelyCartesian)
        {
            return null;
        }

        var tables = ExtractTables(sql);
        var joinedTables = string.Join(", ", tables.Skip(1).Take(3));

        var message = rowsAffected.HasValue
            ? $"Query has {joinCount} JOINs ({joinedTables}) and returned {rowsAffected} rows. " +
              "Multiple collection includes can cause row multiplication. Consider using AsSplitQuery()."
            : $"Query has {joinCount} JOINs ({joinedTables}). " +
              "Multiple collection includes can cause row multiplication (cartesian product). Consider using AsSplitQuery().";

        return new QuerySuggestion
        {
            Type = SuggestionType.CartesianExplosion,
            Severity = severity,
            Title = "Potential Cartesian Explosion",
            Message = message,
            SuggestedFix = ".AsSplitQuery() // Split into multiple queries to avoid cartesian product"
        };
    }

    private static List<string> ExtractTables(string sql)
    {
        var tables = new List<string>();

        // Match FROM table
        var fromMatch = FromTableRegex().Match(sql);
        if (fromMatch.Success)
        {
            tables.Add(fromMatch.Groups[1].Value);
        }

        // Match JOIN tables
        var joinMatches = JoinTableRegex().Matches(sql);
        foreach (Match match in joinMatches)
        {
            tables.Add(match.Groups[1].Value);
        }

        return tables;
    }

    private static List<(string Table, string Column)> ExtractWhereColumns(string sql)
    {
        var columns = new List<(string, string)>();

        var matches = WhereColumnRegex().Matches(sql);
        foreach (Match match in matches)
        {
            var tableOrAlias = match.Groups[1].Value;
            var column = match.Groups[2].Value;
            columns.Add((tableOrAlias, column));
        }

        return columns;
    }

    private static List<(string Table, string Column)> ExtractJoinColumns(string sql)
    {
        var columns = new List<(string, string)>();

        var matches = JoinColumnRegex().Matches(sql);
        foreach (Match match in matches)
        {
            var table1 = match.Groups[1].Value;
            var column1 = match.Groups[2].Value;
            var table2 = match.Groups[3].Value;
            var column2 = match.Groups[4].Value;

            columns.Add((table1, column1));
            columns.Add((table2, column2));
        }

        return columns;
    }

    private static List<(string Table, string Column)> ExtractOrderByColumns(string sql)
    {
        var columns = new List<(string, string)>();

        var orderByMatch = OrderByRegex().Match(sql);
        if (!orderByMatch.Success) return columns;

        var orderByClause = orderByMatch.Groups[1].Value;
        var matches = OrderByColumnRegex().Matches(orderByClause);

        foreach (Match match in matches)
        {
            var table = match.Groups[1].Success ? match.Groups[1].Value : "";
            var column = match.Groups[2].Value;
            columns.Add((table, column));
        }

        return columns;
    }

    private static string ResolveTableName(string tableOrAlias, List<string> tables)
    {
        // If it's already a known table, return it
        if (tables.Contains(tableOrAlias, StringComparer.OrdinalIgnoreCase))
        {
            return tableOrAlias;
        }

        // Otherwise, try to find a matching table or return as-is
        // In real implementation, we'd track alias mappings
        return tables.FirstOrDefault() ?? tableOrAlias;
    }

    // Regex patterns
    [GeneratedRegex(@"\bFROM\s+[""'\[]?(\w+)[""'\]]?", RegexOptions.IgnoreCase)]
    private static partial Regex FromTableRegex();

    [GeneratedRegex(@"\bJOIN\s+[""'\[]?(\w+)[""'\]]?", RegexOptions.IgnoreCase)]
    private static partial Regex JoinTableRegex();

    [GeneratedRegex(@"\bLEFT\s+(OUTER\s+)?JOIN\b", RegexOptions.IgnoreCase)]
    private static partial Regex LeftJoinRegex();

    [GeneratedRegex(@"\bJOIN\b", RegexOptions.IgnoreCase)]
    private static partial Regex AllJoinsRegex();

    [GeneratedRegex(@"WHERE\b", RegexOptions.IgnoreCase)]
    private static partial Regex WhereRegex();

    [GeneratedRegex(@"(?:WHERE|AND|OR)\s+[""'\[]?(\w+)[""'\]]?\.[""'\[]?(\w+)[""'\]]?\s*(?:=|<|>|<=|>=|<>|!=|LIKE|IN)", RegexOptions.IgnoreCase)]
    private static partial Regex WhereColumnRegex();

    [GeneratedRegex(@"\bON\s+[""'\[]?(\w+)[""'\]]?\.[""'\[]?(\w+)[""'\]]?\s*=\s*[""'\[]?(\w+)[""'\]]?\.[""'\[]?(\w+)[""'\]]?", RegexOptions.IgnoreCase)]
    private static partial Regex JoinColumnRegex();

    [GeneratedRegex(@"\bORDER\s+BY\s+(.+?)(?:\bLIMIT\b|\bOFFSET\b|\bFETCH\b|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex OrderByRegex();

    [GeneratedRegex(@"(?:^|,)\s*(?:[""'\[]?(\w+)[""'\]]?\.)?[""'\[]?(\w+)[""'\]]?", RegexOptions.IgnoreCase)]
    private static partial Regex OrderByColumnRegex();

    [GeneratedRegex(@"\bLIMIT\s+\d+", RegexOptions.IgnoreCase)]
    private static partial Regex LimitRegex();

    [GeneratedRegex(@"\bOFFSET\s+\d+", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetRegex();

    [GeneratedRegex(@"\bTOP\s*\(\s*\d+\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex TopRegex();

    [GeneratedRegex(@"\bFETCH\s+(FIRST|NEXT)\s+\d+", RegexOptions.IgnoreCase)]
    private static partial Regex FetchRegex();

    [GeneratedRegex(@"\bSELECT\s+\*\s+FROM\b", RegexOptions.IgnoreCase)]
    private static partial Regex SelectAllRegex();
}

/// <summary>
/// A suggestion for optimizing a query.
/// </summary>
public sealed record QuerySuggestion
{
    public SuggestionType Type { get; init; }
    public SuggestionSeverity Severity { get; init; }
    public required string Title { get; init; }
    public required string Message { get; init; }
    public string? SuggestedFix { get; init; }
    public string? Table { get; init; }
    public string? Column { get; init; }
}

public enum SuggestionType
{
    MissingIndex,
    MissingPagination,
    SelectAll,
    NoTracking,
    CartesianExplosion
}

public enum SuggestionSeverity
{
    Info,
    Low,
    Medium,
    High
}
