using Gac.Console.Services;
using Gac.Shared;
using Gac.Shared.ClassMode;
using Gac.Shared.Contracts;

namespace Gac.Demo;

/// <summary>
/// CHANGE-002 fake server state: calendar + lock exemption days, overload alerts and blocks, the Admin PC auto-shutdown
/// schedule, and the "설치 미완료" state until the installer's firewall step is done. Shared by all three demo views.
/// Nothing touches the real firewall, clock, services or network.
/// </summary>
public sealed partial class DemoServerClient
{
    public const string InstallIncompleteMessage = "서버 설치가 끝나지 않았습니다. 서버 PC에서 [방화벽 열기]를 마쳐야 회원가입할 수 있습니다.";
    public const string DemoExemptTodayName = ExemptionCalendar.PresetName + " (체험: 오늘)";

    private readonly List<ExemptionDay> _exemptions = [];
    private readonly List<OverloadAlert> _alerts = [];
    private readonly List<OverloadBlock> _blocks = [];
    private ServerShutdownSettings _serverShutdown = ServerShutdownSettings.Default() with { Enabled = true };
    private OverloadSettings _overload = new();
    private bool _shutdownWarning, _skippedToday;
    private int _extendedMinutes;
    private string? _shutdownEvent;
    private bool _installComplete = true;
    private string _range = "10.10.0.0/16";
    private int _port = 8443;

    private void SeedChange002()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var now = DateTimeOffset.Now;
        _exemptions.Add(new ExemptionDay("ex-guard", ExemptionCalendar.PresetName, ExemptionRecurrence.MonthlyNthWeekday,
            new DateOnly(today.Year, today.Month, 1), null, 3, DayOfWeek.Wednesday, [], "owner", now.AddDays(-20)));
        _exemptions.Add(new ExemptionDay("ex-festival", "학교 축제", ExemptionRecurrence.Once, today.AddDays(12), null, null, null, [], "admin1", now.AddDays(-6)));

        var examStart = new DateTimeOffset(DateTime.Today.AddDays(2).AddHours(8));
        var lab = _pcs.Where(p => p.GroupId is 1 or 2 or 3 or 4).Select(p => p.Name).ToList();
        _periods.Add(new PeriodPresetInfo("period-exam", "기말고사 기간", examStart, examStart.AddDays(5), "owner", lab.Count, false, true, lab));
        var lunchStart = new DateTimeOffset(DateTime.Today.AddDays(-1).AddHours(12));
        var labOnly = _pcs.Where(p => p.GroupId == 4).Select(p => p.Name).ToList();
        _periods.Add(new PeriodPresetInfo("period-lunch", "점심시간 컴퓨터실 잠금", lunchStart, lunchStart.AddDays(14), "admin1", labOnly.Count, true, true, labOnly));

        var started = now.AddMinutes(-7);
        _alerts.Add(new OverloadAlert("alert-1", "컴퓨터실 7번", "컴퓨터실-07", "10.10.9.107", OverloadLimit.Requests, 412, started, started.AddMinutes(10), false));
        _alerts.Add(new OverloadAlert("alert-0", null, null, "10.10.5.77", OverloadLimit.FailedAuth, 23, now.AddDays(-1), now.AddDays(-1).AddMinutes(20), true));
        _blocks.Add(new OverloadBlock("blk-1", "컴퓨터실 7번", "컴퓨터실-07", "10.10.9.107", "E1F2A3B4C5D6E7F8091A2B3C4D5E6F708192A3B4C5D6E7F8091A2B3C4D5E6F70", OverloadLimit.Requests, started.AddMinutes(10), 1));
        SeedAudit(started, "system", "overload.block", "컴퓨터실-07 (10.10.9.107)", "분당 요청 수 초과: 412회, 10분 차단");
        SeedAudit(started, "system", "overload.alert", "컴퓨터실-07", "운영자 PC로 알림 전송");
        SeedAudit(now.AddDays(-20), "owner", "calendar.exemption.add", ExemptionCalendar.PresetName, "매월 셋째 수요일");
    }

    // ---------- install state (CHANGE-002 §3) ----------
    /// <summary>The server view walks through the installer automatically on its first open only.</summary>
    internal bool InstallWizardShown { get; set; }

    public bool InstallComplete { get { lock (_lock) return _installComplete; } }
    public string LanRange { get { lock (_lock) return _range; } }
    public int Port { get { lock (_lock) return _port; } }

    /// <summary>The demo installer was closed before the firewall step: the server stays "설치 미완료".</summary>
    public void MarkInstallIncomplete() { lock (_lock) _installComplete = false; }

    /// <summary>Fake [방화벽 열기]: validates the range/port and "reads the rule back". Returns an error message or null.</summary>
    public string? OpenFirewall(string range, int port)
    {
        if (!IsCidr(range)) return "교내망 대역 형식이 올바르지 않습니다 (예: 10.10.0.0/16).";
        if (port is < 1 or > 65535) return "포트는 1~65535 사이여야 합니다.";
        lock (_lock)
        {
            _range = range.Trim();
            _port = port;
            _installComplete = true;
            SeedAudit(DateTimeOffset.Now, "Windows 관리자", "server.firewall", _range, $"TCP {port}, 교내망만 허용 (체험)");
        }
        return null;
    }

    public void AddRange(string range) { lock (_lock) { _range = _range + ", " + range.Trim(); SeedAudit(DateTimeOffset.Now, "Windows 관리자", "server.firewall.add_range", range); } }

    public static bool IsCidr(string? s)
    {
        var parts = (s ?? "").Trim().Split('/');
        return parts.Length == 2 && System.Net.IPAddress.TryParse(parts[0], out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && parts[0].Count(c => c == '.') == 3 && int.TryParse(parts[1], out var n) && n is >= 0 and <= 32;
    }

    /// <summary>Setup code is shown only after the firewall step and until the first sign-up.</summary>
    public string? SetupCodeForDisplay { get { lock (_lock) return _installComplete && !_ownerSignedUp ? _code : null; } }

    // ---------- calendar (CHANGE-002 §2) ----------
    public async Task<IReadOnlyList<ExemptionDay>> GetExemptionDaysAsync() { await Delay(80); lock (_lock) return _exemptions.ToList(); }

    public async Task<ExemptionDay> SaveExemptionDayAsync(string? id, SaveExemptionDayRequest r)
    {
        await Delay(150);
        if (ExemptionCalendar.Validate(r) is { } err) throw new GacClientException(err);
        lock (_lock)
        {
            var i = id == null ? -1 : _exemptions.FindIndex(e => e.Id == id);
            if (id != null && i < 0) throw new GacClientException("잠금 해제일을 찾을 수 없습니다.");
            var day = new ExemptionDay(id ?? Guid.NewGuid().ToString("N"), r.Name.Trim(), r.Recurrence, r.Date,
                r.Recurrence == ExemptionRecurrence.MonthlyDay ? r.DayOfMonth : null,
                r.Recurrence == ExemptionRecurrence.MonthlyNthWeekday ? r.Nth : null,
                r.Recurrence == ExemptionRecurrence.MonthlyNthWeekday ? r.Weekday : null,
                i >= 0 ? _exemptions[i].Skipped : [], Owner.UserName, DateTimeOffset.Now);
            if (i >= 0) _exemptions[i] = day; else _exemptions.Add(day);
            Audit(i >= 0 ? "calendar.exemption.edit" : "calendar.exemption.add", day.Name, ExemptionCalendar.Describe(day));
            return day;
        }
    }

    public async Task DeleteExemptionDayAsync(string id)
    {
        await Delay(100);
        lock (_lock)
        {
            var d = _exemptions.FirstOrDefault(e => e.Id == id) ?? throw new GacClientException("잠금 해제일을 찾을 수 없습니다.");
            _exemptions.Remove(d);
            Audit("calendar.exemption.delete", d.Name, ExemptionCalendar.Describe(d));
        }
    }

    public async Task SkipExemptionOccurrenceAsync(string id, DateOnly date)
    {
        await Delay(100);
        lock (_lock)
        {
            var i = _exemptions.FindIndex(e => e.Id == id);
            if (i < 0) throw new GacClientException("잠금 해제일을 찾을 수 없습니다.");
            _exemptions[i] = _exemptions[i] with { Skipped = [.. _exemptions[i].Skipped, date] };
            Audit("calendar.exemption.skip", _exemptions[i].Name, $"{date:yyyy-MM-dd} 건너뜀");
        }
    }

    /// <summary>What a client PC would receive: the next 60 days of exemption dates.</summary>
    public IReadOnlyList<ExemptionDate> AgentExemptDates()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        lock (_lock) return ExemptionCalendar.ExpandAll(_exemptions, today, today.AddDays(ExemptionCalendar.AgentHorizonDays));
    }

    /// <summary>Client view toggle: "오늘은 잠금 해제일" adds/removes a one-off exemption for today in the shared calendar.</summary>
    public void SetDemoExemptToday(bool on)
    {
        lock (_lock)
        {
            _exemptions.RemoveAll(e => e.Name == DemoExemptTodayName);
            if (on) _exemptions.Add(new ExemptionDay("ex-demo-today", DemoExemptTodayName, ExemptionRecurrence.Once, DateOnly.FromDateTime(DateTime.Today), null, null, null, [], Owner.UserName, DateTimeOffset.Now));
            Audit(on ? "calendar.exemption.add" : "calendar.exemption.delete", DemoExemptTodayName, "클라이언트 PC 화면의 체험 스위치");
        }
    }

    // ---------- alerts (CHANGE-002 §4.3/4.5) ----------
    public async Task<IReadOnlyList<OverloadAlert>> GetAlertsAsync() { await Delay(60); lock (_lock) return _alerts.OrderByDescending(a => a.StartedAt).ToList(); }

    public async Task MarkAlertsReadAsync(IReadOnlyList<string> ids)
    {
        await Delay(60);
        lock (_lock) for (var i = 0; i < _alerts.Count; i++) if (ids.Contains(_alerts[i].Id)) _alerts[i] = _alerts[i] with { Read = true };
    }

    /// <summary>Server view button: a classroom PC floods the server → it is blocked and the operator gets an alert.</summary>
    public OverloadAlert SimulateOverload()
    {
        lock (_lock)
        {
            var pc = _pcs.Where(p => p.Online).OrderBy(_ => _rng.Next()).First();
            var prior = _blocks.FirstOrDefault(b => b.WindowsName == pc.Name);
            var offence = (prior?.Offences ?? 0) + 1;
            var now = DateTimeOffset.Now;
            var until = now + _overload.BlockFor(offence);
            var count = _overload.MaxRequestsPerAgentPerMinute + _rng.Next(40, 400);
            _blocks.RemoveAll(b => b.WindowsName == pc.Name);
            _blocks.Add(new OverloadBlock("blk-" + Guid.NewGuid().ToString("N")[..8], pc.Alias, pc.Name, pc.Ip, pc.Id, OverloadLimit.Requests, until, offence));
            var alert = new OverloadAlert("alert-" + Guid.NewGuid().ToString("N")[..8], pc.Alias, pc.Name, pc.Ip, OverloadLimit.Requests, count, now, until, false);
            _alerts.Add(alert);
            SeedAudit(now, "system", "overload.block", $"{pc.Name} ({pc.Ip})", $"분당 요청 수 초과: {count}회, {(until - now).TotalMinutes:0}분 차단 ({offence}회째)");
            SeedAudit(now, "system", "overload.alert", pc.Name, "운영자 PC로 알림 전송");
            return alert;
        }
    }

    // ---------- server PC auto shutdown (CHANGE-002 §4.2) ----------
    public async Task<ServerShutdownSettings> GetServerShutdownAsync() { await Delay(60); return ServerShutdownNow(); }
    public ServerShutdownSettings ServerShutdownNow() { lock (_lock) return _serverShutdown; }

    public async Task SaveServerShutdownAsync(ServerShutdownSettings s) { await Delay(120); SaveServerShutdown(s, Owner.UserName); }

    public void SaveServerShutdown(ServerShutdownSettings s, string actor)
    {
        if (ServerShutdownSettings.Validate(s) is { } err) throw new GacClientException(err);
        lock (_lock)
        {
            _serverShutdown = s;
            var days = string.Join(", ", s.Days.Where(d => d.Enabled).Select(d => $"{ExemptionCalendar.WeekdayKorean(d.Day)[..1]} {d.Time}"));
            SeedAudit(DateTimeOffset.Now, actor, "server.shutdown.schedule", "서버 PC", (s.Enabled ? "켜짐: " + days : "꺼짐") + (s.ExceptionDates.Count > 0 ? $", 예외 {s.ExceptionDates.Count}일" : ""));
        }
    }

    public async Task<ServerShutdownStatus> GetServerShutdownStatusAsync() { await Delay(40); return ServerShutdownStatusNow(); }

    public ServerShutdownStatus ServerShutdownStatusNow()
    {
        lock (_lock)
        {
            DateTimeOffset? next = null;
            for (var i = 0; i < 8 && next == null; i++)
            {
                var date = DateOnly.FromDateTime(DateTime.Today.AddDays(i));
                if (i == 0 && _skippedToday) continue;
                if (_serverShutdown.ScheduledAt(date) is { } at)
                {
                    var when = new DateTimeOffset(at.AddMinutes(i == 0 ? _extendedMinutes : 0));
                    if (when > DateTimeOffset.Now || (i == 0 && _shutdownWarning)) next = when;
                }
            }
            var running = _batches.Count(b => b.Batch.Module is ModuleIds.Deploy or ModuleIds.Update && b.Batch.Done < b.Batch.Total && b.Rows.Any(r => r.Status == CommandStatus.Delivered));
            var waiting = _shutdownWarning && running > 0 ? $"배포·업데이트 작업 {running}건이 끝날 때까지 기다립니다 (최대 30분)" : null;
            return new ServerShutdownStatus(next, _shutdownWarning, _skippedToday, _extendedMinutes, waiting, _shutdownEvent);
        }
    }

    /// <summary>Demo: jump to "10 minutes before" so the Admin PC popup and the Console notice can be tried.</summary>
    public void SimulateServerShutdownWarning()
    {
        lock (_lock)
        {
            _shutdownWarning = true;
            _skippedToday = false;
            _shutdownEvent = "서버 PC가 10분 후 종료됩니다.";
            SeedAudit(DateTimeOffset.Now, "system", "server.shutdown.warning", "서버 PC", "10분 전 경고 (체험)");
        }
    }

    public async Task SkipServerShutdownTodayAsync() { await Delay(80); SkipServerShutdownToday(Owner.UserName); }
    public void SkipServerShutdownToday(string actor)
    {
        lock (_lock)
        {
            _skippedToday = true; _shutdownWarning = false; _extendedMinutes = 0;
            _shutdownEvent = $"오늘은 끄지 않기 ({actor})";
            SeedAudit(DateTimeOffset.Now, actor, "server.shutdown.skip_today", "서버 PC", "오늘만 자동 종료 안 함");
        }
    }

    public async Task ExtendServerShutdownAsync() { await Delay(80); ExtendServerShutdown(Owner.UserName); }
    public void ExtendServerShutdown(string actor)
    {
        lock (_lock)
        {
            _extendedMinutes += ServerShutdownSettings.ExtendMinutes; _shutdownWarning = false;
            _shutdownEvent = $"30분 연장 ({actor}, 총 {_extendedMinutes}분)";
            SeedAudit(DateTimeOffset.Now, actor, "server.shutdown.extend", "서버 PC", $"30분 연장 (총 {_extendedMinutes}분)");
        }
    }

    // ---------- overload settings + blocks ----------
    public async Task<OverloadSettings> GetOverloadSettingsAsync() { await Delay(60); return OverloadNow(); }
    public OverloadSettings OverloadNow() { lock (_lock) return _overload; }

    public async Task SaveOverloadSettingsAsync(OverloadSettings s) { await Delay(100); SaveOverload(s, Owner.UserName); }
    public void SaveOverload(OverloadSettings s, string actor)
    {
        if (OverloadSettings.Validate(s) is { } err) throw new GacClientException(err);
        lock (_lock) { _overload = s; SeedAudit(DateTimeOffset.Now, actor, "server.overload.settings", null, GacJson.Serialize(s)); }
    }

    public async Task<IReadOnlyList<OverloadBlock>> GetOverloadBlocksAsync() { await Delay(60); return BlocksNow(); }
    public IReadOnlyList<OverloadBlock> BlocksNow() { lock (_lock) return _blocks.Where(b => b.BlockedUntil > DateTimeOffset.Now).ToList(); }

    public async Task UnblockAsync(string key) { await Delay(80); Unblock(key, Owner.UserName); }
    public void Unblock(string key, string actor)
    {
        lock (_lock)
        {
            var b = _blocks.FirstOrDefault(x => x.Key == key) ?? throw new GacClientException("차단 항목을 찾을 수 없습니다.");
            _blocks.Remove(b);
            SeedAudit(DateTimeOffset.Now, actor, "overload.unblock", $"{b.WindowsName ?? "-"} ({b.Ip})");
        }
    }

    public async Task BlockPermanentlyAsync(string key) { await Delay(80); BlockPermanently(key, Owner.UserName); }
    public void BlockPermanently(string key, string actor)
    {
        lock (_lock)
        {
            var b = _blocks.FirstOrDefault(x => x.Key == key) ?? throw new GacClientException("차단 항목을 찾을 수 없습니다.");
            _blocks.Remove(b);
            _blocked.Add(new BlockedDevice(b.Fingerprint ?? b.Key, DateTimeOffset.Now, b.Ip, b.WindowsName ?? "-", "과부하 차단에서 영구 차단",
                $"과도한 요청으로 영구 차단되었습니다. ({OverloadText.Korean(b.Limit)})", true));
            SeedAudit(DateTimeOffset.Now, actor, "overload.block_permanent", $"{b.WindowsName ?? "-"} ({b.Ip})", "차단된 기기 목록에 추가");
        }
    }

    // ---------- server status snapshot for the GAC 서버 관리 view ----------
    public (int Pcs, int Online, int Blocked, DateTimeOffset? LastEnroll) ServerCounts()
    {
        lock (_lock) return (_pcs.Count, _pcs.Count(p => p.Online && !p.Revoked), _blocked.Count + _blocks.Count(b => b.BlockedUntil > DateTimeOffset.Now), DateTimeOffset.Now.AddDays(-1).AddHours(-3));
    }

    public string OwnerName { get { lock (_lock) return Owner.UserName; } }
    public void AuditFromServerPc(string action, string? target, string? detail) => SeedAudit(DateTimeOffset.Now, "Windows 관리자", action, target, detail);
}
