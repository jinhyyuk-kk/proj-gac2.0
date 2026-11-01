using Gac.Console.Services;
using Gac.Demo;
using Gac.ServerAdmin.UI;
using Gac.Shared.Auth;
using Gac.Shared.Contracts;
using Gac.Tray.UI;

namespace Gac.Console.Tests;

/// <summary>CHANGE-002 §5 demo: calendar, alerts, server management, installer firewall step, exemption-day lock.</summary>
public class DemoChange002Tests
{
    [Fact]
    public void Korean_texts_are_exactly_as_written()
    {
        Assert.Equal("내PC지키미 점검일", ExemptionCalendar.PresetName);
        Assert.Equal("서버 PC가 10분 후 종료됩니다.", ServerShutdownNoticeForm.NoticeText);
        Assert.Equal("설치를 완료하려면 방화벽을 여세요", ServerAdminView.FirewallPrompt);
        Assert.Equal("교내망에서만 서버에 접속할 수 있도록 방화벽을 엽니다.", ServerAdminView.FirewallText);
    }

    [Fact]
    public async Task Calendar_has_sample_exam_period_and_guard_inspection_day()
    {
        using var c = new DemoServerClient(delayScale: 0);
        var periods = await c.GetPeriodsAsync();
        var exam = Assert.Single(periods, p => p.PeriodName == "기말고사 기간");
        Assert.True(exam.HasLock);
        Assert.NotEmpty(exam.TargetNames!);
        var guard = Assert.Single(await c.GetExemptionDaysAsync(), d => d.Name == "내PC지키미 점검일");
        Assert.Equal(ExemptionRecurrence.MonthlyNthWeekday, guard.Recurrence);
        Assert.Equal(3, guard.Nth);
        Assert.Equal(DayOfWeek.Wednesday, guard.Weekday);
        Assert.Contains(c.AgentExemptDates(), d => d.Name == "내PC지키미 점검일"); // within the 60-day agent horizon
    }

    [Fact]
    public async Task Exemption_days_can_be_added_edited_skipped_and_deleted_with_audit()
    {
        using var c = new DemoServerClient(delayScale: 0);
        var d = await c.SaveExemptionDayAsync(null, new SaveExemptionDayRequest("학교 행사", ExemptionRecurrence.MonthlyDay, DateOnly.FromDateTime(DateTime.Today), 15));
        d = await c.SaveExemptionDayAsync(d.Id, new SaveExemptionDayRequest("학교 행사(수정)", ExemptionRecurrence.MonthlyDay, d.Date, 16));
        Assert.Equal(16, d.DayOfMonth);
        var next = ExemptionCalendar.Expand(d, DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today).AddMonths(3)).First();
        await c.SkipExemptionOccurrenceAsync(d.Id, next);
        Assert.Contains(next, (await c.GetExemptionDaysAsync()).Single(x => x.Id == d.Id).Skipped);
        await c.DeleteExemptionDayAsync(d.Id);
        var audit = await c.GetAuditAsync();
        foreach (var a in new[] { "calendar.exemption.add", "calendar.exemption.edit", "calendar.exemption.skip", "calendar.exemption.delete" })
            Assert.Contains(audit, x => x.Action == a);
        await Assert.ThrowsAsync<GacClientException>(() => c.SaveExemptionDayAsync(null, new SaveExemptionDayRequest("", ExemptionRecurrence.Once, default)));
    }

    [Fact]
    public async Task Alerts_have_a_sample_and_overload_queues_an_unread_alert_with_block()
    {
        using var c = new DemoServerClient(delayScale: 0);
        Assert.Contains(await c.GetAlertsAsync(), a => !a.Read && a.Limit == OverloadLimit.Requests);
        var alert = c.SimulateOverload();
        var alerts = await c.GetAlertsAsync();
        Assert.Equal(alert.Id, alerts[0].Id); // newest first
        Assert.False(alerts[0].Read);
        Assert.Contains(await c.GetOverloadBlocksAsync(), b => b.WindowsName == alert.WindowsName);
        await c.MarkAlertsReadAsync([alert.Id]);
        Assert.True((await c.GetAlertsAsync()).Single(a => a.Id == alert.Id).Read);

        var block = (await c.GetOverloadBlocksAsync()).First(b => b.WindowsName == alert.WindowsName);
        await c.UnblockAsync(block.Key);
        Assert.DoesNotContain(await c.GetOverloadBlocksAsync(), b => b.Key == block.Key);
        var other = (await c.GetOverloadBlocksAsync()).First();
        await c.BlockPermanentlyAsync(other.Key);
        Assert.Contains(await c.GetBlockedDevicesAsync(), d => d.PermanentlyBlocked && d.Reason.Contains("과도한 요청"));
        Assert.Contains(await c.GetAuditAsync(), a => a.Action == "overload.unblock");
    }

    [Fact]
    public async Task Server_shutdown_warning_skip_today_and_extend()
    {
        using var c = new DemoServerClient(delayScale: 0);
        c.SimulateServerShutdownWarning();
        Assert.True((await c.GetServerShutdownStatusAsync()).Warning);
        await c.ExtendServerShutdownAsync();
        var s = await c.GetServerShutdownStatusAsync();
        Assert.False(s.Warning);
        Assert.Equal(30, s.ExtendedMinutes);
        c.SimulateServerShutdownWarning();
        await c.SkipServerShutdownTodayAsync();
        s = await c.GetServerShutdownStatusAsync();
        Assert.True(s.SkippedToday);
        Assert.False(s.Warning);
        // only today is skipped: a later day is still scheduled
        Assert.True(s.NextShutdown == null || s.NextShutdown.Value.Date > DateTime.Today);
        var audit = await c.GetAuditAsync();
        Assert.Contains(audit, a => a.Action == "server.shutdown.extend");
        Assert.Contains(audit, a => a.Action == "server.shutdown.skip_today");
    }

    [Fact]
    public async Task Server_shutdown_schedule_is_shared_between_console_and_server_pc()
    {
        using var c = new DemoServerClient(delayScale: 0);
        var backend = new DemoServerAdminBackend(c);
        var s = await c.GetServerShutdownAsync();
        var changed = s with { Days = [.. s.Days.Select(d => d.Day == DayOfWeek.Monday ? d with { Time = "19:30" } : d)] };
        Assert.Null(backend.SaveShutdown(changed));
        Assert.Equal("19:30", (await c.GetServerShutdownAsync()).Days.Single(d => d.Day == DayOfWeek.Monday).Time);
        Assert.NotNull(backend.SaveShutdown(changed with { Days = [.. changed.Days.Select(d => d with { Time = "25:99" })] }));
    }

    [Fact]
    public async Task Install_incomplete_refuses_sign_up_and_hides_setup_code_until_firewall_is_opened()
    {
        using var c = new DemoServerClient(delayScale: 0);
        var backend = new DemoServerAdminBackend(c);
        c.MarkInstallIncomplete();
        Assert.False(backend.GetStatus().InstallComplete);
        Assert.Null(backend.GetStatus().SetupCode);
        await Assert.ThrowsAsync<GacClientException>(() => c.CreateOwnerAsync(new CreateOwnerRequest("principal", "교장", "GoodPass1", "x")));
        Assert.NotNull(backend.OpenFirewall("not-a-range", 8443));
        Assert.False(c.InstallComplete);
        Assert.Null(backend.OpenFirewall("10.10.0.0/16", 8443));
        Assert.True(backend.GetStatus().InstallComplete);
        Assert.NotNull(backend.GetStatus().SetupCode);
        var owner = await c.CreateOwnerAsync(new CreateOwnerRequest("principal", "교장", "GoodPass1", "x"));
        Assert.Equal(Role.Owner, owner.Role);
        Assert.Null(backend.GetStatus().SetupCode); // shown only until the first sign-up
    }

    [Fact]
    public void Installer_wizard_needs_the_firewall_step_before_finish() => Sta.Run(() =>
    {
        using var c = new DemoServerClient(delayScale: 0);
        using var wiz = new DemoServerInstallWizard(c, new DemoServerAdminBackend(c));
        while (wiz.Step < DemoServerInstallWizard.FirewallStep) wiz.Next();
        wiz.Next(); // [다음] is disabled until the rule exists
        Assert.Equal(DemoServerInstallWizard.FirewallStep, wiz.Step);
        Assert.Null(wiz.ClickOpenFirewall());
        wiz.Next();
        Assert.Equal(DemoServerInstallWizard.FinishStep, wiz.Step);
        Assert.NotNull(c.SetupCodeForDisplay);
    });

    [Fact]
    public void Client_lock_does_not_appear_on_an_exemption_day_and_closes_if_open() => Sta.Run(() =>
    {
        using var c = new DemoServerClient(delayScale: 0);
        using var f = new DemoClientPcForm(c);
        if (f.IsExemptToday()) return; // today happens to be a real sample exemption day: nothing to compare
        f.OpenLock();
        var lockForm = f.CurrentLock;
        Assert.NotNull(lockForm);
        f.ExemptToggle.Checked = true; // exemption day starts while the lock window is open
        Assert.Null(f.CurrentLock);
        Assert.True(lockForm!.IsDisposed || !lockForm.Visible);
        f.OpenLock();
        Assert.Null(f.CurrentLock); // lock_pc does not run at all today
        f.ExemptToggle.Checked = false; // the next (non-exempt) day: lock runs again
        f.OpenLock();
        Assert.NotNull(f.CurrentLock);
        f.CurrentLock!.ForceClose();
    });

    [Fact]
    public void Console_main_window_has_calendar_and_alerts_and_admin_icon() => Sta.Run(() =>
    {
        using var client = new DemoServerClient(delayScale: 0);
        using var form = new Gac.Console.UI.MainForm(client, client.Owner, DemoProgram.CreateOptions(false));
        var tabs = form.Tabs.TabPages.Cast<TabPage>().Select(t => t.Text).ToList();
        Assert.Contains("캘린더", tabs);
        Assert.Contains("알림", tabs);
        Assert.NotNull(form.Calendar);
        Assert.NotNull(form.Alerts);
        Assert.Same(Gac.Console.UI.ConsoleIcons.Admin, form.Icon);
        Assert.NotNull(form.LogoImage.Image);
    });

    [Fact]
    public void Server_admin_ui_cannot_reach_the_real_server()
    {
        var closure = DemoSafetyTests.ReferenceClosure(typeof(ServerAdminView).Assembly);
        foreach (var f in new[] { "Gac.Server", "Gac.Agent", "Gac.Tray", "Gac.ServerStatus" }) Assert.DoesNotContain(f, closure);
        Assert.DoesNotContain(closure, n => n.StartsWith("Gac.Modules.", StringComparison.OrdinalIgnoreCase));
    }
}
