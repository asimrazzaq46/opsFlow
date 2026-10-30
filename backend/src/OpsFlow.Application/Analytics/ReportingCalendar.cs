namespace OpsFlow.Application.Analytics;

/// <summary>
/// The ONE authoritative reporting timezone of the current tenant (day buckets, "resolved today", report ranges).
/// It is never taken from a request/browser header; see TenantReportingTimeZoneProvider (UTC for tenants without a setting).
/// </summary>
public interface IReportingTimeZoneProvider
{
    Task<TimeZoneInfo> GetAsync(CancellationToken ct);
}

/// <summary>A UTC interval with one constant UTC offset, so "local day = (utc + offset).Date" holds for every instant inside it.</summary>
public sealed record OffsetSegment(DateTime StartUtc, DateTime EndUtc, TimeSpan Offset);

/// <summary>DST-safe conversion between tenant-local calendar days and UTC instants (start-inclusive / end-exclusive).</summary>
public static class ReportingCalendar
{
    /// <summary>Resolves an IANA (or Windows) id; unknown ids are rejected instead of falling back silently.</summary>
    public static TimeZoneInfo Resolve(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            throw new ArgumentException($"'{id}' is not a valid IANA time zone.", nameof(id), ex);
        }
    }

    /// <summary>UTC instant at which the local calendar day starts. Each boundary is converted on its own (a day can have 23, 24 or 25 hours).</summary>
    public static DateTime LocalDayStartUtc(DateOnly day, TimeZoneInfo tz)
    {
        var local = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        while (tz.IsInvalidTime(local))
        {
            local = local.AddMinutes(30); // midnight skipped by a DST jump: the day starts at the first valid local time
        }

        var offset = tz.IsAmbiguousTime(local) ? tz.GetAmbiguousTimeOffsets(local).Max() : tz.GetUtcOffset(local); // ambiguous: the earlier instant
        return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
    }

    public static DateOnly LocalDate(DateTime utc, TimeZoneInfo tz) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz));

    public static (DateTime FromUtc, DateTime ToUtc) ToUtcRange(DateOnly fromInclusive, DateOnly toInclusive, TimeZoneInfo tz) =>
        (LocalDayStartUtc(fromInclusive, tz), LocalDayStartUtc(toInclusive.AddDays(1), tz));

    /// <summary>Splits [fromUtc,toUtc) at every UTC-offset change so each piece can be bucketed with a constant offset.</summary>
    public static IReadOnlyList<OffsetSegment> Segments(DateTime fromUtc, DateTime toUtc, TimeZoneInfo tz)
    {
        var segments = new List<OffsetSegment>();
        var start = fromUtc;
        var offset = tz.GetUtcOffset(fromUtc);
        for (var probe = fromUtc.AddHours(12); probe < toUtc; probe = probe.AddHours(12))
        {
            var next = tz.GetUtcOffset(probe);
            if (next == offset)
            {
                continue;
            }

            var change = FindChange(probe.AddHours(-12), probe, offset, tz); // transitions are on whole minutes
            segments.Add(new OffsetSegment(start, change, offset));
            (start, offset) = (change, next);
        }

        segments.Add(new OffsetSegment(start, toUtc, offset));
        return segments;
    }

    // Bisects in whole minutes (transitions are minute-aligned); lo keeps the old offset, the result is the first minute with the new one.
    private static DateTime FindChange(DateTime lo, DateTime hi, TimeSpan before, TimeZoneInfo tz)
    {
        long loMin = lo.Ticks / TimeSpan.TicksPerMinute, hiMin = hi.Ticks / TimeSpan.TicksPerMinute + 1;
        while (hiMin - loMin > 1)
        {
            var mid = loMin + ((hiMin - loMin) / 2);
            if (tz.GetUtcOffset(new DateTime(mid * TimeSpan.TicksPerMinute, DateTimeKind.Utc)) == before)
            {
                loMin = mid;
            }
            else
            {
                hiMin = mid;
            }
        }

        return new DateTime(hiMin * TimeSpan.TicksPerMinute, DateTimeKind.Utc);
    }
}
