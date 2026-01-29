using System.Net;
using EFCore.Insight.QueryCapture;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EFCore.Insight.Tests;

public class DashboardMiddlewareTests : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;

    public DashboardMiddlewareTests()
    {
        _host = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddEFCoreInsight();
                    })
                    .Configure(app =>
                    {
                        app.UseEFCoreInsight();
                        app.Run(context =>
                        {
                            context.Response.StatusCode = 404;
                            return Task.CompletedTask;
                        });
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
    public async Task Dashboard_ReturnsHtml()
    {
        var response = await _client.GetAsync("/_ef-insight");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("EFCore.Insight", content);
        Assert.Contains("<!DOCTYPE html>", content);
    }

    [Fact]
    public async Task Dashboard_WithTrailingSlash_ReturnsHtml()
    {
        var response = await _client.GetAsync("/_ef-insight/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Api_GetQueries_ReturnsJson()
    {
        var response = await _client.GetAsync("/_ef-insight/api/queries");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"count\"", content);
        Assert.Contains("\"queries\"", content);
    }

    [Fact]
    public async Task Api_GetStats_ReturnsJson()
    {
        var response = await _client.GetAsync("/_ef-insight/api/stats");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"totalQueries\"", content);
    }

    [Fact]
    public async Task Api_ClearQueries_ReturnsSuccess()
    {
        var response = await _client.DeleteAsync("/_ef-insight/api/queries");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"success\":true", content);
    }

    [Fact]
    public async Task Api_GetQueryById_ReturnsNotFoundForUnknownId()
    {
        var response = await _client.GetAsync($"/_ef-insight/api/queries/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnknownPath_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/_ef-insight/unknown");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OtherRoutes_PassThrough()
    {
        var response = await _client.GetAsync("/other-route");

        // Should fall through to the default 404 handler
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
