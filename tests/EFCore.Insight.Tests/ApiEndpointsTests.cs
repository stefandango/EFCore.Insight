using System.Net;
using System.Text.Json;
using EFCore.Insight.QueryCapture;
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

        _host = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(new InsightOptions());
                        services.AddSingleton(_store);
                        services.AddHttpContextAccessor();
                        services.AddSingleton<EFDiagnosticListener>();
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
}
