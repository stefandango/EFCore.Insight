using EFCore.Insight.QueryCapture;
using Xunit;

namespace EFCore.Insight.Tests;

public class QueryEventTests
{
    [Fact]
    public void QueryEvent_HasDefaultId()
    {
        var query = new QueryEvent { Sql = "SELECT 1" };

        Assert.NotEqual(Guid.Empty, query.Id);
    }

    [Fact]
    public void QueryEvent_HasDefaultTimestamp()
    {
        var before = DateTime.UtcNow;
        var query = new QueryEvent { Sql = "SELECT 1" };
        var after = DateTime.UtcNow;

        Assert.True(query.Timestamp >= before);
        Assert.True(query.Timestamp <= after);
    }

    [Fact]
    public void QueryEvent_HasDefaultEmptyParameters()
    {
        var query = new QueryEvent { Sql = "SELECT 1" };

        Assert.NotNull(query.Parameters);
        Assert.Empty(query.Parameters);
    }

    [Fact]
    public void QueryEvent_CanSetAllProperties()
    {
        var parameters = new Dictionary<string, object?> { ["@p0"] = 42 };
        var query = new QueryEvent
        {
            Sql = "SELECT * FROM Users WHERE Id = @p0",
            Parameters = parameters,
            Duration = TimeSpan.FromMilliseconds(15.5),
            RowsAffected = 1,
            RequestPath = "/api/users/42",
            RequestId = "abc123",
            CommandType = "Query",
            IsError = false,
            ErrorMessage = null
        };

        Assert.Equal("SELECT * FROM Users WHERE Id = @p0", query.Sql);
        Assert.Equal(42, query.Parameters["@p0"]);
        Assert.Equal(15.5, query.Duration.TotalMilliseconds);
        Assert.Equal(1, query.RowsAffected);
        Assert.Equal("/api/users/42", query.RequestPath);
        Assert.Equal("abc123", query.RequestId);
        Assert.Equal("Query", query.CommandType);
        Assert.False(query.IsError);
        Assert.Null(query.ErrorMessage);
    }
}
