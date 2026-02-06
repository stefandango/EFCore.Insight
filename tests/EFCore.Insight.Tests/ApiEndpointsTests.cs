using System.Net;
using System.Text.Json;
using EFCore.Insight.Cost;
using EFCore.Insight.QueryCapture;
using EFCore.Insight.QueryPlan;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EFCore.Insight.Tests;

public class ApiEndpointsTests : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;
    private readonly QueryStore _store;

    public ApiEndpointsTests()
    {
        _store = new QueryStore();
        var options = new InsightOptions();

        _host = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(options);
                        services.AddSingleton(_store);
                        services.AddHttpContextAccessor();
                        services.AddSingleton<EFDiagnosticListener>();
                        services.AddSingleton(new QueryPlanService(options));
                        services.AddSingleton<CostCalculator>();
                    })
                    .Configure(app =>
                    {
                        app.UseEFCoreInsight();
                    });
            })
            .Build();

        _host.Start();
        _client = _host.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task GetQueries_WithStoredQueries_ReturnsAll()
    {
        _store.Add(new QueryEvent { Sql = "SELECT 1", Duration = TimeSpan.FromMilliseconds(10) });
        _store.Add(new QueryEvent { Sql = "SELECT 2", Duration = TimeSpan.FromMilliseconds(20) });

        var response = await _client.GetAsync("/_ef-insight/api/queries");
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        Assert.Equal(2, json.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task GetQueries_FilterByMinDuration_FiltersCorrectly()
    {
        _store.Add(new QueryEvent { Sql = "SELECT 1", Duration = TimeSpan.FromMilliseconds(10) });
        _store.Add(new QueryEvent { Sql = "SELECT 2", Duration = TimeSpan.FromMilliseconds(50) });

        var response = await _client.GetAsync("/_ef-insight/api/queries?minDurationMs=30");
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        Assert.Equal(1, json.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task GetQueries_FilterByPath_FiltersCorrectly()
    {
        _store.Add(new QueryEvent { Sql = "SELECT 1", RequestPath = "/api/users" });
        _store.Add(new QueryEvent { Sql = "SELECT 2", RequestPath = "/api/orders" });

        var response = await _client.GetAsync("/_ef-insight/api/queries?path=users");
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        Assert.Equal(1, json.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task GetQueries_FilterByErrorsOnly_FiltersCorrectly()
    {
        _store.Add(new QueryEvent { Sql = "SELECT 1", IsError = false });
        _store.Add(new QueryEvent { Sql = "SELECT 2", IsError = true, ErrorMessage = "Test error" });

        var response = await _client.GetAsync("/_ef-insight/api/queries?errorsOnly=true");
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        Assert.Equal(1, json.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task GetQueryById_WithValidId_ReturnsQuery()
    {
        var query = new QueryEvent { Sql = "SELECT 1" };
        _store.Add(query);

        var response = await _client.GetAsync($"/_ef-insight/api/queries/{query.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        Assert.Equal(query.Id.ToString(), json.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetStats_ReturnsCorrectStats()
    {
        _store.Add(new QueryEvent { Sql = "SELECT 1", Duration = TimeSpan.FromMilliseconds(10) });
        _store.Add(new QueryEvent { Sql = "SELECT 2", Duration = TimeSpan.FromMilliseconds(20), IsError = true });

        var response = await _client.GetAsync("/_ef-insight/api/stats");
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        Assert.Equal(2, json.RootElement.GetProperty("totalQueries").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("errorCount").GetInt32());
        Assert.Equal(15, json.RootElement.GetProperty("averageDurationMs").GetDouble());
    }

    [Fact]
    public async Task ClearQueries_ClearsStore()
    {
        _store.Add(new QueryEvent { Sql = "SELECT 1" });
        _store.Add(new QueryEvent { Sql = "SELECT 2" });

        await _client.DeleteAsync("/_ef-insight/api/queries");

        Assert.Equal(0, _store.Count);
    }

    [Fact]
    public async Task GetCostReport_WithSelectAllQueries_ReturnsRecommendations()
    {
        // Add queries with SELECT * pattern and significant duration to trigger recommendations
        _store.Add(new QueryEvent
        {
            Sql = "SELECT * FROM Users WHERE Id = @p0",
            Duration = TimeSpan.FromMilliseconds(100),
            Timestamp = DateTime.UtcNow.AddSeconds(-30)
        });
        _store.Add(new QueryEvent
        {
            Sql = "SELECT * FROM Users WHERE Id = @p1",
            Duration = TimeSpan.FromMilliseconds(100),
            Timestamp = DateTime.UtcNow
        });

        var response = await _client.GetAsync("/_ef-insight/api/cost-report");
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        Assert.True(json.RootElement.GetProperty("totalQueryTimeMs").GetDouble() > 0);
        Assert.True(json.RootElement.GetProperty("recommendationCount").GetInt32() > 0);
    }

    [Fact]
    public async Task GetCostReport_WithCartesianExplosion_ReturnsNonZeroSavings()
    {
        // Add a query with multiple JOINs to trigger CartesianExplosion detection
        var sql = @"SELECT u.*, o.*, oi.*
            FROM Users u
            LEFT JOIN Orders o ON o.UserId = u.Id
            LEFT JOIN OrderItems oi ON oi.OrderId = o.Id
            WHERE u.Id = @p0";

        _store.Add(new QueryEvent
        {
            Sql = sql,
            Duration = TimeSpan.FromMilliseconds(200),
            RowsAffected = 500,
            Timestamp = DateTime.UtcNow.AddSeconds(-30)
        });
        _store.Add(new QueryEvent
        {
            Sql = sql,
            Duration = TimeSpan.FromMilliseconds(200),
            RowsAffected = 500,
            Timestamp = DateTime.UtcNow
        });

        var response = await _client.GetAsync("/_ef-insight/api/cost-report");
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        var recommendations = json.RootElement.GetProperty("recommendations");
        Assert.True(recommendations.GetArrayLength() > 0, "Should have at least one recommendation");

        // Check that at least one recommendation has non-zero savings
        var hasNonZeroSavings = false;
        foreach (var rec in recommendations.EnumerateArray())
        {
            var timeSaved = rec.GetProperty("estimatedTimeSavedPerMinMs").GetDouble();
            if (timeSaved > 0)
            {
                hasNonZeroSavings = true;
                break;
            }
        }

        Assert.True(hasNonZeroSavings, "Should have at least one recommendation with non-zero savings");
    }
}
