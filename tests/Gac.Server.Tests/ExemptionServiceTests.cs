using Gac.Server.Accounts;
using Gac.Server.Calendar;
using Gac.Server.Data;
using Gac.Shared;
using Gac.Shared.Auth;
using Gac.Shared.Contracts;

namespace Gac.Server.Tests;

public sealed class ExemptionServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gac-ex-" + Guid.NewGuid().ToString("N"));
    private readonly Db _db;
    private readonly FakeTime _time = new();
    private readonly ExemptionService _svc;
    private readonly Session _owner = new(1, "owner", "교장", Role.Owner, [], RolePermissions.Default(Role.Owner));

    public ExemptionServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new Db(Path.Combine(_dir, "gac.db"));
        _db.EnsureSchema();
        _svc = new ExemptionService(_db, new AuditLog(_db, _time), _time);
    }

    [Fact]
    public void Save_skip_delete_round_trip_and_audit()
    {
        var d = _svc.Save(_owner, null, new SaveExemptionDayRequest(ExemptionCalendar.PresetName, ExemptionRecurrence.MonthlyNthWeekday, new DateOnly(2026, 1, 1), Nth: 3, Weekday: DayOfWeek.Wednesday));
        var loaded = Assert.Single(_svc.List());
        Assert.Equal(3, loaded.Nth);
        Assert.Equal(DayOfWeek.Wednesday, loaded.Weekday);
        _svc.Skip(_owner, d.Id, new DateOnly(2026, 12, 16));
        Assert.Contains(new DateOnly(2026, 12, 16), _svc.List()[0].Skipped);
        _svc.Save(_owner, d.Id, new SaveExemptionDayRequest("점검일", ExemptionRecurrence.MonthlyNthWeekday, new DateOnly(2026, 1, 1), Nth: 3, Weekday: DayOfWeek.Wednesday));
        Assert.Equal("점검일", _svc.List()[0].Name);
        Assert.Contains(new DateOnly(2026, 12, 16), _svc.List()[0].Skipped); // edit keeps skipped occurrences
        _svc.Delete(_owner, d.Id);
        Assert.Empty(_svc.List());
        var actions = new AuditLog(_db, _time).Query(null, null, 100).Select(a => a.Action).ToList();
        foreach (var a in new[] { "calendar.exemption.add", "calendar.exemption.skip", "calendar.exemption.edit", "calendar.exemption.delete" }) Assert.Contains(a, actions);
    }

    [Fact]
    public void Agent_calendar_carries_server_time_and_next_sixty_days()
    {
        _svc.Save(_owner, null, new SaveExemptionDayRequest(ExemptionCalendar.PresetName, ExemptionRecurrence.MonthlyDay, new DateOnly(2020, 1, 1), DayOfMonth: 5));
        var cal = _svc.ForAgent();
        Assert.Equal(_time.GetUtcNow(), cal.ServerTime);
        var today = ExemptionCalendar.SeoulDate(_time.GetUtcNow());
        Assert.InRange(cal.ExemptDates.Count, 1, 3);
        Assert.All(cal.ExemptDates, d => { Assert.Equal(5, d.Date.Day); Assert.InRange(d.Date, today, today.AddDays(60)); });
        // round-trips through JSON as the agent stores it
        Assert.Equal(cal.ExemptDates.Count, GacJson.Deserialize<AgentCalendar>(GacJson.Serialize(cal))!.ExemptDates.Count);
    }

    [Fact]
    public void Invalid_request_is_rejected()
    {
        Assert.Throws<GacException>(() => _svc.Save(_owner, null, new SaveExemptionDayRequest(" ", ExemptionRecurrence.Once, new DateOnly(2026, 1, 1))));
        Assert.Throws<GacException>(() => _svc.Delete(_owner, "missing"));
    }

    [Fact]
    public void Old_saved_matrix_still_grants_new_permissions_to_admin_by_default()
    {
        // A matrix saved before CHANGE-002 has no Known list and no entries for the new permissions.
        var old = new PermissionMatrix { Roles = new() { [Role.Admin] = [Permission.ViewPcs, Permission.RunModules], [Role.Operator] = [Permission.ViewPcs] } };
        var restored = GacJson.Deserialize<PermissionMatrix>(GacJson.Serialize(old))!;
        Assert.Contains(Permission.ManageCalendar, restored.Effective(Role.Admin));
        Assert.Contains(Permission.ManageServerShutdown, restored.Effective(Role.Admin));
        Assert.DoesNotContain(Permission.ManageCalendar, restored.Effective(Role.Operator));
        // once the Owner edits it, the explicit choice wins
        restored.Set(Role.Admin, Permission.ManageCalendar, false);
        Assert.DoesNotContain(Permission.ManageCalendar, restored.Effective(Role.Admin));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
