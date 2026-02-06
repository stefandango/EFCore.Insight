using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using EFCore.Insight.History;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EFCore.Insight.QueryCapture;

/// <summary>
/// Diagnostic listener that captures EF Core query execution events.
/// </summary>
public sealed class EFDiagnosticListener : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly QueryStore _store;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IQueryHistoryStore? _historyStore;
    private readonly List<IDisposable> _subscriptions = [];

    public EFDiagnosticListener(QueryStore store, IHttpContextAccessor httpContextAccessor, IQueryHistoryStore? historyStore = null)
    {
        _store = store;
        _httpContextAccessor = httpContextAccessor;
        _historyStore = historyStore;
    }

    public void Subscribe()
    {
        var subscription = DiagnosticListener.AllListeners.Subscribe(this);
        _subscriptions.Add(subscription);
    }

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "Microsoft.EntityFrameworkCore")
        {
            var subscription = listener.Subscribe(this);
            _subscriptions.Add(subscription);
        }
    }

    void IObserver<DiagnosticListener>.OnError(Exception error) { }
    void IObserver<DiagnosticListener>.OnCompleted() { }

    private const string CommandExecutedEventName = "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted";
    private const string CommandErrorEventName = "Microsoft.EntityFrameworkCore.Database.Command.CommandError";

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> pair)
    {
        switch (pair.Key)
        {
            case CommandExecutedEventName:
                HandleCommandExecuted(pair.Value);
                break;
            case CommandErrorEventName:
                HandleCommandError(pair.Value);
                break;
        }
    }

    private void HandleCommandExecuted(object? value)
    {
        if (value is CommandExecutedEventData eventData)
        {
            var queryEvent = CreateQueryEvent(eventData.Command, eventData.Duration, eventData.Result, eventData.Context);
            _store.Add(queryEvent);

            // Record to history store for pattern tracking
            RecordToHistoryAsync(queryEvent).ConfigureAwait(false);
        }
    }

    private void HandleCommandError(object? value)
    {
        if (value is CommandErrorEventData eventData)
        {
            var queryEvent = CreateQueryEvent(eventData.Command, eventData.Duration, result: null, eventData.Context, error: eventData.Exception);
            _store.Add(queryEvent);

            // Don't record errors to history (they'd skew metrics)
        }
    }

    private async Task RecordToHistoryAsync(QueryEvent queryEvent)
    {
        if (_historyStore is null)
        {
            return;
        }

        try
        {
            await _historyStore.RecordExecutionAsync(
                queryEvent.PatternHash,
                queryEvent.NormalizedSql,
                queryEvent.Duration.TotalMilliseconds);
        }
        catch
        {
            // Ignore history recording errors - don't impact query execution
        }
    }

    private QueryEvent CreateQueryEvent(DbCommand command, TimeSpan duration, object? result, Microsoft.EntityFrameworkCore.DbContext? dbContext, Exception? error = null)
    {
        var httpContext = _httpContextAccessor.HttpContext;

        var parameters = new Dictionary<string, object?>();
        foreach (DbParameter param in command.Parameters)
        {
            parameters[param.ParameterName] = param.Value == DBNull.Value ? null : param.Value;
        }

        int? rowsAffected = result switch
        {
            int rows => rows,
            DbDataReader reader => reader.RecordsAffected >= 0 ? reader.RecordsAffected : null,
            _ => null
        };

        // Capture stack trace, filtering to user code only
        var (callSite, callingMethod, stackTrace) = CaptureStackTrace();

        // Extract provider name and connection string from DbContext
        string? providerName = null;
        string? connectionString = null;

        if (dbContext is not null)
        {
            try
            {
                var database = dbContext.Database;
                providerName = database.ProviderName;

                // Try multiple ways to get the connection string
                // 1. From the command's connection (may be empty after open for security)
                connectionString = command.Connection?.ConnectionString;

                // 2. If still empty, try from the connection directly
                if (string.IsNullOrEmpty(connectionString) && command.Connection is not null)
                {
                    // Some providers expose DataSource for file-based DBs
                    var conn = command.Connection;
                    if (conn.GetType().Name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
                    {
                        // For SQLite, reconstruct from DataSource
                        var dataSource = conn.DataSource;
                        if (!string.IsNullOrEmpty(dataSource))
                        {
                            connectionString = $"Data Source={dataSource}";
                        }
                    }
                }
            }
            catch
            {
                // Ignore errors extracting provider info
            }
        }

        return new QueryEvent
        {
            Sql = command.CommandText,
            Parameters = parameters,
            Duration = duration,
            RowsAffected = rowsAffected,
            RequestPath = httpContext?.Request.Path.Value,
            RequestId = httpContext?.TraceIdentifier,
            HttpMethod = httpContext?.Request.Method,
            CommandType = command.CommandType.ToString(),
            IsError = error is not null,
            ErrorMessage = error?.Message,
            CallSite = callSite,
            CallingMethod = callingMethod,
            StackTrace = stackTrace,
            ProviderName = providerName,
            ConnectionString = connectionString
        };
    }

    /// <summary>
    /// Captures the current stack trace, filtering out framework and EF Core internals.
    /// Returns the primary call site, method name, and abbreviated stack trace.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (string? CallSite, string? CallingMethod, List<string> StackTrace) CaptureStackTrace()
    {
        var stackTrace = new StackTrace(fNeedFileInfo: true);
        var frames = stackTrace.GetFrames();
        var userFrames = new List<string>();
        string? callSite = null;
        string? callingMethod = null;

        foreach (var frame in frames)
        {
            var method = frame.GetMethod();
            if (method is null) continue;

            var declaringType = method.DeclaringType;
            if (declaringType is null) continue;

            var ns = declaringType.Namespace ?? string.Empty;

            // Skip framework and EF Core internals
            if (IsFrameworkNamespace(ns)) continue;

            var fileName = frame.GetFileName();
            var lineNumber = frame.GetFileLineNumber();
            var methodName = $"{declaringType.Name}.{method.Name}";

            // Build frame description
            string frameDesc;
            if (!string.IsNullOrEmpty(fileName) && lineNumber > 0)
            {
                var shortFileName = Path.GetFileName(fileName);
                frameDesc = $"{methodName} ({shortFileName}:{lineNumber})";

                // First user frame with file info becomes the primary call site
                callSite ??= $"{shortFileName}:{lineNumber}";
                callingMethod ??= methodName;
            }
            else
            {
                frameDesc = methodName;
                callingMethod ??= methodName;
            }

            userFrames.Add(frameDesc);

            // Limit to 10 frames to avoid bloat
            if (userFrames.Count >= 10) break;
        }

        return (callSite, callingMethod, userFrames);
    }

    private static bool IsFrameworkNamespace(string ns)
    {
        return ns.StartsWith("System", StringComparison.Ordinal) ||
               ns.StartsWith("Microsoft", StringComparison.Ordinal) ||
               ns.StartsWith("Npgsql", StringComparison.Ordinal) ||
               ns.StartsWith("MySql", StringComparison.Ordinal) ||
               ns.StartsWith("Oracle", StringComparison.Ordinal) ||
               ns.StartsWith("EFCore.Insight", StringComparison.Ordinal) ||
               ns.StartsWith("Lambda", StringComparison.Ordinal);
    }

    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error) { }
    void IObserver<KeyValuePair<string, object?>>.OnCompleted() { }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
        _subscriptions.Clear();
    }
}
