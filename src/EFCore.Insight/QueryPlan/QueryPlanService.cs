namespace EFCore.Insight.QueryPlan;

/// <summary>
/// Service for analyzing query execution plans across different database providers.
/// </summary>
public sealed class QueryPlanService
{
    private readonly Dictionary<string, IQueryPlanProvider> _providers;
    private readonly InsightOptions _options;

    public QueryPlanService(InsightOptions options)
    {
        _options = options;
        _providers = new Dictionary<string, IQueryPlanProvider>(StringComparer.OrdinalIgnoreCase);

        // Try to register built-in providers (assemblies may not be available at runtime)
        TryRegisterProvider(() => new SqliteQueryPlanProvider());
        TryRegisterProvider(() => new PostgresQueryPlanProvider());
        TryRegisterProvider(() => new SqlServerQueryPlanProvider());
    }

    private void TryRegisterProvider(Func<IQueryPlanProvider> factory)
    {
        try
        {
            RegisterProvider(factory());
        }
        catch (Exception)
        {
            // Provider assembly not available — skip
        }
    }

    /// <summary>
    /// Registers a custom query plan provider.
    /// </summary>
    public void RegisterProvider(IQueryPlanProvider provider)
    {
        _providers[provider.ProviderName] = provider;
    }

    /// <summary>
    /// Gets the plan for a query using the appropriate provider.
    /// </summary>
    /// <param name="sql">The SQL query to analyze.</param>
    /// <param name="providerName">The database provider name.</param>
    /// <param name="connectionString">The connection string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The query plan result, or null if the provider is not supported.</returns>
    public async Task<QueryPlanResult?> GetPlanAsync(
        string sql,
        string providerName,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        if (!_options.EnableQueryPlanAnalysis)
        {
            return new QueryPlanResult
            {
                Sql = sql,
                RawPlan = string.Empty,
                Provider = providerName,
                IsSuccess = false,
                ErrorMessage = "Query plan analysis is disabled. Enable it with options.EnableQueryPlanAnalysis = true"
            };
        }

        if (!_providers.TryGetValue(providerName, out var provider))
        {
            return new QueryPlanResult
            {
                Sql = sql,
                RawPlan = string.Empty,
                Provider = providerName,
                IsSuccess = false,
                ErrorMessage = $"No query plan provider found for '{providerName}'. Supported providers: {string.Join(", ", _providers.Keys)}"
            };
        }

        return await provider.GetPlanAsync(sql, connectionString, cancellationToken);
    }

    /// <summary>
    /// Checks if a provider is supported.
    /// </summary>
    public bool IsProviderSupported(string providerName)
    {
        return _providers.ContainsKey(providerName);
    }

    /// <summary>
    /// Gets all supported provider names.
    /// </summary>
    public IReadOnlyCollection<string> SupportedProviders => _providers.Keys.ToList();
}
