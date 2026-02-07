using EFCore.Insight.QueryCapture;
using Xunit;

namespace EFCore.Insight.Tests;

public class ConnectionStringHelperTests
{
    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite", "SQLite")]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", "SQL Server")]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", "PostgreSQL")]
    [InlineData("Pomelo.EntityFrameworkCore.MySql", "MySQL")]
    [InlineData("Microsoft.EntityFrameworkCore.Cosmos", "Cosmos DB")]
    [InlineData("Microsoft.EntityFrameworkCore.InMemory", "In-Memory")]
    [InlineData("Oracle.EntityFrameworkCore.Oracle", "Oracle")]
    public void GetFriendlyProviderName_KnownProviders_ReturnsExpected(string providerName, string expected)
    {
        var result = ConnectionStringHelper.GetFriendlyProviderName(providerName);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetFriendlyProviderName_UnknownProvider_ReturnsLastSegment()
    {
        var result = ConnectionStringHelper.GetFriendlyProviderName("SomeVendor.EntityFrameworkCore.CockroachDb");
        Assert.Equal("CockroachDb", result);
    }

    [Fact]
    public void GetFriendlyProviderName_NoDots_ReturnsAsIs()
    {
        var result = ConnectionStringHelper.GetFriendlyProviderName("CustomProvider");
        Assert.Equal("CustomProvider", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetFriendlyProviderName_NullOrEmpty_ReturnsUnknown(string? providerName)
    {
        var result = ConnectionStringHelper.GetFriendlyProviderName(providerName);
        Assert.Equal("Unknown", result);
    }

    [Fact]
    public void SanitizeDatabaseId_SqliteFilePath_ReturnsFileName()
    {
        var result = ConnectionStringHelper.SanitizeDatabaseId(
            "Data Source=/path/to/sample.db",
            "Microsoft.EntityFrameworkCore.Sqlite");
        Assert.Equal("sample.db", result);
    }

    [Fact]
    public void SanitizeDatabaseId_SqliteMemory_ReturnsMemory()
    {
        var result = ConnectionStringHelper.SanitizeDatabaseId(
            "Data Source=:memory:",
            "Microsoft.EntityFrameworkCore.Sqlite");
        Assert.Equal(":memory:", result);
    }

    [Fact]
    public void SanitizeDatabaseId_SqlServer_ReturnsServerDatabase()
    {
        var result = ConnectionStringHelper.SanitizeDatabaseId(
            "Server=myserver,1433;Database=mydb;User Id=sa;Password=secret123;",
            "Microsoft.EntityFrameworkCore.SqlServer");
        Assert.Equal("myserver/mydb", result);
    }

    [Fact]
    public void SanitizeDatabaseId_PostgreSQL_ReturnsServerDatabase()
    {
        var result = ConnectionStringHelper.SanitizeDatabaseId(
            "Host=localhost;Database=appdb;Username=admin;Password=secret;",
            "Npgsql.EntityFrameworkCore.PostgreSQL");
        Assert.Equal("localhost/appdb", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SanitizeDatabaseId_NullOrEmptyConnectionString_ReturnsNull(string? connectionString)
    {
        var result = ConnectionStringHelper.SanitizeDatabaseId(
            connectionString,
            "Microsoft.EntityFrameworkCore.Sqlite");
        Assert.Null(result);
    }

    [Fact]
    public void SanitizeDatabaseId_InvalidConnectionString_ReturnsNull()
    {
        var result = ConnectionStringHelper.SanitizeDatabaseId(
            "not a valid connection string ;;;===",
            "Microsoft.EntityFrameworkCore.SqlServer");
        // Should not throw, returns null on failure
        Assert.Null(result);
    }

    [Fact]
    public void SanitizeDatabaseId_UnknownProvider_ReturnsNull()
    {
        var result = ConnectionStringHelper.SanitizeDatabaseId(
            "Data Source=test.db",
            "SomeUnknown.Provider");
        Assert.Null(result);
    }

    [Fact]
    public void SanitizeDatabaseId_SqlServerWithInitialCatalog_ReturnsServerDatabase()
    {
        var result = ConnectionStringHelper.SanitizeDatabaseId(
            "Data Source=server1;Initial Catalog=catalog1;Integrated Security=true;",
            "Microsoft.EntityFrameworkCore.SqlServer");
        Assert.Equal("server1/catalog1", result);
    }
}
