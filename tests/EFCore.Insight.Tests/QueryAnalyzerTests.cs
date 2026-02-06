using EFCore.Insight.QueryCapture;
using Xunit;

namespace EFCore.Insight.Tests;

public class QueryAnalyzerTests
{
    [Fact]
    public void Analyze_SlowQueryWithWhereClause_SuggestsMissingIndex()
    {
        var query = new QueryEvent
        {
            Sql = "SELECT * FROM Orders WHERE Orders.CustomerId = @p0",
            Duration = TimeSpan.FromMilliseconds(100)
        };

        var suggestions = QueryAnalyzer.Analyze(query);

        Assert.Contains(suggestions, s =>
            s.Type == SuggestionType.MissingIndex &&
            s.Column == "CustomerId");
    }

    [Fact]
    public void Analyze_FastQuery_NoIndexSuggestion()
    {
        var query = new QueryEvent
        {
            Sql = "SELECT * FROM Orders WHERE Orders.CustomerId = @p0",
            Duration = TimeSpan.FromMilliseconds(10)
        };

        var suggestions = QueryAnalyzer.Analyze(query);

        Assert.DoesNotContain(suggestions, s => s.Type == SuggestionType.MissingIndex);
    }

    [Fact]
    public void Analyze_SelectAll_SuggestsAvoidSelectAll()
    {
        var query = new QueryEvent
        {
            Sql = "SELECT * FROM Products",
            Duration = TimeSpan.FromMilliseconds(10)
        };

        var suggestions = QueryAnalyzer.Analyze(query);

        Assert.Contains(suggestions, s => s.Type == SuggestionType.SelectAll);
    }

    [Fact]
    public void Analyze_SelectSpecificColumns_NoSelectAllSuggestion()
    {
        var query = new QueryEvent
        {
            Sql = "SELECT Id, Name FROM Products",
            Duration = TimeSpan.FromMilliseconds(10)
        };

        var suggestions = QueryAnalyzer.Analyze(query);

        Assert.DoesNotContain(suggestions, s => s.Type == SuggestionType.SelectAll);
    }

    [Fact]
    public void Analyze_QueryWithManyRows_SuggestsPagination()
    {
        var query = new QueryEvent
        {
            Sql = "SELECT Id, Name FROM Products",
            Duration = TimeSpan.FromMilliseconds(10),
            RowsAffected = 500
        };

        var suggestions = QueryAnalyzer.Analyze(query);

        Assert.Contains(suggestions, s => s.Type == SuggestionType.MissingPagination);
    }

    [Fact]
    public void Analyze_QueryWithLimit_NoPaginationSuggestion()
    {
        var query = new QueryEvent
        {
            Sql = "SELECT Id, Name FROM Products LIMIT 10",
            Duration = TimeSpan.FromMilliseconds(10),
            RowsAffected = 10
        };

        var suggestions = QueryAnalyzer.Analyze(query);

        Assert.DoesNotContain(suggestions, s => s.Type == SuggestionType.MissingPagination);
    }

    [Fact]
    public void Analyze_ReadOnlyQuery_SuggestsNoTracking()
    {
        var query = new QueryEvent
        {
            Sql = "SELECT Id, Name FROM Products",
            Duration = TimeSpan.FromMilliseconds(10)
        };

        var suggestions = QueryAnalyzer.Analyze(query);

        Assert.Contains(suggestions, s => s.Type == SuggestionType.NoTracking);
    }

    [Fact]
    public void Analyze_UpdateQuery_NoSuggestions()
    {
        var query = new QueryEvent
        {
            Sql = "UPDATE Products SET Name = @p0 WHERE Id = @p1",
            Duration = TimeSpan.FromMilliseconds(10)
        };

        var suggestions = QueryAnalyzer.Analyze(query);

        Assert.Empty(suggestions);
    }

    [Fact]
    public void Analyze_JoinQuery_SuggestsIndexOnJoinColumn()
    {
        var query = new QueryEvent
        {
            Sql = "SELECT o.Id FROM Orders o JOIN OrderItems oi ON o.Id = oi.OrderId",
            Duration = TimeSpan.FromMilliseconds(150)
        };

        var suggestions = QueryAnalyzer.Analyze(query);

        Assert.Contains(suggestions, s =>
            s.Type == SuggestionType.MissingIndex &&
            (s.Column == "Id" || s.Column == "OrderId"));
    }

    [Fact]
    public void Analyze_IndexSuggestion_IncludesCreateStatement()
    {
        var query = new QueryEvent
        {
            Sql = "SELECT * FROM Orders WHERE Orders.CustomerId = @p0",
            Duration = TimeSpan.FromMilliseconds(100)
        };

        var suggestions = QueryAnalyzer.Analyze(query);
        var indexSuggestion = suggestions.FirstOrDefault(s => s.Type == SuggestionType.MissingIndex);

        Assert.NotNull(indexSuggestion);
        Assert.Contains("CREATE INDEX", indexSuggestion.SuggestedFix);
        Assert.Contains("CustomerId", indexSuggestion.SuggestedFix);
    }

    [Fact]
    public void Analyze_MultipleJoinsWithManyRows_SuggestsCartesianExplosion()
    {
        var query = new QueryEvent
        {
            Sql = "SELECT o.Id, oi.Id, p.Id FROM Orders o " +
                  "LEFT JOIN OrderItems oi ON o.Id = oi.OrderId " +
                  "LEFT JOIN Payments p ON o.Id = p.OrderId " +
                  "WHERE o.CustomerId = @p0",
            Duration = TimeSpan.FromMilliseconds(50),
            RowsAffected = 500
        };

        var suggestions = QueryAnalyzer.Analyze(query);

        Assert.Contains(suggestions, s => s.Type == SuggestionType.CartesianExplosion);
    }

    [Fact]
    public void Analyze_SingleJoin_NoCartesianExplosionSuggestion()
    {
        var query = new QueryEvent
        {
            Sql = "SELECT o.Id, oi.Id FROM Orders o " +
                  "LEFT JOIN OrderItems oi ON o.Id = oi.OrderId",
            Duration = TimeSpan.FromMilliseconds(50),
            RowsAffected = 100
        };

        var suggestions = QueryAnalyzer.Analyze(query);

        Assert.DoesNotContain(suggestions, s => s.Type == SuggestionType.CartesianExplosion);
    }

    [Fact]
    public void Analyze_MultipleLeftJoins_SuggestsCartesianEvenWithoutRowCount()
    {
        // 2+ LEFT JOINs is a common Include pattern that can cause cartesian explosion
        var query = new QueryEvent
        {
            Sql = "SELECT o.Id, oi.Id, p.Id FROM Orders o " +
                  "LEFT JOIN OrderItems oi ON o.Id = oi.OrderId " +
                  "LEFT JOIN Payments p ON o.Id = p.OrderId",
            Duration = TimeSpan.FromMilliseconds(10),
            RowsAffected = null // Row count often unavailable for SELECT queries
        };

        var suggestions = QueryAnalyzer.Analyze(query);

        Assert.Contains(suggestions, s => s.Type == SuggestionType.CartesianExplosion);
    }

    [Fact]
    public void Analyze_ThreeJoinsWithHighRowCount_HighSeverity()
    {
        var query = new QueryEvent
        {
            Sql = "SELECT o.Id FROM Orders o " +
                  "JOIN OrderItems oi ON o.Id = oi.OrderId " +
                  "JOIN Payments p ON o.Id = p.OrderId " +
                  "JOIN Shipments s ON o.Id = s.OrderId",
            Duration = TimeSpan.FromMilliseconds(300),
            RowsAffected = 5000
        };

        var suggestions = QueryAnalyzer.Analyze(query);
        var cartesianSuggestion = suggestions.FirstOrDefault(s => s.Type == SuggestionType.CartesianExplosion);

        Assert.NotNull(cartesianSuggestion);
        Assert.Equal(SuggestionSeverity.High, cartesianSuggestion.Severity);
        Assert.Contains("AsSplitQuery()", cartesianSuggestion.SuggestedFix);
    }

    [Fact]
    public void Analyze_CartesianExplosion_IncludesJoinedTableNames()
    {
        var query = new QueryEvent
        {
            Sql = "SELECT o.Id FROM Orders o " +
                  "LEFT JOIN OrderItems oi ON o.Id = oi.OrderId " +
                  "LEFT JOIN Payments p ON o.Id = p.OrderId",
            Duration = TimeSpan.FromMilliseconds(50),
            RowsAffected = 200
        };

        var suggestions = QueryAnalyzer.Analyze(query);
        var cartesianSuggestion = suggestions.FirstOrDefault(s => s.Type == SuggestionType.CartesianExplosion);

        Assert.NotNull(cartesianSuggestion);
        Assert.Contains("OrderItems", cartesianSuggestion.Message);
        Assert.Contains("Payments", cartesianSuggestion.Message);
    }

    [Fact]
    public void Analyze_EFCoreIncludePattern_SuggestsCartesian()
    {
        // EF Core generates this pattern for Include().ThenInclude()
        // LEFT JOIN (SELECT ... INNER JOIN ...) AS t
        var query = new QueryEvent
        {
            Sql = """
                SELECT "o"."Id", "o"."CustomerName", "t"."Id"
                FROM "Orders" AS "o"
                LEFT JOIN (
                    SELECT "o0"."Id", "o0"."OrderId"
                    FROM "OrderItems" AS "o0"
                    INNER JOIN "Products" AS "p" ON "o0"."ProductId" = "p"."Id"
                ) AS "t" ON "o"."Id" = "t"."OrderId"
                """,
            Duration = TimeSpan.FromMilliseconds(10),
            RowsAffected = null
        };

        var suggestions = QueryAnalyzer.Analyze(query);

        Assert.Contains(suggestions, s => s.Type == SuggestionType.CartesianExplosion);
    }
}
