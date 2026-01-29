# EFCore.Insight

Real-time EF Core query monitoring for ASP.NET Core applications.

## Features

- **Live query feed** - See SQL as it executes
- **Query timing** - Duration in milliseconds
- **Row counts** - Rows returned/affected
- **Request correlation** - Link queries to HTTP requests
- **Error tracking** - Capture failed queries with exception details
- **Zero config** - Uses DiagnosticListener, no DbContext changes needed

## Installation

```bash
dotnet add package EFCore.Insight
```

## Quick Start

```csharp
var builder = WebApplication.CreateBuilder(args);

// Add EFCore.Insight services
builder.Services.AddEFCoreInsight();

var app = builder.Build();

// Enable the dashboard (development only recommended)
if (app.Environment.IsDevelopment())
{
    app.UseEFCoreInsight();
}

app.Run();
```

Navigate to `/_ef-insight` to view the query dashboard.

## Configuration

```csharp
builder.Services.AddEFCoreInsight(options =>
{
    options.RoutePrefix = "_ef-insight";     // Dashboard URL prefix
    options.MaxStoredQueries = 1000;         // Max queries in memory
    options.EnableRequestCorrelation = true; // Link queries to HTTP requests
});
```

## API Endpoints

| Endpoint | Description |
|----------|-------------|
| `GET /_ef-insight` | Dashboard UI |
| `GET /_ef-insight/api/queries` | List captured queries |
| `GET /_ef-insight/api/queries/{id}` | Get query by ID |
| `GET /_ef-insight/api/stats` | Aggregated statistics |
| `DELETE /_ef-insight/api/queries` | Clear query history |

### Query Filtering

```
GET /_ef-insight/api/queries?minDurationMs=100&errorsOnly=true&path=/api/users&requestId=abc123
```

## How It Works

EFCore.Insight subscribes to EF Core's `DiagnosticListener` to capture query events. This approach is:

- **Non-invasive** - No changes to your DbContext or interceptors
- **Lightweight** - Uses built-in .NET diagnostics infrastructure
- **Automatic** - Captures all EF Core queries application-wide

## Security

This package is intended for **development use only**. The dashboard exposes sensitive information about your database queries. Do not enable in production environments.

## Requirements

- .NET 10.0 or later
- ASP.NET Core
- Entity Framework Core 10.0 or later

## License

Apache License 2.0 - see [LICENSE](LICENSE) for details.
