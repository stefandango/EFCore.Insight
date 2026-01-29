# EFCore.Insight - Architecture & Planning

## Overview

EFCore.Insight is a developer productivity tool that provides visibility into Entity Framework Core query execution during development. It helps developers understand exactly what SQL is being generated, how long queries take, and which HTTP requests trigger which database operations.

## Vision

Make EF Core query behavior transparent and observable without leaving your development workflow.

## V1 Scope: Query Timing + SQL Visibility

A NuGet package with ASP.NET Core middleware that captures and displays query information in a development dashboard.

```csharp
// Program.cs
if (app.Environment.IsDevelopment())
{
    app.UseEFCoreInsight(); // Adds /_ef-insight dashboard
}
```

### V1 Features

- **Live query feed** - See SQL as it executes in real-time
- **Query timing** - Duration in milliseconds for each query
- **Row counts** - Number of rows returned/affected
- **Request correlation** - Which HTTP request triggered which queries
- **Query history** - Searchable/filterable history with configurable retention
- **Copy SQL** - One-click copy of generated SQL for debugging

---

## Project Structure

```
EFCore.Insight/
├── EFCore.Insight.slnx
├── Directory.Build.props
├── Directory.Packages.props
├── README.md
├── PLANNING.md
├── src/
│   └── EFCore.Insight/
│       ├── EFCore.Insight.csproj
│       ├── InsightMiddleware.cs
│       ├── InsightOptions.cs
│       ├── ServiceCollectionExtensions.cs
│       ├── ApplicationBuilderExtensions.cs
│       ├── QueryCapture/
│       │   ├── EFDiagnosticListener.cs
│       │   ├── QueryEvent.cs
│       │   └── QueryStore.cs
│       ├── Dashboard/
│       │   ├── DashboardMiddleware.cs
│       │   └── assets/
│       │       └── index.html
│       └── Api/
│           └── InsightApiEndpoints.cs
└── tests/
    └── EFCore.Insight.Tests/
        └── ...
```

---

## Core Components

### Query Capture

**EFDiagnosticListener** subscribes to EF Core's diagnostic events:
- `Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted`
- `Microsoft.EntityFrameworkCore.Database.Command.CommandError`

```csharp
public class EFDiagnosticListener : IObserver<DiagnosticListener>
{
    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "Microsoft.EntityFrameworkCore")
        {
            listener.Subscribe(new EFEventObserver(_queryStore, _httpContextAccessor));
        }
    }
}
```

**QueryEvent** captures data for each query:

```csharp
public record QueryEvent
{
    public Guid Id { get; init; }
    public DateTime Timestamp { get; init; }
    public string Sql { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, object?> Parameters { get; init; }
    public TimeSpan Duration { get; init; }
    public int? RowsAffected { get; init; }
    public string? RequestPath { get; init; }
    public string? RequestId { get; init; }
    public bool IsError { get; init; }
    public string? ErrorMessage { get; init; }
}
```

**QueryStore** maintains an in-memory ring buffer of recent queries:
- Thread-safe (ConcurrentQueue or similar)
- Configurable max size (default 1000)
- Auto-eviction of oldest entries

### ASP.NET Core Integration

**InsightOptions** for configuration:

```csharp
public class InsightOptions
{
    public string RoutePrefix { get; set; } = "_ef-insight";
    public int MaxStoredQueries { get; set; } = 1000;
    public bool EnableRequestCorrelation { get; set; } = true;
    public Func<HttpContext, bool>? AuthorizationFilter { get; set; }
}
```

**Extension methods**:

```csharp
// Services registration
services.AddEFCoreInsight(options =>
{
    options.MaxStoredQueries = 500;
});

// Middleware pipeline
app.UseEFCoreInsight();
```

### Dashboard

**Embedded SPA** served from `/_ef-insight`:
- Single HTML file with inline CSS/JS (no build tooling needed)
- Embedded as assembly resource
- Vanilla JavaScript for simplicity

**API endpoints**:
- `GET /_ef-insight/api/queries` - List queries (supports pagination, filtering)
- `GET /_ef-insight/api/queries/{id}` - Single query details
- `DELETE /_ef-insight/api/queries` - Clear history
- `GET /_ef-insight/api/stats` - Summary (total queries, avg duration, etc.)

---

## Implementation Tasks

### Phase 1: Project Setup
- [ ] Initialize git repository
- [ ] Create solution with .slnx format
- [ ] Set up Directory.Build.props / Directory.Packages.props
- [ ] Create main library project targeting net10.0
- [ ] Create test project

### Phase 2: Query Capture
- [ ] Implement QueryEvent record
- [ ] Implement QueryStore with ring buffer
- [ ] Implement EFDiagnosticListener
- [ ] Implement EFEventObserver
- [ ] Add HTTP context correlation

### Phase 3: ASP.NET Core Integration
- [ ] Implement InsightOptions
- [ ] Implement AddEFCoreInsight extension
- [ ] Implement UseEFCoreInsight extension
- [ ] Add authorization support

### Phase 4: Dashboard
- [ ] Create API endpoints
- [ ] Build dashboard HTML/CSS/JS
- [ ] Implement embedded resource serving
- [ ] Add real-time updates (polling or SSE)

### Phase 5: Testing & Polish
- [ ] Unit tests for QueryStore
- [ ] Integration tests for middleware
- [ ] Integration tests for query capture
- [ ] README documentation
- [ ] NuGet package setup

---

## Future Roadmap

### Phase 2: CLI Tool
- Global dotnet tool: `dotnet tool install -g EFCore.Insight.Cli`
- Attach to running processes via EventPipe
- Terminal UI with real-time query display
- JSON/CSV export

### Phase 3: Static Analysis
- Roslyn analyzer package
- Detect client-side evaluation patterns
- N+1 query warnings
- Missing AsNoTracking suggestions

### Phase 4: Advanced Features
- Query plan visualization
- Index suggestions based on query patterns
- Memory tracking / result set sizes
- Slow query alerts / thresholds
- Query diff (compare before/after)

---

## Design Decisions

### Why Middleware over Global Tool?

For V1, middleware is simpler:
- No cross-process communication
- Easy to install (just NuGet)
- Automatic HTTP correlation
- Familiar pattern (like Swagger)

CLI tool comes in Phase 2 for non-web scenarios.

### Why Embedded UI vs. External?

Embedded advantages:
- Single package, nothing to install separately
- Works offline
- No CORS issues
- Consistent with MiniProfiler, Swagger patterns

### Why Not Just Use MiniProfiler?

MiniProfiler is excellent but:
- Broader scope (all profiling, not EF-focused)
- EFCore.Insight can provide EF-specific features (query analysis, N+1 detection)
- Simpler, focused tool for EF-only scenarios
- Foundation for static analysis features

---

## Tech Stack

- **Language**: C# with .NET 10
- **Framework**: ASP.NET Core
- **Dependencies**: Microsoft.EntityFrameworkCore (for diagnostic types)
- **Testing**: xUnit
- **UI**: Vanilla HTML/CSS/JS (embedded)

---

## Success Criteria

1. Single NuGet install enables query visibility
2. Dashboard shows queries within 100ms of execution
3. No measurable performance impact in production (middleware only active in Development)
4. Request correlation accurately links queries to HTTP requests
5. Works with all EF Core database providers (SQL Server, PostgreSQL, SQLite, etc.)
