using Gac.Agent.Runtime;
using Gac.Shared;
using Gac.Shared.ClassMode;
using Gac.Shared.Contracts;
using Gac.Shared.Modules;
using Gac.Shared.Pipe;

namespace Gac.Agent.Tests;

public class LockControllerTests
{
    private sealed class FakeTray : ITrayBridge
    {
        public List<(ServiceEvent Type, LockBody? Body)> Events { get; } = [];
        public bool AnyUserSession => true;
        public void Broadcast(ServiceEvent type, object? body) => Events.Add((type, body as LockBody ?? (body is string s ? GacJson.Deserialize<LockBody>(s) : null)));
        public Task<PipeResponse?> RequestAsync(ServiceEvent type, object? body, TimeSpan timeout, CancellationToken ct) => Task.FromResult<PipeResponse?>(null);
    }

    private static PeriodPreset Period(string id, params string[] actionTypes)
    {
        var preset = new ClassPreset { Name = id, Actions = actionTypes.Select(t => new PresetAction { Type = t }).ToList() };
        return new PeriodPreset(id, "기말고사", GacJson.Serialize(preset), DateTimeOffset.Now, DateTimeOffset.Now.AddDays(1), "sig");
    }

    [Fact]
    public void Lock_event_sent_once_for_active_lock_period()
    {
        var tray = new FakeTray();
        var c = new LockController(tray, TimeProvider.System);
        var periods = new[] { Period("p1", PresetActionTypes.Mute, PresetActionTypes.LockPc) };
        c.Reconcile(periods);
        c.Reconcile(periods); // repeated polls must not re-send
        Assert.Single(tray.Events, e => e.Type == ServiceEvent.Lock && e.Body!.PeriodId == "p1");
    }

    [Fact]
    public void Period_without_lock_does_not_lock()
    {
        var tray = new FakeTray();
        new LockController(tray, TimeProvider.System).Reconcile([Period("p1", PresetActionTypes.Mute)]);
        Assert.DoesNotContain(tray.Events, e => e.Type == ServiceEvent.Lock);
    }

    [Fact]
    public void Unlock_lifts_the_lock_until_period_ends_and_does_not_relock()
    {
        var tray = new FakeTray();
        var c = new LockController(tray, TimeProvider.System);
        var periods = new[] { Period("p1", PresetActionTypes.LockPc) };
        c.Reconcile(periods);
        c.MarkUnlocked("p1");
        Assert.Contains(tray.Events, e => e.Type == ServiceEvent.Unlock && e.Body!.PeriodId == "p1");
        tray.Events.Clear();
        c.Reconcile(periods); // still active, but unlocked this boot → no new lock
        Assert.DoesNotContain(tray.Events, e => e.Type == ServiceEvent.Lock);
    }

    [Fact]
    public void Exemption_day_suppresses_lock_even_with_an_active_period()
    {
        var tray = new FakeTray();
        var c = new LockController(tray, TimeProvider.System);
        c.Reconcile([Period("p1", PresetActionTypes.LockPc)], DateTimeOffset.Now, exemptToday: true);
        Assert.DoesNotContain(tray.Events, e => e.Type == ServiceEvent.Lock);
    }

    [Fact]
    public void Open_lock_closes_when_the_exemption_day_starts_and_locks_again_the_next_day()
    {
        var tray = new FakeTray();
        var c = new LockController(tray, TimeProvider.System);
        var periods = new[] { Period("p1", PresetActionTypes.LockPc) };
        c.Reconcile(periods, DateTimeOffset.Now, exemptToday: false);   // PC left on overnight, locked
        tray.Events.Clear();
        c.Reconcile(periods, DateTimeOffset.Now, exemptToday: true);    // 00:00 of the exemption day
        Assert.Contains(tray.Events, e => e.Type == ServiceEvent.Unlock && e.Body!.PeriodId == "p1");
        tray.Events.Clear();
        c.Reconcile(periods, DateTimeOffset.Now, exemptToday: true);    // stays unlocked all day
        Assert.Empty(tray.Events);
        c.Reconcile(periods, DateTimeOffset.Now, exemptToday: false);   // next day
        Assert.Single(tray.Events, e => e.Type == ServiceEvent.Lock && e.Body!.PeriodId == "p1");
    }

    [Fact]
    public void Agent_uses_server_time_not_a_tampered_local_clock_for_exemption()
    {
        var serverNow = new DateTimeOffset(2026, 11, 17, 1, 0, 0, TimeSpan.Zero); // 17th KST
        var wall = serverNow.AddDays(1);                                          // student set the PC clock to the 18th
        long tick = 0;
        var clock = new TrustedClock(() => tick, () => wall);
        clock.OnServerTime(serverNow);
        var dates = new[] { new ExemptionDate(new DateOnly(2026, 11, 18), ExemptionCalendar.PresetName, "x") };
        var (now, ignored) = clock.Now();
        Assert.True(ignored);
        Assert.False(ExemptionCalendar.IsExempt(dates, now));

        var tray = new FakeTray();
        var c = new LockController(tray, TimeProvider.System);
        var p = new PeriodPreset("p1", "기말고사", GacJson.Serialize(new ClassPreset { Name = "x", Actions = [new() { Type = PresetActionTypes.LockPc }] }),
            serverNow.AddDays(-1), serverNow.AddDays(3), "sig");
        c.Reconcile([p], now, ExemptionCalendar.IsExempt(dates, now));
        Assert.Single(tray.Events, e => e.Type == ServiceEvent.Lock); // lock still runs
    }

    [Fact]
    public void Period_ending_clears_the_lock()
    {
        var tray = new FakeTray();
        var c = new LockController(tray, TimeProvider.System);
        c.Reconcile([Period("p1", PresetActionTypes.LockPc)]);
        tray.Events.Clear();
        c.Reconcile([]); // period no longer active
        Assert.Contains(tray.Events, e => e.Type == ServiceEvent.Unlock && e.Body!.PeriodId == "p1");
    }
}
