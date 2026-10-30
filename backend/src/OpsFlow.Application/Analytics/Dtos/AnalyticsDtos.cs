using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Analytics;

/// <summary>
/// Shared filter of Dashboard and Reports. <c>From</c>/<c>To</c> are INCLUSIVE calendar days in <see cref="AnalyticsRange.TimeZoneName"/>;
/// internally the range is start-inclusive / end-exclusive. Null dates default to the last 30 days including today.
/// </summary>
public sealed record AnalyticsFilter(
    DateOnly? From,
    DateOnly? To,
    TicketPriority? Priority,
    Guid? CategoryId,
    TicketStatus? Status,
    Guid? AgentMembershipId,
    Guid? CustomerId
);

public sealed record CountByDto(string Key, int Count);

public sealed record TrendPointDto(
    DateTime PeriodStartUtc,
    int Created,
    int Resolved,
    double? AvgResolutionMinutes
);

/// <summary>Cards that describe the CURRENT state; they never depend on the selected date range.</summary>
public sealed record CurrentStateDto(
    int OpenTickets,
    int UnassignedTickets,
    int OverdueTickets,
    int ResolvedToday
);

public sealed record SlaComplianceDto(
    int EligibleCycles,
    int Met,
    int Breached,
    double? CompliancePercent
);

public sealed record WorkloadDto(Guid MembershipId, string DisplayName, int ActiveTickets);

public sealed record DashboardDto(
    DateTime GeneratedAtUtc,
    string TimeZone,
    DateTime FromUtc,
    DateTime ToUtc,
    string Grouping,
    CurrentStateDto Current,
    int CreatedInPeriod,
    int ResolvedInPeriod,
    double? AvgFirstResponseMinutes,
    int FirstResponseSample,
    SlaComplianceDto? Sla,
    IReadOnlyList<TrendPointDto> Trend,
    IReadOnlyList<CountByDto> OpenByStatus,
    IReadOnlyList<CountByDto> ByPriority,
    IReadOnlyList<WorkloadDto>? Workload
);

public sealed record ResolutionReportDto(
    int CompletedCycles,
    double? AvgMinutes,
    double? MedianMinutes
);

public sealed record ReopenReportDto(
    int ResolvedCycles,
    int ReopenedCycles,
    double? ReopenRatePercent,
    int ReopenEventsInPeriod
);

public sealed record AgentPerformanceDto(
    Guid MembershipId,
    string DisplayName,
    int CurrentWorkload,
    int ResolvedInPeriod
);

public sealed record CategoryShareDto(Guid CategoryId, string Name, int Count, double SharePercent);

public sealed record CustomerAnalysisDto(
    Guid CustomerId,
    string Name,
    int Tickets,
    int Open,
    int Resolved,
    int OverdueOpen
);

public sealed record ReportDto(
    DateTime GeneratedAtUtc,
    string TimeZone,
    DateTime FromUtc,
    DateTime ToUtc,
    string Grouping,
    int TicketsCreated,
    IReadOnlyList<TrendPointDto> Trend,
    IReadOnlyList<CountByDto> ByStatus,
    IReadOnlyList<CountByDto> ByPriority,
    ResolutionReportDto Resolution,
    SlaComplianceDto Sla,
    int OpenOverdueTickets,
    ReopenReportDto Reopen,
    IReadOnlyList<AgentPerformanceDto> Agents,
    IReadOnlyList<CategoryShareDto> Categories,
    IReadOnlyList<CustomerAnalysisDto> Customers
);

/// <summary>A CSV to stream: headers are known up front, rows are written straight from the database reader (no full buffering).</summary>
public sealed record CsvExport(string FileName, bool Truncated, Func<Stream, CancellationToken, Task> WriteAsync);

/// <summary>The resolved reporting window: UTC instants, start-inclusive / end-exclusive.</summary>
public sealed record AnalyticsRange(DateTime FromUtc, DateTime ToUtc, bool Monthly)
{
    /// <summary>OpsFlow stores no per-tenant timezone yet, so reporting days are UTC days; exposed so the UI can say so.</summary>
    public const string TimeZoneName = "UTC";

    public const int MaxDays = 366;
    public const int MonthlyAfterDays = 92;
}
