namespace OpsFlow.Api.GraphQL;

/// <summary>Structured GraphQL diagnostics: field names, durations and error classes only (never queries, variables, tokens or message content).</summary>
internal static partial class GraphQlLog
{
    public const int SlowFieldMilliseconds = 1000;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Slow GraphQL field {Field} took {ElapsedMs} ms (tenant {TenantId})")]
    public static partial void SlowField(ILogger logger, string field, long elapsedMs, Guid? tenantId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected GraphQL resolver failure in {Field}")]
    public static partial void Unexpected(ILogger logger, Exception exception, string? field);
}

/// <summary>
/// HotChocolate builds error filters in its own schema service provider, which cannot see ILoggerFactory, so the logger is handed over once
/// at endpoint mapping time (set once at startup, read-only afterwards).
/// </summary>
internal static class GraphQlDiagnostics
{
    public static ILogger Logger { get; set; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
}
