namespace EFCore.Insight.QueryPlan;

/// <summary>
/// Provides query execution plan analysis for a specific database provider.
/// </summary>
public interface IQueryPlanProvider
{
    /// <summary>
    /// Gets the database provider name this plan provider supports (e.g., "Microsoft.EntityFrameworkCore.Sqlite").
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Analyzes the execution plan for the given SQL query.
    /// </summary>
    /// <param name="sql">The SQL query to analyze.</param>
    /// <param name="connectionString">The connection string to use for analysis.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The query plan result with parsed nodes and detected issues.</returns>
    Task<QueryPlanResult> GetPlanAsync(string sql, string connectionString, CancellationToken cancellationToken = default);
}
