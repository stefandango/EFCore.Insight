# EFCore.Insight Feature Backlog

## Phase 1: Quick Wins (Polish)

### 1.1 Export Queries (JSON/CSV)
**Effort:** Small

**Backend:**
- Add endpoint: `GET /api/queries/export?format=json|csv`
- Return `Content-Disposition: attachment` header

**Frontend:**
- Add export dropdown button in header next to "Clear All"
- Options: "Export JSON", "Export CSV"
- Use Blob API for client-side download

---

### 1.2 Query Grouping
**Effort:** Small

**Frontend Only:**
- Add toggle button: "Group Similar Queries"
- Group by normalized SQL (replace parameters with `?`)
- Show: SQL pattern, count, avg duration, total time
- Expandable to show individual queries

**Implementation:**
```javascript
function normalizeSQL(sql) {
  return sql.replace(/@p\d+|@\w+|'[^']*'|\d+/g, '?');
}

function groupQueries(queries) {
  return Object.groupBy(queries, q => normalizeSQL(q.sql));
}
```

---

### 1.3 Keyboard Shortcuts
**Effort:** Small

| Key | Action |
|-----|--------|
| `Esc` | Collapse expanded query |
| `/` | Focus search/filter input |
| `r` | Refresh |
| `g` | Toggle grouping |
| `?` | Show shortcuts help modal |

**Implementation:**
- Add `document.addEventListener('keydown', handleKeyboard)`
- Add help modal HTML (hidden by default)

---

### 1.4 Dark/Light Theme Toggle
**Effort:** Small

**CSS:**
- Move current colors to `[data-theme="dark"]` selector
- Add `[data-theme="light"]` with light colors
- Default to dark, persist preference in localStorage

**Frontend:**
- Add theme toggle button (sun/moon icon) in header
- Toggle `data-theme` attribute on `<html>`

---

### 1.5 Connection Info
**Effort:** Small

**Backend:**
- Capture database provider and connection string (sanitized) in `EFDiagnosticListener`
- Add to stats endpoint: `{ provider: "Sqlite", database: "sample.db" }`

**Frontend:**
- Display in stats bar or header subtitle

---

## Phase 2: Smart Analysis (Differentiation)

### 2.1 N+1 Detection ⭐ Key Feature
**Effort:** Medium

**Detection Logic (QueryStore.cs):**
```csharp
public record N1Pattern
{
    public string NormalizedSql { get; init; }
    public int Count { get; init; }
    public string RequestId { get; init; }
    public List<Guid> QueryIds { get; init; }
}

public IReadOnlyList<N1Pattern> DetectN1Patterns(int threshold = 3)
{
    return _queries
        .Where(q => q.RequestId != null)
        .GroupBy(q => (q.RequestId, NormalizeSql(q.Sql)))
        .Where(g => g.Count() >= threshold)
        .Select(g => new N1Pattern { ... })
        .ToList();
}
```

**API Response - Add to `/api/stats`:**
```json
{
  "n1Patterns": [
    {
      "normalizedSql": "SELECT * FROM Products WHERE Id = ?",
      "count": 15,
      "requestPath": "/api/orders",
      "queryIds": ["guid1", "guid2", ...]
    }
  ],
  "n1PatternCount": 3
}
```

**Frontend:**
- Add stat: "N+1 Patterns: X" (red if > 0)
- Add filter: "Show N+1 only"
- Badge on affected queries: `<span class="badge n1">N+1 (x15)</span>`
- New section: "N+1 Patterns Detected" with expandable details

---

### 2.2 Slow Query Alerts
**Effort:** Small

- Highlight queries > configurable threshold
- Add option: `SlowQueryThresholdMs` (default: 100)
- Visual indicator in stats bar

---

### 2.3 Query Plan Viewer
**Effort:** Medium

- Add "Explain" button per query
- Execute `EXPLAIN` / `EXPLAIN ANALYZE` for the query
- Display execution plan in collapsible section
- Support: SQLite, PostgreSQL, SQL Server

---

### 2.4 Duplicate Detection
**Effort:** Small

- Detect identical queries (same SQL + same params) in same request
- Badge: "Duplicate (x3)"
- Separate from N+1 (N+1 = same SQL, different params)

---

### 2.5 Missing Index Hints
**Effort:** Medium

- Parse query plan for table scans
- Suggest indexes based on WHERE/JOIN clauses
- Display as warning on affected queries

---

## Phase 3: Developer Experience

### 3.1 Stack Trace Capture
**Effort:** Medium

- Capture call stack when query executes
- Store abbreviated stack (top 5-10 frames)
- Display in query details: "Called from: UserService.cs:42"

---

### 3.2 Entity Mapping
**Effort:** Medium

- Show which DbSet/entity triggered the query
- Parse EF Core diagnostics for entity info
- Display: "Entity: Product (Products table)"

---

### 3.3 Diff View
**Effort:** Small

- Select two queries to compare
- Side-by-side diff view
- Highlight differences in SQL/params

---

### 3.4 Query Bookmarks
**Effort:** Small

- Star/bookmark interesting queries
- Persist to localStorage
- Filter: "Show bookmarked only"

---

### 3.5 WebSocket Live Feed
**Effort:** Medium

- Replace polling with WebSocket connection
- Real-time query feed as they execute
- Reduce server load vs 2-second polling

---

## Phase 4: Production-Ready

### 4.1 Authentication
**Effort:** Medium

- Protect dashboard with authentication
- Options: API key, ASP.NET Identity integration
- Config: `options.RequireAuthentication = true`

---

### 4.2 PII Redaction
**Effort:** Medium

- Auto-mask sensitive parameter values
- Configurable patterns: email, phone, SSN
- Config: `options.RedactPatterns = [...]`

---

### 4.3 Sampling
**Effort:** Small

- Capture only X% of queries in production
- Config: `options.SamplingRate = 0.01` (1%)
- Reduces memory/performance overhead

---

### 4.4 Persistence
**Effort:** Medium

- Store queries to SQLite/file instead of memory
- Survive app restarts
- Configurable retention period

---

### 4.5 Metrics Export
**Effort:** Medium

- Prometheus endpoint: `/metrics`
- OpenTelemetry integration
- Metrics: query count, duration histogram, error rate

---

## Files to Modify

| File | Purpose |
|------|---------|
| `src/EFCore.Insight/Dashboard/assets/index.html` | UI, JavaScript, CSS |
| `src/EFCore.Insight/Api/InsightApiEndpoints.cs` | API endpoints |
| `src/EFCore.Insight/QueryCapture/QueryStore.cs` | Storage, detection logic |
| `src/EFCore.Insight/QueryCapture/QueryEvent.cs` | Data model |
| `src/EFCore.Insight/QueryCapture/QueryStats.cs` | Stats model |
| `src/EFCore.Insight/QueryCapture/EFDiagnosticListener.cs` | Event capture |
| `src/EFCore.Insight/InsightOptions.cs` | Configuration |

---

## Similar Tools (Competitive Analysis)

| Tool | In-App UI | Free | EF Core Native | N+1 Detection |
|------|-----------|------|----------------|---------------|
| **EFCore.Insight** | ✓ | ✓ | ✓ | Planned |
| MiniProfiler | ✓ | ✓ | Via adapter | Limited |
| Glimpse | ✓ | ✓ | Discontinued | - |
| EF Logging | ✗ | ✓ | ✓ | ✗ |
| Stackify Prefix | ✓ | Freemium | Via .NET | ✗ |

**Key Differentiator:** N+1 detection with visual highlighting would set EFCore.Insight apart from alternatives.

---

## Recommended Implementation Order

1. Theme Toggle (visible, easy win)
2. Keyboard Shortcuts (quick to add)
3. N+1 Detection (key differentiator)
4. Export (useful utility)
5. Query Grouping (UI enhancement)
6. Connection Info (polish)
