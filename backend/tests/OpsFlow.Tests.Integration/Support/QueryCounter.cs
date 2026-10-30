using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace OpsFlow.Tests.Integration.Support;

public sealed record CapturedCommand(string Sql, IReadOnlyList<DbParameter> Parameters);

/// <summary>
/// Counts every database command (and optionally keeps its SQL + parameters) so tests can assert query counts and
/// run EXPLAIN on exactly what EF sent. Measurements are only meaningful while one request runs at a time.
/// </summary>
public sealed class QueryCounter : DbCommandInterceptor
{
    private int _count;
    private readonly List<CapturedCommand> _captured = [];
    private readonly Lock _gate = new();

    public bool Capture { get; set; }

    public int Count => Volatile.Read(ref _count);

    public IReadOnlyList<CapturedCommand> Captured
    {
        get
        {
            lock (_gate)
            {
                return [.. _captured];
            }
        }
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _count, 0);
        lock (_gate)
        {
            _captured.Clear();
        }
    }

    /// <summary>Runs <paramref name="action"/> and returns how many commands it issued.</summary>
    public async Task<int> MeasureAsync(Func<Task> action)
    {
        Reset();
        await action();
        return Count;
    }

    private void Record(DbCommand command)
    {
        Interlocked.Increment(ref _count);
        if (!Capture)
        {
            return;
        }

        var copies = command.Parameters.Cast<DbParameter>().Select(p => p is NpgsqlParameter n ? (DbParameter)n.Clone() : p).ToList();
        lock (_gate)
        {
            _captured.Add(new CapturedCommand(command.CommandText, copies));
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return new(result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return new(result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return new(result);
    }
}
