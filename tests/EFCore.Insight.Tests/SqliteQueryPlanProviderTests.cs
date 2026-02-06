using EFCore.Insight.QueryPlan;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EFCore.Insight.Tests;

public class SqliteQueryPlanProviderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _connectionString;

    public SqliteQueryPlanProviderTests()
    {
        // Use a shared in-memory database that persists across connections
        _connectionString = "Data Source=InMemoryTest;Mode=Memory;Cache=Shared";
        _connection = new SqliteConnection(_connectionString);
        _connection.Open();

        // Create test table
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Users (
                Id INTEGER PRIMARY KEY,
                Name TEXT NOT NULL,
                Email TEXT NOT NULL
            );
            DELETE FROM Users;
            INSERT INTO Users (Name, Email) VALUES ('Alice', 'alice@example.com');
            INSERT INTO Users (Name, Email) VALUES ('Bob', 'bob@example.com');
        ";
        cmd.ExecuteNonQuery();

        // Create index if not exists
        cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_Users_Email ON Users (Email);";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task GetPlanAsync_SimpleQuery_ReturnsNodes()
    {
        var provider = new SqliteQueryPlanProvider();
        var sql = "SELECT * FROM Users WHERE Id = 1";

        var result = await provider.GetPlanAsync(sql, _connectionString);

        Assert.True(result.IsSuccess);
        Assert.NotEmpty(result.Nodes);
        Assert.NotEmpty(result.RawPlan);
    }

    [Fact]
    public async Task GetPlanAsync_TableScan_DetectsIssue()
    {
        var provider = new SqliteQueryPlanProvider();
        // Query on Name column which has no index - should cause table scan
        var sql = "SELECT * FROM Users WHERE Name = 'Alice'";

        var result = await provider.GetPlanAsync(sql, _connectionString);

        Assert.True(result.IsSuccess);
        Assert.NotEmpty(result.Nodes);
        Assert.Contains(result.Nodes, n => n.IsTableScan);
        Assert.Contains(result.Issues, i => i.Type == PlanIssueType.TableScan);
    }

    [Fact]
    public async Task GetPlanAsync_IndexSeek_NoTableScanIssue()
    {
        var provider = new SqliteQueryPlanProvider();
        // Query on Email column which has an index
        var sql = "SELECT * FROM Users WHERE Email = 'alice@example.com'";

        var result = await provider.GetPlanAsync(sql, _connectionString);

        Assert.True(result.IsSuccess);
        Assert.NotEmpty(result.Nodes);
        Assert.Contains(result.Nodes, n => n.UsesIndex);
        Assert.DoesNotContain(result.Issues, i => i.Type == PlanIssueType.TableScan);
    }

    [Fact]
    public async Task GetPlanAsync_InvalidSql_ReturnsError()
    {
        var provider = new SqliteQueryPlanProvider();
        var sql = "SELECT * FROM NonExistentTable";

        var result = await provider.GetPlanAsync(sql, _connectionString);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
    }
}
