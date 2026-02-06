using EFCore.Insight.QueryCapture;
using Xunit;

namespace EFCore.Insight.Tests;

public class QueryStoreTests
{
    [Fact]
    public void Add_StoresQueryEvent()
    {
        var store = new QueryStore();
        var query = new QueryEvent { Sql = "SELECT 1" };

        store.Add(query);

        Assert.Equal(1, store.Count);
        Assert.Contains(store.GetAll(), q => q.Sql == "SELECT 1");
    }

    [Fact]
    public void Add_RespectsMasSize()
    {
        var store = new QueryStore(maxSize: 3);

        store.Add(new QueryEvent { Sql = "SELECT 1" });
        store.Add(new QueryEvent { Sql = "SELECT 2" });
        store.Add(new QueryEvent { Sql = "SELECT 3" });
        store.Add(new QueryEvent { Sql = "SELECT 4" });

        Assert.Equal(3, store.Count);
        Assert.DoesNotContain(store.GetAll(), q => q.Sql == "SELECT 1");
        Assert.Contains(store.GetAll(), q => q.Sql == "SELECT 4");
    }

    [Fact]
    public void GetAll_ReturnsNewestFirst()
    {
        var store = new QueryStore();

        store.Add(new QueryEvent { Sql = "SELECT 1" });
        store.Add(new QueryEvent { Sql = "SELECT 2" });
        store.Add(new QueryEvent { Sql = "SELECT 3" });

        var all = store.GetAll();

        Assert.Equal("SELECT 3", all[0].Sql);
        Assert.Equal("SELECT 2", all[1].Sql);
        Assert.Equal("SELECT 1", all[2].Sql);
    }

    [Fact]
    public void GetById_ReturnsCorrectQuery()
    {
        var store = new QueryStore();
        var query = new QueryEvent { Sql = "SELECT 1" };

        store.Add(query);

        var result = store.GetById(query.Id);

        Assert.NotNull(result);
        Assert.Equal(query.Id, result.Id);
    }

    [Fact]
    public void GetById_ReturnsNullForUnknownId()
    {
        var store = new QueryStore();
        store.Add(new QueryEvent { Sql = "SELECT 1" });

        var result = store.GetById(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public void GetByRequestId_FiltersCorrectly()
    {
        var store = new QueryStore();

        store.Add(new QueryEvent { Sql = "SELECT 1", RequestId = "req-1" });
        store.Add(new QueryEvent { Sql = "SELECT 2", RequestId = "req-2" });
        store.Add(new QueryEvent { Sql = "SELECT 3", RequestId = "req-1" });

        var result = store.GetByRequestId("req-1");

        Assert.Equal(2, result.Count);
        Assert.All(result, q => Assert.Equal("req-1", q.RequestId));
    }

    [Fact]
    public void Clear_RemovesAllQueries()
    {
        var store = new QueryStore();
        store.Add(new QueryEvent { Sql = "SELECT 1" });
        store.Add(new QueryEvent { Sql = "SELECT 2" });

        store.Clear();

        Assert.Equal(0, store.Count);
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void GetStats_ReturnsCorrectStatistics()
    {
        var store = new QueryStore();

        store.Add(new QueryEvent { Sql = "SELECT 1", Duration = TimeSpan.FromMilliseconds(10) });
        store.Add(new QueryEvent { Sql = "SELECT 2", Duration = TimeSpan.FromMilliseconds(20) });
        store.Add(new QueryEvent { Sql = "SELECT 3", Duration = TimeSpan.FromMilliseconds(30), IsError = true });

        var stats = store.GetStats();

        Assert.Equal(3, stats.TotalQueries);
        Assert.Equal(1, stats.ErrorCount);
        Assert.Equal(20, stats.AverageDurationMs);
        Assert.Equal(10, stats.MinDurationMs);
        Assert.Equal(30, stats.MaxDurationMs);
        Assert.Equal(60, stats.TotalDurationMs);
    }

    [Fact]
    public void GetStats_EmptyStore_ReturnsDefaults()
    {
        var store = new QueryStore();

        var stats = store.GetStats();

        Assert.Equal(0, stats.TotalQueries);
        Assert.Equal(0, stats.ErrorCount);
    }

    [Fact]
    public void DetectN1Patterns_DetectsRepeatedQueriesInSameRequest()
    {
        var store = new QueryStore();
        var requestId = "req-1";

        // Simulate N+1: one query to get orders, then repeated queries for each order's product
        store.Add(new QueryEvent { Sql = "SELECT * FROM Orders", RequestId = requestId });
        store.Add(new QueryEvent { Sql = "SELECT * FROM Products WHERE Id = @p0", RequestId = requestId });
        store.Add(new QueryEvent { Sql = "SELECT * FROM Products WHERE Id = @p0", RequestId = requestId });
        store.Add(new QueryEvent { Sql = "SELECT * FROM Products WHERE Id = @p0", RequestId = requestId });

        var patterns = store.DetectN1Patterns(threshold: 3);

        Assert.Single(patterns);
        Assert.Equal(3, patterns[0].Count);
        Assert.Equal(requestId, patterns[0].RequestId);
        Assert.Contains("Products", patterns[0].NormalizedSql);
    }

    [Fact]
    public void DetectN1Patterns_IgnoresQueriesBelowThreshold()
    {
        var store = new QueryStore();
        var requestId = "req-1";

        store.Add(new QueryEvent { Sql = "SELECT * FROM Products WHERE Id = @p0", RequestId = requestId });
        store.Add(new QueryEvent { Sql = "SELECT * FROM Products WHERE Id = @p0", RequestId = requestId });

        var patterns = store.DetectN1Patterns(threshold: 3);

        Assert.Empty(patterns);
    }

    [Fact]
    public void DetectN1Patterns_DoesNotGroupAcrossDifferentRequests()
    {
        var store = new QueryStore();

        store.Add(new QueryEvent { Sql = "SELECT * FROM Products WHERE Id = @p0", RequestId = "req-1" });
        store.Add(new QueryEvent { Sql = "SELECT * FROM Products WHERE Id = @p0", RequestId = "req-1" });
        store.Add(new QueryEvent { Sql = "SELECT * FROM Products WHERE Id = @p0", RequestId = "req-2" });
        store.Add(new QueryEvent { Sql = "SELECT * FROM Products WHERE Id = @p0", RequestId = "req-2" });

        var patterns = store.DetectN1Patterns(threshold: 3);

        Assert.Empty(patterns);
    }

    [Fact]
    public void DetectN1Patterns_NormalizesDifferentParameterValues()
    {
        var store = new QueryStore();
        var requestId = "req-1";

        store.Add(new QueryEvent { Sql = "SELECT * FROM Products WHERE Id = @p0", RequestId = requestId });
        store.Add(new QueryEvent { Sql = "SELECT * FROM Products WHERE Id = @p1", RequestId = requestId });
        store.Add(new QueryEvent { Sql = "SELECT * FROM Products WHERE Id = @param", RequestId = requestId });

        var patterns = store.DetectN1Patterns(threshold: 3);

        Assert.Single(patterns);
        Assert.Equal(3, patterns[0].Count);
    }

    [Fact]
    public void DetectN1Patterns_IncludesTotalAndAverageDuration()
    {
        var store = new QueryStore();
        var requestId = "req-1";

        store.Add(new QueryEvent { Sql = "SELECT * FROM Products WHERE Id = @p0", RequestId = requestId, Duration = TimeSpan.FromMilliseconds(10) });
        store.Add(new QueryEvent { Sql = "SELECT * FROM Products WHERE Id = @p0", RequestId = requestId, Duration = TimeSpan.FromMilliseconds(20) });
        store.Add(new QueryEvent { Sql = "SELECT * FROM Products WHERE Id = @p0", RequestId = requestId, Duration = TimeSpan.FromMilliseconds(30) });

        var patterns = store.DetectN1Patterns(threshold: 3);

        Assert.Single(patterns);
        Assert.Equal(60, patterns[0].TotalDurationMs);
        Assert.Equal(20, patterns[0].AverageDurationMs);
    }

    [Fact]
    public void GetStats_IncludesN1Patterns()
    {
        var store = new QueryStore();
        var requestId = "req-1";

        store.Add(new QueryEvent { Sql = "SELECT * FROM Products WHERE Id = @p0", RequestId = requestId });
        store.Add(new QueryEvent { Sql = "SELECT * FROM Products WHERE Id = @p0", RequestId = requestId });
        store.Add(new QueryEvent { Sql = "SELECT * FROM Products WHERE Id = @p0", RequestId = requestId });

        var stats = store.GetStats();

        Assert.Single(stats.N1Patterns);
        Assert.Equal(3, stats.N1Patterns[0].Count);
    }

    [Fact]
    public void DetectN1Patterns_IgnoresQueriesWithoutRequestId()
    {
        var store = new QueryStore();

        store.Add(new QueryEvent { Sql = "SELECT * FROM Products WHERE Id = @p0", RequestId = null });
        store.Add(new QueryEvent { Sql = "SELECT * FROM Products WHERE Id = @p0", RequestId = null });
        store.Add(new QueryEvent { Sql = "SELECT * FROM Products WHERE Id = @p0", RequestId = null });

        var patterns = store.DetectN1Patterns(threshold: 3);

        Assert.Empty(patterns);
    }

    [Fact]
    public void DetectSplitQueries_DetectsRapidSuccessionDifferentQueries()
    {
        var store = new QueryStore();
        var requestId = "req-1";
        var baseTime = DateTime.UtcNow;

        // Simulate split query: different SQL patterns in rapid succession
        store.Add(new QueryEvent
        {
            Sql = "SELECT * FROM Orders WHERE Id = @p0",
            RequestId = requestId,
            Timestamp = baseTime,
            Duration = TimeSpan.FromMilliseconds(5)
        });
        store.Add(new QueryEvent
        {
            Sql = "SELECT * FROM OrderItems WHERE OrderId = @p0",
            RequestId = requestId,
            Timestamp = baseTime.AddMilliseconds(10),
            Duration = TimeSpan.FromMilliseconds(5)
        });
        store.Add(new QueryEvent
        {
            Sql = "SELECT * FROM Products WHERE Id IN (@p0, @p1)",
            RequestId = requestId,
            Timestamp = baseTime.AddMilliseconds(20),
            Duration = TimeSpan.FromMilliseconds(5)
        });

        var splits = store.DetectSplitQueries(maxGapMs: 50);

        Assert.Single(splits);
        Assert.Equal(3, splits[0].QueryCount);
        Assert.Equal(requestId, splits[0].RequestId);
    }

    [Fact]
    public void DetectSplitQueries_IgnoresSamePatternQueries()
    {
        var store = new QueryStore();
        var requestId = "req-1";
        var baseTime = DateTime.UtcNow;

        // Same SQL pattern = N+1, not split query
        store.Add(new QueryEvent
        {
            Sql = "SELECT * FROM Products WHERE Id = @p0",
            RequestId = requestId,
            Timestamp = baseTime,
            Duration = TimeSpan.FromMilliseconds(5)
        });
        store.Add(new QueryEvent
        {
            Sql = "SELECT * FROM Products WHERE Id = @p1",
            RequestId = requestId,
            Timestamp = baseTime.AddMilliseconds(10),
            Duration = TimeSpan.FromMilliseconds(5)
        });

        var splits = store.DetectSplitQueries(maxGapMs: 50);

        Assert.Empty(splits);
    }

    [Fact]
    public void DetectSplitQueries_IgnoresQueriesWithLargeGap()
    {
        var store = new QueryStore();
        var requestId = "req-1";
        var baseTime = DateTime.UtcNow;

        store.Add(new QueryEvent
        {
            Sql = "SELECT * FROM Orders",
            RequestId = requestId,
            Timestamp = baseTime,
            Duration = TimeSpan.FromMilliseconds(5)
        });
        store.Add(new QueryEvent
        {
            Sql = "SELECT * FROM Products",
            RequestId = requestId,
            Timestamp = baseTime.AddMilliseconds(200), // Large gap
            Duration = TimeSpan.FromMilliseconds(5)
        });

        var splits = store.DetectSplitQueries(maxGapMs: 50);

        Assert.Empty(splits);
    }

    [Fact]
    public void DetectSplitQueries_ExtractsTables()
    {
        var store = new QueryStore();
        var requestId = "req-1";
        var baseTime = DateTime.UtcNow;

        store.Add(new QueryEvent
        {
            Sql = "SELECT * FROM Orders WHERE Id = @p0",
            RequestId = requestId,
            Timestamp = baseTime,
            Duration = TimeSpan.FromMilliseconds(5)
        });
        store.Add(new QueryEvent
        {
            Sql = "SELECT * FROM OrderItems JOIN Products ON OrderItems.ProductId = Products.Id",
            RequestId = requestId,
            Timestamp = baseTime.AddMilliseconds(10),
            Duration = TimeSpan.FromMilliseconds(5)
        });

        var splits = store.DetectSplitQueries(maxGapMs: 50);

        Assert.Single(splits);
        Assert.Contains("Orders", splits[0].Tables);
        Assert.Contains("OrderItems", splits[0].Tables);
        Assert.Contains("Products", splits[0].Tables);
    }

    [Fact]
    public void GetStats_IncludesSplitQueryGroups()
    {
        var store = new QueryStore();
        var requestId = "req-1";
        var baseTime = DateTime.UtcNow;

        store.Add(new QueryEvent
        {
            Sql = "SELECT * FROM Orders",
            RequestId = requestId,
            Timestamp = baseTime,
            Duration = TimeSpan.FromMilliseconds(5)
        });
        store.Add(new QueryEvent
        {
            Sql = "SELECT * FROM OrderItems",
            RequestId = requestId,
            Timestamp = baseTime.AddMilliseconds(10),
            Duration = TimeSpan.FromMilliseconds(5)
        });

        var stats = store.GetStats();

        Assert.Single(stats.SplitQueryGroups);
        Assert.Equal(2, stats.SplitQueryGroups[0].QueryCount);
    }
}
