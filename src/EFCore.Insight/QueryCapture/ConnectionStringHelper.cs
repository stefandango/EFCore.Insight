using System.Data.Common;

namespace EFCore.Insight.QueryCapture;

internal static class ConnectionStringHelper
{
    public static string GetFriendlyProviderName(string? providerName)
    {
        if (string.IsNullOrEmpty(providerName))
            return "Unknown";

        return providerName switch
        {
            _ when providerName.EndsWith(".Sqlite", StringComparison.OrdinalIgnoreCase) => "SQLite",
            _ when providerName.EndsWith(".SqlServer", StringComparison.OrdinalIgnoreCase) => "SQL Server",
            _ when providerName.EndsWith(".Npgsql", StringComparison.OrdinalIgnoreCase) => "PostgreSQL",
            _ when providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) => "PostgreSQL",
            _ when providerName.EndsWith(".MySql", StringComparison.OrdinalIgnoreCase) => "MySQL",
            _ when providerName.Contains("Pomelo", StringComparison.OrdinalIgnoreCase) => "MySQL",
            _ when providerName.EndsWith(".Oracle", StringComparison.OrdinalIgnoreCase) => "Oracle",
            _ when providerName.EndsWith(".Cosmos", StringComparison.OrdinalIgnoreCase) => "Cosmos DB",
            _ when providerName.EndsWith(".InMemory", StringComparison.OrdinalIgnoreCase) => "In-Memory",
            _ => providerName.Contains('.') ? providerName[(providerName.LastIndexOf('.') + 1)..] : providerName
        };
    }

    public static string? SanitizeDatabaseId(string? connectionString, string? providerName)
    {
        if (string.IsNullOrEmpty(connectionString))
            return null;

        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            var friendly = GetFriendlyProviderName(providerName);

            return friendly switch
            {
                "SQLite" => SanitizeSqlite(builder),
                "SQL Server" => SanitizeServerDatabase(builder),
                "PostgreSQL" => SanitizeServerDatabase(builder),
                "MySQL" => SanitizeServerDatabase(builder),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? SanitizeSqlite(DbConnectionStringBuilder builder)
    {
        if (!builder.TryGetValue("Data Source", out var dataSource) &&
            !builder.TryGetValue("DataSource", out dataSource) &&
            !builder.TryGetValue("Filename", out dataSource))
            return null;

        var ds = dataSource?.ToString();
        if (string.IsNullOrEmpty(ds))
            return null;

        if (ds == ":memory:" || ds.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
            return ":memory:";

        return Path.GetFileName(ds);
    }

    private static string? SanitizeServerDatabase(DbConnectionStringBuilder builder)
    {
        string? server = null;
        string? database = null;

        if (builder.TryGetValue("Server", out var s))
            server = s?.ToString();
        else if (builder.TryGetValue("Data Source", out s))
            server = s?.ToString();
        else if (builder.TryGetValue("Host", out s))
            server = s?.ToString();

        if (builder.TryGetValue("Database", out var d))
            database = d?.ToString();
        else if (builder.TryGetValue("Initial Catalog", out d))
            database = d?.ToString();

        // Strip port from server
        if (server is not null)
        {
            var commaIndex = server.IndexOf(',');
            if (commaIndex > 0)
                server = server[..commaIndex];

            var colonIndex = server.IndexOf(':');
            if (colonIndex > 0)
                server = server[..colonIndex];
        }

        if (server is null && database is null)
            return null;

        if (database is not null)
            return server is not null ? $"{server}/{database}" : database;

        return server;
    }
}
