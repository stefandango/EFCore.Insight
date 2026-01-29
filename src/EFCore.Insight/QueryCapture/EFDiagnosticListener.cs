using System.Data.Common;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EFCore.Insight.QueryCapture;

/// <summary>
/// Diagnostic listener that captures EF Core query execution events.
/// </summary>
public sealed class EFDiagnosticListener(QueryStore store, IHttpContextAccessor httpContextAccessor) : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly QueryStore _store = store;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly List<IDisposable> _subscriptions = [];

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
            var queryEvent = CreateQueryEvent(eventData.Command, eventData.Duration, eventData.Result);
            _store.Add(queryEvent);
        }
    }

    private void HandleCommandError(object? value)
    {
        if (value is CommandErrorEventData eventData)
        {
            var queryEvent = CreateQueryEvent(eventData.Command, eventData.Duration, result: null, error: eventData.Exception);
            _store.Add(queryEvent);
        }
    }

    private QueryEvent CreateQueryEvent(DbCommand command, TimeSpan duration, object? result, Exception? error = null)
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

        return new QueryEvent
        {
            Sql = command.CommandText,
            Parameters = parameters,
            Duration = duration,
            RowsAffected = rowsAffected,
            RequestPath = httpContext?.Request.Path.Value,
            RequestId = httpContext?.TraceIdentifier,
            CommandType = command.CommandType.ToString(),
            IsError = error is not null,
            ErrorMessage = error?.Message
        };
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
