using System.Net;
using Gac.Modules.ClassMode;
using Gac.Modules.Deploy;
using Gac.Modules.Errors;
using Gac.Modules.Health;
using Gac.Modules.Inspect;
using Gac.Modules.NetDiag;
using Gac.Modules.Shutdown;
using Gac.Modules.Update;
using Gac.Shared;
using Gac.Shared.ClassMode;
using Gac.Shared.Contracts;
using Gac.Shared.Modules;
using Gac.Shared.Pipe;

namespace Gac.Modules.Tests;

internal sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("t", Now.Offset, "t", "t");
    public void Advance(TimeSpan t) => Now += t;
}

internal sealed class FakeTray(bool session = true) : ITrayBridge
{
    public bool AnyUserSession { get; set; } = session;
    public List<ServiceEvent> Events { get; } = [];
    public Func<ServiceEvent, PipeResponse?>? Respond { get; set; }
    public Task<PipeResponse?> RequestAsync(ServiceEvent type, object? body, TimeSpan timeout, CancellationToken ct) { Events.Add(type); return Task.FromResult(Respond?.Invoke(type)); }
    public void Broadcast(ServiceEvent type, object? body) => Events.Add(type);
}

internal sealed class FakeExecutor : IShutdownExecutor
{
    public List<bool> Calls { get; } = [];
    public Task ShutdownAsync(bool force, CancellationToken ct) { Calls.Add(force); return Task.CompletedTask; }
}

internal sealed class FakeRunner : IProcessRunner
{
    public List<(string File, string Args)> Calls { get; } = [];
    public int Exit { get; set; }
    public Task<ProcessRunResult> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Add((fileName, arguments));
        return Task.FromResult(new ProcessRunResult(Exit, false, "ok", "", TimeSpan.FromSeconds(1)));
    }
}

public class ShutdownCoordinatorTests
{
    // Friday 17:20 local (+09:00).
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 17, 20, 0, TimeSpan.FromHours(9));
    private static readonly ShutdownPolicy Policy = new() { Enabled = true, Time = "17:30", WarningMinutes = 5, ExtensionMinutes = 30, MaxExtensions = 1, ForceGraceMinutes = 10 };

    private static (ShutdownCoordinator C, FakeTime T, FakeTray Tray, FakeExecutor X, List<ShutdownLog> Logs) Make(bool session = true)
    {
        var t = new FakeTime(Start);
        var tray = new FakeTray(session);
        var x = new FakeExecutor();
        var logs = new List<ShutdownLog>();
        return (new ShutdownCoordinator(tray, x, t, logs.Add), t, tray, x, logs);
    }

    [Fact]
    public async Task Warns_at_T_minus_5_then_shuts_down_normally_then_forces()
    {
        var (c, t, tray, x, logs) = Make();
        await c.TickAsync(Policy, default);
        Assert.Empty(tray.Events);

        t.Now = Start.AddMinutes(5); // 17:25
        await c.TickAsync(Policy, default);
        Assert.Contains(ServiceEvent.ShutdownWarning, tray.Events);
        Assert.Empty(x.Calls);

        t.Now = Start.AddMinutes(10); // 17:30
        await c.TickAsync(Policy, default);
        Assert.Equal([false], x.Calls);

        t.Now = Start.AddMinutes(21); // grace passed
        await c.TickAsync(Policy, default);
        Assert.Equal([false, true], x.Calls);
        Assert.Contains(logs, l => l.Outcome == ShutdownOutcome.Forced);
    }

    [Fact]
    public async Task Extension_moves_deadline_and_is_limited()
    {
        var (c, t, _, x, logs) = Make();
        t.Now = Start.AddMinutes(6);
        await c.TickAsync(Policy, default);
        Assert.True(c.Extend("teacher").Ok);
        Assert.False(c.Extend("teacher").Ok); // MaxExtensions = 1
        t.Now = Start.AddMinutes(10);
        await c.TickAsync(Policy, default);
        Assert.Empty(x.Calls);
        t.Now = Start.AddMinutes(40);
        await c.TickAsync(Policy, default);
        Assert.Single(x.Calls);
        Assert.Contains(logs, l => l.Outcome == ShutdownOutcome.Extended && l.By == "teacher");
    }

    [Fact]
    public async Task No_user_means_no_popup()
    {
        var (c, t, tray, x, _) = Make(session: false);
        t.Now = Start.AddMinutes(6);
        await c.TickAsync(Policy, default);
        Assert.DoesNotContain(ServiceEvent.ShutdownWarning, tray.Events);
        t.Now = Start.AddMinutes(10);
        await c.TickAsync(Policy, default);
        Assert.Single(x.Calls);
    }

    [Fact]
    public async Task Weekend_and_disabled_do_nothing()
    {
        var (c, t, tray, x, _) = Make();
        t.Now = new DateTimeOffset(2026, 10, 31, 17, 30, 0, TimeSpan.FromHours(9)); // Saturday
        await c.TickAsync(Policy, default);
        await c.TickAsync(Policy with { Enabled = false }, default);
        Assert.Empty(x.Calls);
        Assert.Empty(tray.Events);
    }

    [Fact]
    public async Task Pc_started_long_after_schedule_is_not_shut_down()
    {
        var (c, t, _, x, _) = Make();
        t.Now = Start.AddHours(2);
        await c.TickAsync(Policy, default);
        t.Advance(TimeSpan.FromMinutes(10));
        await c.TickAsync(Policy, default);
        Assert.Empty(x.Calls);
    }

    [Fact]
    public async Task Cancel_stops_pending_shutdown()
    {
        var (c, t, _, x, logs) = Make();
        c.Request(5, "owner");
        Assert.True(c.Cancel("owner"));
        t.Advance(TimeSpan.FromMinutes(10));
        await c.TickAsync(Policy with { Enabled = false }, default);
        Assert.Empty(x.Calls);
        Assert.Contains(logs, l => l.Outcome == ShutdownOutcome.Cancelled);
    }
}

public class NetDiagTests
{
    private sealed class FakeProbe : INetProbe
    {
        public bool DnsWorks { get; set; }
        public bool ProxyOn { get; set; }
        public bool ServerReachable { get; set; } = true;
        public IReadOnlyList<AdapterInfo> Adapters() =>
            [new("이더넷", "Intel", true, IPAddress.Parse("10.0.0.5"), IPAddress.Parse("10.0.0.1"), [IPAddress.Parse("10.0.0.2")], true)];
        public Task<bool> PingAsync(string host, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> ResolveAsync(string name, CancellationToken ct) => Task.FromResult(DnsWorks);
        public Task<bool> TcpConnectAsync(string host, int port, CancellationToken ct) => Task.FromResult(ServerReachable && !ProxyOn || ServerReachable);
        public ProxyInfo? UserProxy() => new(ProxyOn, ProxyOn ? "1.2.3.4:8080" : null, null);
    }

    private sealed class FakeRepairs(FakeProbe probe) : INetRepairChannel
    {
        public List<NetRepairAction> Calls { get; } = [];
        public Task<PipeResponse> RepairAsync(NetRepairAction action, CancellationToken ct)
        {
            Calls.Add(action);
            if (action == NetRepairAction.ResetDnsToDhcp) probe.DnsWorks = true; // the broken static DNS gets fixed
            return Task.FromResult(new PipeResponse(true, "ok"));
        }
    }

    private static readonly NetDiagOptions Opts = new("gac-server", 8443, null, null);

    [Fact]
    public async Task Healthy_network_reports_ok_without_repairs()
    {
        var probe = new FakeProbe { DnsWorks = true };
        var repairs = new FakeRepairs(probe);
        var r = await new NetDiagFlow(new NetDiagnostics(probe), repairs, null).RunAsync(Opts, _ => false, null, default);
        Assert.True(r.Healthy);
        Assert.Equal("네트워크 연결 정상", r.Summary);
        Assert.Empty(repairs.Calls);
    }

    [Fact]
    public async Task Broken_dns_is_fixed_by_flush_then_reset_to_dhcp()
    {
        var probe = new FakeProbe { DnsWorks = false };
        var repairs = new FakeRepairs(probe);
        var r = await new NetDiagFlow(new NetDiagnostics(probe), repairs, null).RunAsync(Opts, _ => false, null, default);
        Assert.True(r.Healthy);
        Assert.Equal([NetRepairAction.FlushDns, NetRepairAction.ResetDnsToDhcp], repairs.Calls);
        Assert.Contains("DNS 문제를 고쳤습니다", r.Summary);
    }

    [Fact]
    public async Task Winsock_reset_only_with_user_consent()
    {
        var probe = new FakeProbe { DnsWorks = false };
        var repairs = new NeverFix();
        var asked = false;
        var r = await new NetDiagFlow(new NetDiagnostics(probe), repairs, null).RunAsync(Opts, _ => { asked = true; return false; }, null, default);
        Assert.True(asked);
        Assert.DoesNotContain(NetRepairAction.ResetWinsock, repairs.Calls);
        Assert.False(r.Healthy);
    }

    private sealed class NeverFix : INetRepairChannel
    {
        public List<NetRepairAction> Calls { get; } = [];
        public Task<PipeResponse> RepairAsync(NetRepairAction action, CancellationToken ct) { Calls.Add(action); return Task.FromResult(new PipeResponse(true, "")); }
    }

    [Fact]
    public async Task Apipa_address_is_detected()
    {
        var probe = new ApipaProbe();
        var (checks, _, _) = await new NetDiagnostics(probe).DiagnoseAsync(Opts, default);
        Assert.Equal(NetCheckStatus.Failed, checks.Single(c => c.Name == CheckNames.Ip).Status);
    }

    private sealed class ApipaProbe : INetProbe
    {
        public IReadOnlyList<AdapterInfo> Adapters() => [new("이더넷", "x", true, IPAddress.Parse("169.254.10.2"), null, [], true)];
        public Task<bool> PingAsync(string host, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> ResolveAsync(string name, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> TcpConnectAsync(string host, int port, CancellationToken ct) => Task.FromResult(false);
        public ProxyInfo? UserProxy() => null;
    }

    [Fact]
    public async Task External_check_is_off_by_default()
    {
        var (checks, _, _) = await new NetDiagnostics(new FakeProbe { DnsWorks = true }).DiagnoseAsync(Opts, default);
        Assert.DoesNotContain(checks, c => c.Name == CheckNames.External);
    }
}

public class OtherModuleTests
{
    [Fact]
    public void Errors_are_deduplicated_and_critical_first()
    {
        var t = DateTimeOffset.Now;
        var s = ErrorsModule.Summarize([
            new("System", "Disk", 7, "Error", t, "bad block"), new("System", "Disk", 7, "Error", t.AddMinutes(1), "bad block 2"),
            new("System", "Kernel-Power", 41, "Critical", t, "unexpected"),
        ]);
        Assert.Equal(2, s.Count);
        Assert.Equal("Critical", s[0].Level);
        Assert.Equal(2, s[1].Count);
        Assert.Equal("bad block 2", s[1].Message);
    }

    [Fact]
    public void Msi_install_uses_msiexec_quiet()
    {
        var (file, args) = DeployModule.BuildInstallCommand(@"C:\p\app.msi", "");
        Assert.EndsWith("msiexec.exe", file);
        Assert.Equal("/i \"C:\\p\\app.msi\" /qn /norestart", args);
        Assert.Equal((@"C:\p\setup.exe", "/S"), DeployModule.BuildInstallCommand(@"C:\p\setup.exe", "/S"));
    }

    [Fact]
    public async Task Deploy_rejects_hash_mismatch_without_running()
    {
        var runner = new FakeRunner();
        var m = new DeployModule(new BadHashSource(), runner, Path.GetTempPath());
        var payload = GacJson.Serialize(new DeployPackageRequest("p1", "a.exe", "AA", "/S", 60));
        var r = await m.ExecuteAsync(new ModuleRequest(ModuleActions.DeployInstall, payload, ReportTrigger.Command, new AgentPolicy()), default);
        Assert.False(r.Success);
        Assert.Contains("해시", r.Summary);
        Assert.Empty(runner.Calls);
    }

    private sealed class BadHashSource : IPackageSource
    {
        public Task<string> DownloadVerifiedAsync(string packageId, string fileName, string sha256, CancellationToken ct) => throw new InvalidDataException();
    }

    [Fact]
    public async Task Run_script_writes_temp_file_and_cleans_up()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gac-deploy-" + Guid.NewGuid().ToString("N"));
        var runner = new FakeRunner();
        var m = new DeployModule(new BadHashSource(), runner, dir);
        var payload = GacJson.Serialize(new RunScriptRequest(ScriptShell.Cmd, "echo 안녕", 30));
        var r = await m.ExecuteAsync(new ModuleRequest(ModuleActions.DeployRunScript, payload, ReportTrigger.Command, new AgentPolicy()), default);
        Assert.True(r.Success);
        Assert.Single(runner.Calls);
        Assert.Empty(Directory.EnumerateFiles(dir));
        Directory.Delete(dir);
    }

    [Theory]
    [InlineData("한컴오피스 2022", null, true)]
    [InlineData("Some Game", "Evil Inc", false)]
    [InlineData("Google Chrome", "Google LLC", true)]
    public void Inspect_allowlist_matches_name_or_publisher(string name, string? publisher, bool allowed) =>
        Assert.Equal(allowed, InspectModule.IsAllowed(name, publisher, ["한컴", "Google"]));

    [Fact]
    public void Health_flags_low_disk()
    {
        var m = new HealthModule(new FakeHealth(), TimeProvider.System);
        var r = m.Build(new HealthPolicy { FreeSpaceWarnPercent = 10 });
        Assert.Contains(r.Warnings, w => w.Contains("C:"));
    }

    private sealed class FakeHealth : IHealthSource
    {
        public IReadOnlyList<VolumeHealth> Volumes() => [new("C:", null, 100, 5, 5.0), new("D:", null, 100, 50, 50.0)];
        public IReadOnlyList<DiskHealth> Disks() => [];
        public (long Total, long Used) Memory() => (100, 40);
        public TimeSpan Uptime() => TimeSpan.FromHours(3);
        public double? LastBootDurationSeconds() => 20;
    }

    [Fact]
    public void Update_summary_reports_unreachable_source()
    {
        var s = UpdateModule.Summarize(new UpdateReport(false, "업데이트 원본에 연결할 수 없습니다.", [], null, false));
        Assert.Contains("연결할 수 없습니다", s);
    }

    [Fact]
    public async Task Preset_runner_continues_after_failed_action()
    {
        var exec = new FailSecond();
        var p = new ClassPreset { Name = "t", Actions = [new() { Type = "mute" }, new() { Type = "unmute" }, new() { Type = "set_volume", Value = 10 }] };
        var r = await new PresetRunner(exec).RunAsync(p, null, default);
        Assert.Equal(3, r.Count);
        Assert.False(r[1].Success);
        Assert.True(r[2].Success);
    }

    [Fact]
    public async Task Preset_runner_refuses_run_command_in_teacher_preset()
    {
        var exec = new FailSecond();
        var p = new ClassPreset { Name = "t", Actions = [new() { Type = "run_command", Command = "whoami" }] };
        var r = await new PresetRunner(exec).RunAsync(p, null, default);
        Assert.False(Assert.Single(r).Success);
        Assert.Equal(0, exec.Count);
    }

    private sealed class FailSecond : IPresetActionExecutor
    {
        public int Count;
        public Task ExecuteAsync(PresetAction action, bool sharedPreset, CancellationToken ct) =>
            ++Count == 2 ? throw new InvalidOperationException("실패") : Task.CompletedTask;
    }
}
