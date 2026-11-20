using Ember.Core.Rules;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Tests;

public class TimeZonesTests
{
    private static readonly TimeZoneInfo Amsterdam =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    private static readonly TimeZoneInfo NewYork =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static readonly TimeZoneInfo Tokyo =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    // ── Find ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Find_ValidIanaId_ReturnsTimeZoneInfo()
    {
        TimeZones.Find("Europe/Amsterdam").Should().NotBeNull();
    }

    [Fact]
    public void Find_InvalidId_ReturnsNull()
    {
        TimeZones.Find("Not/ATimezone").Should().BeNull();
    }

    // ── ToUtc ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ToUtc_UtcTimezone_ReturnsSameTime()
    {
        var time = new TimeOnly(10, 0);
        TimeZones.ToUtc(time, TimeZoneInfo.Utc).Should().Be(time);
    }

    [Fact]
    public void ToUtc_AmsterdamSummerTime_SubtractsTwoHours()
    {
        // Amsterdam is UTC+2 in summer (CEST)
        // This test is date-dependent for DST, so we compute expected dynamically
        var localTime = new TimeOnly(10, 0);
        var utcTime = TimeZones.ToUtc(localTime, Amsterdam);

        var today = DateTime.UtcNow.Date;
        var localDt = DateTime.SpecifyKind(today.Add(localTime.ToTimeSpan()), DateTimeKind.Unspecified);
        var expected = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeToUtc(localDt, Amsterdam));

        utcTime.Should().Be(expected);
    }

    [Fact]
    public void ToUtc_NewYork_ConvertsCorrectly()
    {
        var localTime = new TimeOnly(9, 30);
        var utcTime = TimeZones.ToUtc(localTime, NewYork);

        var today = DateTime.UtcNow.Date;
        var localDt = DateTime.SpecifyKind(today.Add(localTime.ToTimeSpan()), DateTimeKind.Unspecified);
        var expected = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeToUtc(localDt, NewYork));

        utcTime.Should().Be(expected);
    }

    [Fact]
    public void ToUtc_Tokyo_ConvertsCorrectly()
    {
        // Tokyo is UTC+9, no DST
        var localTime = new TimeOnly(18, 0);
        var utcTime = TimeZones.ToUtc(localTime, Tokyo);
        var expected = new TimeOnly(9, 0);
        utcTime.Should().Be(expected);
    }

    [Fact]
    public void ToUtc_TimeInSpringForwardGap_UsesFirstValidTimeInsteadOfThrowing()
    {
        TimeZones.ToUtc(new TimeOnly(2, 30), Berlin, new DateOnly(2027, 3, 28))
            .Should().Be(new TimeOnly(1, 0));
    }

    // ── NextOccurrenceUtc ─────────────────────────────────────────────────────

    private static readonly TimeZoneInfo Berlin =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static DateTime Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void NextOccurrenceUtc_NormalDay_ReturnsTodaysLocalTimeWhenStillAhead()
    {
        TimeZones.NextOccurrenceUtc(new TimeOnly(9, 0), Berlin, Utc(2027, 1, 15, 6, 0))
            .Should().Be(Utc(2027, 1, 15, 8, 0));
    }

    [Fact]
    public void NextOccurrenceUtc_IsStrictlyAfter_SoTheSameInstantMovesToTomorrow()
    {
        TimeZones.NextOccurrenceUtc(new TimeOnly(9, 0), Berlin, Utc(2027, 1, 15, 8, 0))
            .Should().Be(Utc(2027, 1, 16, 8, 0));
    }

    [Fact]
    public void NextOccurrenceUtc_SpringForwardGap_MovesToFirstValidTimeAfterGap()
    {
        // 2027-03-28 02:00–03:00 does not exist in Berlin; 03:00 CEST is 01:00 UTC.
        TimeZones.NextOccurrenceUtc(new TimeOnly(2, 30), Berlin, Utc(2027, 3, 27, 12, 0))
            .Should().Be(Utc(2027, 3, 28, 1, 0));
    }

    [Fact]
    public void NextOccurrenceUtc_DayAfterSpringForward_UsesSummerOffset()
    {
        TimeZones.NextOccurrenceUtc(new TimeOnly(2, 30), Berlin, Utc(2027, 3, 28, 1, 0))
            .Should().Be(Utc(2027, 3, 29, 0, 30));
    }

    [Fact]
    public void NextOccurrenceUtc_FallBackAmbiguity_UsesFirstOccurrence()
    {
        // 02:30 happens twice on 2027-10-31 in Berlin: 00:30 UTC (CEST) and 01:30 UTC (CET).
        TimeZones.NextOccurrenceUtc(new TimeOnly(2, 30), Berlin, Utc(2027, 10, 30, 12, 0))
            .Should().Be(Utc(2027, 10, 31, 0, 30));
    }

    [Fact]
    public void NextOccurrenceUtc_BetweenBothAmbiguousOccurrences_DoesNotFireTwice()
    {
        TimeZones.NextOccurrenceUtc(new TimeOnly(2, 30), Berlin, Utc(2027, 10, 31, 1, 0))
            .Should().Be(Utc(2027, 11, 1, 1, 30));
    }

    [Fact]
    public void NextOccurrenceUtc_HalfHourZone_AppliesHalfHourOffset()
    {
        var kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        TimeZones.NextOccurrenceUtc(new TimeOnly(8, 0), kolkata, Utc(2027, 1, 15, 0, 0))
            .Should().Be(Utc(2027, 1, 15, 2, 30));
    }

    public static TheoryData<string, int, int, int, int, int, int, int, int> LocalDays => new()
    {
        { "Asia/Kolkata", 2027, 1, 14, 18, 29, 59, 1, 14 },
        { "Asia/Kolkata", 2027, 1, 14, 18, 30, 0, 1, 15 },
        { "America/St_Johns", 2027, 1, 15, 3, 29, 59, 1, 14 },
        { "America/St_Johns", 2027, 1, 15, 3, 30, 0, 1, 15 },
        { "Australia/Lord_Howe", 2027, 1, 10, 12, 59, 59, 1, 10 },
        { "Australia/Lord_Howe", 2027, 1, 10, 13, 0, 0, 1, 11 },
        { "Australia/Lord_Howe", 2027, 7, 10, 13, 29, 59, 7, 10 },
        { "Australia/Lord_Howe", 2027, 7, 10, 13, 30, 0, 7, 11 },
        { "America/Havana", 2026, 11, 1, 3, 59, 59, 10, 31 },
        { "America/Havana", 2026, 11, 1, 4, 0, 0, 11, 1 },
        { "America/Havana", 2026, 11, 1, 5, 30, 0, 11, 1 },
        { "America/Havana", 2026, 3, 8, 4, 59, 59, 3, 7 },
        { "America/Havana", 2026, 3, 8, 5, 0, 0, 3, 8 },
    };

    [Theory]
    [MemberData(nameof(LocalDays))]
    public void LocalToday_FollowsHalfHourOffsetsAndDaylightSavingEdges(
        string zone, int year, int month, int day, int hour, int minute, int second, int expectedMonth, int expectedDay)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero));

        TimeZones.LocalToday(zone, time).Should().Be(new DateOnly(year, expectedMonth, expectedDay));
    }
}
