using Gac.Shared.Auth;
using Gac.Shared.Contracts;

namespace Gac.Shared.Tests;

public class CalendarTests
{
    private static ExemptionDay Day(ExemptionRecurrence r, DateOnly date, int? dom = null, int? nth = null, DayOfWeek? wd = null, params DateOnly[] skipped) =>
        new("x", ExemptionCalendar.PresetName, r, date, dom, nth, wd, skipped, "owner", DateTimeOffset.UnixEpoch);

    [Fact]
    public void Third_wednesday_every_month_expands_to_the_right_dates()
    {
        var d = Day(ExemptionRecurrence.MonthlyNthWeekday, new DateOnly(2026, 11, 1), nth: 3, wd: DayOfWeek.Wednesday);
        var dates = ExemptionCalendar.Expand(d, new DateOnly(2026, 11, 1), new DateOnly(2027, 2, 28)).ToList();
        Assert.Equal([new DateOnly(2026, 11, 18), new DateOnly(2026, 12, 16), new DateOnly(2027, 1, 20), new DateOnly(2027, 2, 17)], dates);
        Assert.All(dates, x => Assert.Equal(DayOfWeek.Wednesday, x.DayOfWeek));
    }

    [Fact]
    public void Skip_this_occurrence_removes_only_that_date()
    {
        var d = Day(ExemptionRecurrence.MonthlyNthWeekday, new DateOnly(2026, 11, 1), nth: 3, wd: DayOfWeek.Wednesday, skipped: new DateOnly(2026, 12, 16));
        var dates = ExemptionCalendar.Expand(d, new DateOnly(2026, 11, 1), new DateOnly(2027, 1, 31)).ToList();
        Assert.Equal([new DateOnly(2026, 11, 18), new DateOnly(2027, 1, 20)], dates);
    }

    [Fact]
    public void Last_weekday_and_monthly_day_and_once()
    {
        var last = Day(ExemptionRecurrence.MonthlyNthWeekday, new DateOnly(2026, 1, 1), nth: 5, wd: DayOfWeek.Friday);
        Assert.Equal(new DateOnly(2026, 10, 30), ExemptionCalendar.Occurrence(last, 2026, 10));
        var dom31 = Day(ExemptionRecurrence.MonthlyDay, new DateOnly(2026, 1, 1), dom: 31);
        Assert.Null(ExemptionCalendar.Occurrence(dom31, 2026, 11)); // November has 30 days
        Assert.Equal(new DateOnly(2026, 12, 31), ExemptionCalendar.Occurrence(dom31, 2026, 12));
        var once = Day(ExemptionRecurrence.Once, new DateOnly(2026, 11, 20));
        Assert.Single(ExemptionCalendar.Expand(once, new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 31)));
        Assert.Empty(ExemptionCalendar.Expand(once, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31)));
    }

    [Fact]
    public void Recurrence_does_not_apply_before_its_start_date()
    {
        var d = Day(ExemptionRecurrence.MonthlyDay, new DateOnly(2026, 11, 20), dom: 5);
        Assert.Equal(new DateOnly(2026, 12, 5), ExemptionCalendar.Expand(d, new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 31)).Single());
    }

    [Fact]
    public void Exemption_day_runs_midnight_to_midnight_in_seoul()
    {
        var dates = new[] { new ExemptionDate(new DateOnly(2026, 11, 18), ExemptionCalendar.PresetName, "x") };
        // 2026-11-17 15:00 UTC = 2026-11-18 00:00 KST (start of the day)
        Assert.True(ExemptionCalendar.IsExempt(dates, new DateTimeOffset(2026, 11, 17, 15, 0, 0, TimeSpan.Zero)));
        Assert.False(ExemptionCalendar.IsExempt(dates, new DateTimeOffset(2026, 11, 17, 14, 59, 59, TimeSpan.Zero)));
        // 2026-11-18 14:59:59 UTC = 23:59:59 KST (still exempt); one second later the next day starts
        Assert.True(ExemptionCalendar.IsExempt(dates, new DateTimeOffset(2026, 11, 18, 14, 59, 59, TimeSpan.Zero)));
        Assert.False(ExemptionCalendar.IsExempt(dates, new DateTimeOffset(2026, 11, 18, 15, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("", ExemptionRecurrence.Once, null, null, false)]
    [InlineData("점검", ExemptionRecurrence.MonthlyDay, 0, null, false)]
    [InlineData("점검", ExemptionRecurrence.MonthlyNthWeekday, null, 3, true)]
    [InlineData("점검", ExemptionRecurrence.MonthlyDay, 15, null, true)]
    public void Validation(string name, ExemptionRecurrence r, int? dom, int? nth, bool ok)
    {
        var req = new SaveExemptionDayRequest(name, r, new DateOnly(2026, 11, 1), dom, nth, nth == null ? null : DayOfWeek.Wednesday);
        Assert.Equal(ok, ExemptionCalendar.Validate(req) == null);
    }

    [Fact]
    public void New_permissions_default_to_owner_and_admin_and_are_editable()
    {
        foreach (var p in new[] { Permission.ManageCalendar, Permission.ManageServerShutdown })
        {
            Assert.True(RolePermissions.Has(Role.Owner, p));
            Assert.True(RolePermissions.Has(Role.Admin, p));
            Assert.False(RolePermissions.Has(Role.Operator, p));
            Assert.False(RolePermissions.Has(Role.Viewer, p));
            Assert.Contains(p, RolePermissions.Editable);
        }
        Assert.False(RolePermissions.Has(Role.Admin, Permission.ManageServerSettings)); // 서버 설정: Owner only
    }
}

public class OverloadSettingsTests
{
    [Fact]
    public void Block_doubles_for_repeat_offenders_up_to_the_maximum()
    {
        var s = new OverloadSettings();
        Assert.Equal(TimeSpan.FromMinutes(10), s.BlockFor(1));
        Assert.Equal(TimeSpan.FromMinutes(20), s.BlockFor(2));
        Assert.Equal(TimeSpan.FromMinutes(40), s.BlockFor(3));
        Assert.Equal(TimeSpan.FromHours(24), s.BlockFor(20));
        Assert.Null(OverloadSettings.Validate(s));
        Assert.NotNull(OverloadSettings.Validate(s with { MaxRequestsPerAgentPerMinute = 0 }));
    }

    [Fact]
    public void Server_shutdown_schedule_skips_off_days_and_exception_dates()
    {
        var s = ServerShutdownSettings.Default() with { Enabled = true };
        var monday = new DateOnly(2026, 11, 2);
        Assert.Equal(monday.ToDateTime(new TimeOnly(18, 0)), s.ScheduledAt(monday));
        Assert.Null(s.ScheduledAt(new DateOnly(2026, 11, 1))); // Sunday off
        Assert.Null((s with { ExceptionDates = [monday] }).ScheduledAt(monday));
        Assert.Null((s with { Enabled = false }).ScheduledAt(monday));
    }
}

public class TrustedClockTests
{
    private long _tick;
    private DateTimeOffset _wall = new(2026, 11, 17, 1, 0, 0, TimeSpan.Zero);

    private TrustedClock Clock() => new(() => _tick, () => _wall);

    [Fact]
    public void Online_uses_server_time_even_if_local_clock_is_set_to_an_exemption_day()
    {
        var c = Clock();
        var server = new DateTimeOffset(2026, 11, 17, 1, 0, 0, TimeSpan.Zero); // 17th KST: not exempt
        c.OnServerTime(server);
        _wall = new DateTimeOffset(2026, 11, 18, 3, 0, 0, TimeSpan.Zero); // student moved the clock to the 18th
        var (now, ignored) = c.Now();
        Assert.True(ignored);
        Assert.Equal(server, now);
        var dates = new[] { new ExemptionDate(new DateOnly(2026, 11, 18), ExemptionCalendar.PresetName, "x") };
        Assert.False(ExemptionCalendar.IsExempt(dates, now)); // lock still runs
    }

    [Fact]
    public void Offline_estimate_uses_monotonic_time_not_the_wall_clock()
    {
        var c = Clock();
        var server = new DateTimeOffset(2026, 11, 17, 1, 0, 0, TimeSpan.Zero);
        c.OnServerTime(server);
        _tick += (long)TimeSpan.FromHours(5).TotalMilliseconds; // 5 hours pass (server now unreachable)
        _wall = server.AddDays(2); // wall clock tampered
        var (now, ignored) = c.Now();
        Assert.True(ignored);
        Assert.Equal(server.AddHours(5), now);
    }

    [Fact]
    public void Small_drift_is_accepted_without_a_tamper_flag()
    {
        var c = Clock();
        c.OnServerTime(_wall);
        _tick += 60_000;
        _wall = _wall.AddMinutes(1).AddMinutes(5); // 5 min drift < 10 min tolerance
        var (_, ignored) = c.Now();
        Assert.False(ignored);
    }

    [Fact]
    public void Restored_anchor_rejects_a_wall_clock_far_from_the_last_server_time()
    {
        var c = Clock();
        var last = new DateTimeOffset(2026, 11, 17, 1, 0, 0, TimeSpan.Zero);
        c.Restore(last);
        _wall = last.AddDays(3);
        Assert.Equal((last, true), c.Now());
        _wall = last.AddHours(2); // normal overnight gap, plausible
        Assert.Equal((_wall, false), c.Now());
    }
}
