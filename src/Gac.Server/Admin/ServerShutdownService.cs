using Dapper;
using Gac.Server.Data;
using Gac.Shared;
using Gac.Shared.Contracts;

namespace Gac.Server.Admin;

/// <summary>Turns the Admin PC off. The real one runs shutdown.exe; tests use a fake.</summary>
public interface IServerPowerAction { void Shutdown(); }

public sealed class WindowsServerPowerAction : IServerPowerAction
{
    // Normal (not forced) shutdown. /t 0 because any /t > 0 implies /f (documented shutdown.exe behaviour).
    public void Shutdown() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/s /t 0 /c \"GAC: 예약된 서버 PC 자동 종료\"") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
}

/// <summary>
/// Admin PC auto shutdown (CHANGE-002 §4.2). The schedule and today's state (skipped, extended, warned, done) live in
/// the settings table, so the Console, GAC 서버 관리 and the service always see the same thing. T−10 minutes = warning
/// (Admin PC popup + Console notice). At the due time, running deploy/update jobs are waited for (max 30 minutes, the
/// delay is logged). Missed runs are skipped, never caught up: if the service was not running at the due time, nothing
/// happens at the next boot.
/// </summary>
public sealed class ServerShutdownService(SettingsStore settings, AuditLog audit, Db db, TimeProvider time, IServerPowerAction power)
{
    public const string ScheduleKey = "server_shutdown";
    public const string StateKey = "server_shutdown_state";
    private readonly DateTimeOffset _startedAt = time.GetUtcNow();
    private readonly Lock _lock = new();

    public sealed class DayState
    {
        public DateOnly Date { get; set; }
        public bool Skipped { get; set; }
        public int ExtendedMinutes { get; set; }
        public bool Warned { get; set; }
        public bool Done { get; set; }
        public bool Missed { get; set; }
        public DateTimeOffset? WaitStartedAt { get; set; }
        public string? LastEvent { get; set; }
    }

    public ServerShutdownSettings Schedule => settings.GetJson(ScheduleKey, ServerShutdownSettings.Default());

    public void SaveSchedule(string actor, ServerShutdownSettings s)
    {
        if (ServerShutdownSettings.Validate(s) is { } err) throw new Accounts.GacException(err);
        settings.SetJson(ScheduleKey, s);
        var days = string.Join(", ", s.Days.Where(d => d.Enabled).Select(d => $"{ExemptionCalendar.WeekdayKorean(d.Day)[..1]} {d.Time}"));
        audit.Write(actor, "server.shutdown.schedule", "서버 PC", (s.Enabled ? "켜짐: " + days : "꺼짐") + (s.ExceptionDates.Count > 0 ? $", 예외 {s.ExceptionDates.Count}일" : ""));
    }

    private DateTime LocalNow => TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone).DateTime;

    private DayState Today()
    {
        var today = DateOnly.FromDateTime(LocalNow);
        var st = settings.GetJson<DayState?>(StateKey, null);
        return st != null && st.Date == today ? st : new DayState { Date = today };
    }

    private void Save(DayState st) => settings.SetJson(StateKey, st);

    private DateTimeOffset? Due(ServerShutdownSettings s, DayState st) =>
        s.ScheduledAt(st.Date) is { } at ? new DateTimeOffset(at.AddMinutes(st.ExtendedMinutes), time.LocalTimeZone.GetUtcOffset(at)) : null;

    public void SkipToday(string actor)
    {
        lock (_lock)
        {
            var st = Today();
            st.Skipped = true; st.Warned = false; st.WaitStartedAt = null;
            st.LastEvent = $"오늘은 끄지 않기 ({actor})";
            Save(st);
        }
        audit.Write(actor, "server.shutdown.skip_today", "서버 PC", "오늘만 자동 종료 안 함");
    }

    public void Extend(string actor)
    {
        int total;
        lock (_lock)
        {
            var st = Today();
            st.ExtendedMinutes += ServerShutdownSettings.ExtendMinutes; st.Warned = false; st.WaitStartedAt = null;
            total = st.ExtendedMinutes;
            st.LastEvent = $"30분 연장 ({actor}, 총 {total}분)";
            Save(st);
        }
        audit.Write(actor, "server.shutdown.extend", "서버 PC", $"30분 연장 (총 {total}분)");
    }

    /// <summary>Running deploy/update commands: the shutdown waits for them (CHANGE-002 §4.2).</summary>
    private int RunningJobs()
    {
        using var c = db.Open();
        return c.ExecuteScalar<int>("SELECT COUNT(*) FROM commands WHERE module IN (@d, @u) AND status = 'Delivered'", new { d = ModuleIds.Deploy, u = ModuleIds.Update });
    }

    /// <summary>One scheduler step. Called every few seconds by the hosted service.</summary>
    public void Tick()
    {
        var s = Schedule;
        var now = time.GetUtcNow();
        lock (_lock)
        {
            var st = Today();
            if (Due(s, st) is not { } due || st.Skipped || st.Done || st.Missed) return;
            if (_startedAt > due)
            {
                // Missed run: the server PC (or service) was off at the scheduled time. Never caught up.
                st.Missed = true;
                st.LastEvent = "예정 시각에 서버가 꺼져 있어 오늘 자동 종료를 건너뜀";
                Save(st);
                audit.Write("system", "server.shutdown.missed", "서버 PC", $"{due.ToLocalTime():HH:mm} 예정분 건너뜀");
                return;
            }
            if (now < due.AddMinutes(-ServerShutdownSettings.WarningMinutes)) return;
            if (now < due)
            {
                if (st.Warned) return;
                st.Warned = true;
                st.LastEvent = "서버 PC가 10분 후 종료됩니다.";
                Save(st);
                audit.Write("system", "server.shutdown.warning", "서버 PC", $"{due.ToLocalTime():HH:mm} 종료 10분 전 경고");
                return;
            }
            var running = RunningJobs();
            if (running > 0 && (st.WaitStartedAt == null || now - st.WaitStartedAt < TimeSpan.FromMinutes(ServerShutdownSettings.MaxJobWaitMinutes)))
            {
                if (st.WaitStartedAt == null)
                {
                    st.WaitStartedAt = now;
                    st.LastEvent = $"배포·업데이트 작업 {running}건이 끝날 때까지 기다립니다 (최대 30분)";
                    Save(st);
                    audit.Write("system", "server.shutdown.delayed", "서버 PC", $"작업 {running}건 진행 중 — 종료를 미룸");
                }
                return;
            }
            st.Done = true;
            var delay = st.WaitStartedAt is { } w ? $" ({(now - w).TotalMinutes:0}분 늦춤)" : "";
            st.LastEvent = "자동 종료 실행" + delay;
            Save(st);
            audit.Write("system", "server.shutdown.run", "서버 PC", "예약된 자동 종료" + delay);
        }
        power.Shutdown();
    }

    public ServerShutdownStatus Status()
    {
        var s = Schedule;
        var now = time.GetUtcNow();
        DayState st;
        lock (_lock) st = Today();
        DateTimeOffset? next = null;
        var todayDue = Due(s, st);
        var warning = false;
        string? waiting = null;
        if (todayDue is { } d && !st.Skipped && !st.Done && !st.Missed && (d > now || st.WaitStartedAt != null))
        {
            next = d;
            warning = now >= d.AddMinutes(-ServerShutdownSettings.WarningMinutes);
            if (st.WaitStartedAt != null) waiting = st.LastEvent;
        }
        for (var i = 1; next == null && i <= 8; i++)
            if (s.ScheduledAt(st.Date.AddDays(i)) is { } at) next = new DateTimeOffset(at, time.LocalTimeZone.GetUtcOffset(at));
        return new ServerShutdownStatus(next, warning, st.Skipped, st.ExtendedMinutes, waiting, st.LastEvent);
    }
}

/// <summary>Runs <see cref="ServerShutdownService.Tick"/> every 15 seconds.</summary>
public sealed class ServerShutdownWorker(ServerShutdownService svc, TimeProvider time, ILogger<ServerShutdownWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { svc.Tick(); } catch (Exception e) { log.LogWarning(e, "Server shutdown tick failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), time, ct); } catch (OperationCanceledException) { break; }
        }
    }
}
