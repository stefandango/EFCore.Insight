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
}
