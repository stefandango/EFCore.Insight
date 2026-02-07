# EFCore.Insight

Development-time EF Core query diagnostics for ASP.NET Core. See every SQL query, catch N+1 patterns, analyze execution plans, and find performance regressions — all from a built-in dashboard. No DbContext changes required.

## Features

- **N+1 query detection** — automatically flags repeated query patterns within a single request
- **Query plan analysis** — run EXPLAIN plans for SQLite, PostgreSQL, and SQL Server directly from the dashboard
- **Cost estimation** — prioritized recommendations with estimated savings per issue
- **Performance regression tracking** — set baselines and detect queries that have gotten slower
- **Live query feed** — watch SQL execute in real time with duration, row counts, and parameters
- **Request correlation** — see which queries belong to which HTTP request and endpoint
- **Call site tracking** — stack traces pinpoint exactly where each query originates in your code
- **Split query detection** — identifies EF Core split query groups
- **Cartesian explosion warnings** — flags queries with multiple JOINs that may cause row multiplication
- **Zero config** — hooks into EF Core's `DiagnosticListener`, no interceptors or DbContext changes needed

## Installation

```bash
dotnet add package EFCore.Insight
```

**For query plan analysis**, add the provider package for your database:

```bash
# SQLite
dotnet add package Microsoft.Data.Sqlite

# PostgreSQL
dotnet add package Npgsql

# SQL Server
dotnet add package Microsoft.Data.SqlClient
```

## Quick Start

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEFCoreInsight();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseEFCoreInsight();
}

app.Run();
```

Navigate to `/_ef-insight` to open the dashboard.

## Configuration

```csharp
builder.Services.AddEFCoreInsight(options =>
{
    options.RoutePrefix = "_ef-insight";            // Dashboard URL prefix
    options.MaxStoredQueries = 1000;                // Max queries in memory
    options.EnableRequestCorrelation = true;        // Link queries to HTTP requests
    options.EnableQueryPlanAnalysis = true;         // EXPLAIN plan support
    options.QueryPlanAnalysisThresholdMs = 100;     // Auto-analyze queries slower than this
    options.EnableEndpointAnalysis = true;          // Per-endpoint query breakdown
    options.EnableQueryHistory = false;             // Persist patterns for trend analysis
    options.HistoryRetentionDays = 7;               // How long to keep history
});
```

## Dashboard

The dashboard at `/_ef-insight` has three tabs:

**Queries** — live feed of all SQL queries with duration indicators, N+1 flags, parameters, suggestions, and one-click EXPLAIN plan analysis.

**Endpoints** — per-HTTP-endpoint breakdown showing query counts, total duration, and how many queries are N+1 or slow.

**Cost Analysis** — aggregated performance impact across all captured queries with prioritized fix recommendations and estimated time savings.

## API Endpoints

All endpoints are prefixed with your configured `RoutePrefix` (default: `/_ef-insight`).

| Endpoint | Description |
|----------|-------------|
| `GET /api/queries` | List captured queries (supports filtering) |
| `GET /api/queries/{id}` | Get a single query with full details |
| `GET /api/queries/{id}/plan` | Get execution plan for a query |
| `GET /api/stats` | Aggregated statistics and N+1 patterns |
| `GET /api/endpoints` | Per-endpoint query statistics |
| `GET /api/patterns` | Unique query patterns with P95 metrics |
| `GET /api/cost-report` | Cost analysis and recommendations |
| `GET /api/history/patterns` | Historical pattern data (requires `EnableQueryHistory`) |
| `GET /api/regressions?threshold=20` | Performance regressions vs baseline |
| `DELETE /api/queries` | Clear captured queries |

### Query Filtering

```
GET /_ef-insight/api/queries?minDurationMs=100&errorsOnly=true&path=/api/users&n1Only=true
```

## Query Plan Providers

EFCore.Insight loads database providers dynamically at runtime. If a provider assembly is available, its query plan support is automatically registered:

| Provider | Package | EXPLAIN Method |
|----------|---------|----------------|
| SQLite | `Microsoft.Data.Sqlite` | `EXPLAIN QUERY PLAN` |
| PostgreSQL | `Npgsql` | `EXPLAIN (ANALYZE, FORMAT JSON)` |
| SQL Server | `Microsoft.Data.SqlClient` | `SET STATISTICS IO/TIME ON` |

You can also register custom providers:

```csharp
var planService = app.Services.GetRequiredService<QueryPlanService>();
planService.RegisterProvider(new MyCustomProvider());
```

## How It Works

EFCore.Insight subscribes to EF Core's `DiagnosticListener` to capture query events. This means:

- **Non-invasive** — no changes to your DbContext, repositories, or query code
- **Automatic** — captures all EF Core queries application-wide
- **Lightweight** — uses the built-in .NET diagnostics infrastructure

## Security

This package is intended for **development use only**. The dashboard exposes SQL queries, connection details, and stack traces. Do not enable in production environments.

## Requirements

- .NET 10 or later
- ASP.NET Core
- Entity Framework Core 10 or later

## License

Apache License 2.0 — see [LICENSE](LICENSE) for details.
