using OpsFlow.Application.Analytics;
using OpsFlow.Application.Common;

namespace OpsFlow.Tests.Unit;

public class ReportingCalendarTests
{
    private static readonly TimeZoneInfo Rome = ReportingCalendar.Resolve("Europe/Rome");
    private static readonly TimeZoneInfo Karachi = ReportingCalendar.Resolve("Asia/Karachi");

    private static double Hours(DateOnly day, TimeZoneInfo tz) => (ReportingCalendar.LocalDayStartUtc(day.AddDays(1), tz) - ReportingCalendar.LocalDayStartUtc(day, tz)).TotalHours;

    [Fact]
    public void Rome_spring_forward_day_has_23_hours_and_autumn_day_25()
    {
        Assert.Equal(23, Hours(new DateOnly(2026, 3, 29), Rome));
        Assert.Equal(25, Hours(new DateOnly(2026, 10, 25), Rome));
        Assert.Equal(24, Hours(new DateOnly(2026, 7, 1), Rome));
    }

    [Fact]
    public void Local_midnight_is_converted_per_boundary_not_by_adding_24_hours()
    {
        // 2026-03-29 starts at 23:00Z on the 28th (CET, +1); 2026-03-30 starts at 22:00Z on the 29th (CEST, +2).
        Assert.Equal(new DateTime(2026, 3, 28, 23, 0, 0, DateTimeKind.Utc), ReportingCalendar.LocalDayStartUtc(new DateOnly(2026, 3, 29), Rome));
        Assert.Equal(new DateTime(2026, 3, 29, 22, 0, 0, DateTimeKind.Utc), ReportingCalendar.LocalDayStartUtc(new DateOnly(2026, 3, 30), Rome));
    }

    [Fact]
    public void Utc_and_fixed_offset_zones_have_24_hour_days()
    {
        Assert.Equal(24, Hours(new DateOnly(2026, 3, 29), TimeZoneInfo.Utc));
        Assert.Equal(new DateTime(2026, 5, 31, 19, 0, 0, DateTimeKind.Utc), ReportingCalendar.LocalDayStartUtc(new DateOnly(2026, 6, 1), Karachi));
    }

    [Fact]
    public void Range_is_start_inclusive_end_exclusive_across_month_and_year_boundaries()
    {
        var (from, to) = ReportingCalendar.ToUtcRange(new DateOnly(2026, 12, 31), new DateOnly(2027, 1, 1), Rome);
        Assert.Equal(new DateTime(2026, 12, 30, 23, 0, 0, DateTimeKind.Utc), from);
        Assert.Equal(new DateTime(2027, 1, 1, 23, 0, 0, DateTimeKind.Utc), to); // two local days, end exclusive
        Assert.Equal(new DateOnly(2026, 12, 31), ReportingCalendar.LocalDate(from, Rome));
        Assert.Equal(new DateOnly(2027, 1, 1), ReportingCalendar.LocalDate(to.AddTicks(-1), Rome));
    }

    [Fact]
    public void Cross_midnight_instants_land_in_the_local_day()
    {
        var lateUtc = new DateTime(2026, 6, 1, 22, 30, 0, DateTimeKind.Utc); // 00:30 on June 2 in Rome (CEST), 03:30 in Karachi
        Assert.Equal(new DateOnly(2026, 6, 2), ReportingCalendar.LocalDate(lateUtc, Rome));
        Assert.Equal(new DateOnly(2026, 6, 2), ReportingCalendar.LocalDate(lateUtc, Karachi));
        Assert.Equal(new DateOnly(2026, 6, 1), ReportingCalendar.LocalDate(lateUtc, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Segments_split_the_range_exactly_at_the_dst_change()
    {
        var (from, to) = ReportingCalendar.ToUtcRange(new DateOnly(2026, 3, 27), new DateOnly(2026, 3, 31), Rome);
        var segments = ReportingCalendar.Segments(from, to, Rome);

        Assert.Equal(2, segments.Count);
        Assert.Equal(TimeSpan.FromHours(1), segments[0].Offset);
        Assert.Equal(TimeSpan.FromHours(2), segments[1].Offset);
        Assert.Equal(new DateTime(2026, 3, 29, 1, 0, 0, DateTimeKind.Utc), segments[0].EndUtc); // 02:00 CET == 01:00Z
        Assert.Equal(segments[0].EndUtc, segments[1].StartUtc);
        Assert.Equal(from, segments[0].StartUtc);
        Assert.Equal(to, segments[1].EndUtc);
    }

    [Fact]
    public void A_range_without_a_transition_is_one_segment()
    {
        var (from, to) = ReportingCalendar.ToUtcRange(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), Rome);
        Assert.Single(ReportingCalendar.Segments(from, to, Rome));
    }

    [Fact]
    public void Unknown_timezone_ids_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => ReportingCalendar.Resolve("Mars/Olympus_Mons"));
        Assert.Throws<ArgumentException>(() => ReportingCalendar.Resolve(""));
    }

    [Fact]
    public void Range_resolution_validates_order_and_length_in_the_tenant_zone()
    {
        var now = new DateTime(2026, 6, 15, 23, 30, 0, DateTimeKind.Utc); // already June 16 in Rome
        var range = AnalyticsService.ResolveRange(new AnalyticsFilter(null, null, null, null, null, null, null), now, Rome);
        Assert.Equal(ReportingCalendar.LocalDayStartUtc(new DateOnly(2026, 6, 16).AddDays(1), Rome), range.ToUtc);
        Assert.False(range.Monthly);
        Assert.Equal(400, Assert.Throws<AppException>(() => AnalyticsService.ResolveRange(new AnalyticsFilter(new DateOnly(2026, 2, 2), new DateOnly(2026, 2, 1), null, null, null, null, null), now, Rome)).StatusCode);
        Assert.Throws<AppException>(() => AnalyticsService.ResolveRange(new AnalyticsFilter(new DateOnly(2025, 1, 1), new DateOnly(2026, 6, 1), null, null, null, null, null), now, Rome));
        Assert.True(AnalyticsService.ResolveRange(new AnalyticsFilter(new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 1), null, null, null, null, null), now, Rome).Monthly);
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("  =cmd", "'  =cmd")]
    [InlineData("\u0001@SUM(1)", "'\u0001@SUM(1)")]
    [InlineData("＝1", "'＝1")]
    [InlineData("	+1", "'	+1")]
    [InlineData("plain - text", "plain - text")]
    [InlineData("", "")]
    public void Csv_formula_triggers_are_neutralised_even_after_leading_blanks(string input, string expected) => Assert.Equal(expected, CsvWriter.Cell(input));

    [Fact]
    public void Csv_non_text_values_are_not_treated_as_formulas()
    {
        Assert.Equal("-5", CsvWriter.Cell(-5));
        Assert.Equal("1.5", CsvWriter.Cell(1.5));
        Assert.Equal(string.Empty, CsvWriter.Cell(null));
        Assert.Equal("2026-03-29T01:00:00Z", CsvWriter.Cell(new DateTime(2026, 3, 29, 1, 0, 0, DateTimeKind.Utc)));
    }
}
