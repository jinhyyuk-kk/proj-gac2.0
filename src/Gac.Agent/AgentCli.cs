using System.Diagnostics;
using System.Net.NetworkInformation;
using Gac.Agent.Config;
using Gac.Agent.Net;
using Gac.Agent.Runtime;
using Gac.Modules.Clean;
using Gac.Modules.Deploy;
using Gac.Modules.Errors;
using Gac.Modules.Health;
using Gac.Modules.Inspect;
using Gac.Modules.NetDiag;
using Gac.Modules.Shutdown;
using Gac.Modules.Update;
using Gac.Shared.Contracts;
using Gac.Shared.Logging;
using Gac.Shared.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

namespace Gac.Agent;

/// <summary>
///   Gac.Agent.exe                                   run (service or console)
///   Gac.Agent.exe enroll --server HOST[:PORT] --code CODE
///   Gac.Agent.exe install                           register and start the Windows service
///   Gac.Agent.exe uninstall                         stop and remove the service and agent data
/// </summary>
public static class AgentCli
{
    public const string ServiceName = "GACAgent";

    public static async Task<int> RunAsync(string[] args)
    {
        var verb = args.FirstOrDefault()?.ToLowerInvariant() ?? "run";
        switch (verb)
        {
            case "run": await BuildHost(args.Skip(1).ToArray()).RunAsync(); return 0;
            case "enroll": return await EnrollAsync(args);
            case "install": return Install();
            case "uninstall": return Uninstall();
            default:
                Console.Error.WriteLine("Usage: Gac.Agent.exe [run | enroll --server HOST[:PORT] --code CODE | install | uninstall]");
                return 2;
        }
    }

    public static IHost BuildHost(string[] args, AgentStore? store = null)
    {
        store ??= new AgentStore();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider(store.LogsDir, "agent"));
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
        builder.Services.AddSingleton<TrayPipeServer>();
        builder.Services.AddSingleton(sp => new AgentCore(
            store, sp.GetRequiredService<TrayPipeServer>(), sp.GetRequiredService<IProcessRunner>(), TimeProvider.System,
            sp.GetRequiredService<ILogger<AgentCore>>(), core => CreateModules(core, sp.GetRequiredService<IProcessRunner>())));
        if (WindowsServiceHelpers.IsWindowsService())
            builder.Services.AddSingleton<IHostLifetime, GacServiceLifetime>();
        builder.Services.AddHostedService<TrayPipeWorker>();
        builder.Services.AddHostedService<PollWorker>();
        builder.Services.AddHostedService<SchedulerWorker>();
        return builder.Build();
    }

    /// <summary>The service-side modules. classmode runs entirely in the tray (user context).</summary>
    public static IEnumerable<IAgentModule> CreateModules(AgentCore core, IProcessRunner runner)
    {
        var time = TimeProvider.System;
        yield return new CleanModule(new WindowsCleanEnvironment(), time);
        yield return new ShutdownModule(core.Shutdown);
        yield return new ErrorsModule(new WindowsErrorSource(), core.Store.StateDir, time);
        yield return new UpdateModule(new WuaClient());
        yield return new DeployModule(core, runner, core.Store.WorkDir);
        yield return new HealthModule(new WindowsHealthSource(), time);
        yield return new InspectModule(core.Tray, new WindowsInspectSource(), runner, time);
        yield return new NetDiagModule(new SystemNetProbe(readUserProxy: false), () => (core.Identity?.ServerHost, core.Identity?.ServerPort ?? 0));
        yield return new ClassModeSyncModule();
    }

    private static async Task<int> EnrollAsync(string[] args)
    {
        string? Arg(string name)
        {
            var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        var server = Arg("--server");
        var code = Arg("--code");
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(code))
        {
            Console.Error.WriteLine("서버 주소와 등록 코드가 필요합니다: enroll --server HOST[:PORT] --code CODE");
            return 2;
        }
        var (host, port) = ParseServer(server);
        var store = new AgentStore();
        try
        {
            var mac = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                .Select(n => n.GetPhysicalAddress().ToString()).FirstOrDefault(m => m.Length > 0);
            Hardware.DeviceReportCollector.WriteAgentInstallMarker();
            var old = store.LoadIdentity();
            var req = new EnrollRequest(code, Environment.MachineName, Environment.OSVersion.VersionString, mac,
                typeof(AgentCli).Assembly.GetName().Version?.ToString(3) ?? "2.0.0",
                old?.AgentId, old == null ? null : Gac.Shared.Security.EnrollmentProof.ReenrollProof(old.Key, old.AgentId, Environment.MachineName),
                Hardware.DeviceReportCollector.Collect());
            var (resp, thumb) = await Enrollment.EnrollAsync(host, port, code, req, CancellationToken.None);
            if (resp.Status == EnrollStatus.PendingReview)
            {
                Console.Error.WriteLine("등록 대기: " + (resp.Message ?? "최고 관리자 승인을 기다립니다."));
                return 3;
            }
            store.SaveIdentity(new AgentIdentity(host, port, thumb, resp.AgentId, resp.AgentKeyBase64, resp.PresetPublicKeyPem));
            Console.WriteLine($"등록 완료: {Environment.MachineName} → {host}:{port}");
            return 0;
        }
        catch (Exception e) when (e is EnrollmentException or HttpRequestException or TaskCanceledException or System.Security.Authentication.AuthenticationException)
        {
            Console.Error.WriteLine("등록 실패: " + e.Message);
            return 1;
        }
    }

    public static (string Host, int Port) ParseServer(string s)
    {
        s = s.Trim();
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s[8..];
        s = s.TrimEnd('/');
        var idx = s.LastIndexOf(':');
        if (idx > 0 && int.TryParse(s[(idx + 1)..], out var port) && port is > 0 and < 65536) return (s[..idx], port);
        return (s, 8443);
    }

    private static int Install()
    {
        var exe = Environment.ProcessPath!;
        if (Run("sc.exe", $"query {ServiceName}") != 0)
            Run("sc.exe", $"create {ServiceName} binPath= \"\\\"{exe}\\\"\" start= auto DisplayName= \"GAC 2.0 Agent\"");
        else
            Run("sc.exe", $"config {ServiceName} binPath= \"\\\"{exe}\\\"\" start= auto");
        Run("sc.exe", $"description {ServiceName} \"GAC 2.0 학교 PC 관리 에이전트\"");
        Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/30000/restart/60000/restart/300000");
        Run("sc.exe", $"start {ServiceName}");
        return 0;
    }

    private static int Uninstall()
    {
        Run("sc.exe", $"stop {ServiceName}");
        for (var i = 0; i < 30 && Process.GetProcessesByName("Gac.Agent").Any(p => p.Id != Environment.ProcessId); i++) Thread.Sleep(500);
        Run("sc.exe", $"delete {ServiceName}");
        var root = new AgentStore().Root;
        try { Directory.Delete(root, recursive: true); } catch (Exception e) { Console.Error.WriteLine(e.Message); }
        return 0;
    }

    private static int Run(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}

/// <summary>Shared presets arrive with the policy on every poll; this command just confirms the PC has the latest ones.</summary>
public sealed class ClassModeSyncModule : IAgentModule
{
    public string Id => Gac.Shared.ModuleIds.ClassMode;

    public Task<ModuleOutcome> ExecuteAsync(ModuleRequest request, CancellationToken ct) =>
        Task.FromResult(request.Policy.SharedPresets == null
            ? ModuleOutcome.Ok("배포된 공용 프리셋 없음")
            : ModuleOutcome.Ok($"공용 프리셋 적용됨 ({request.Policy.SharedPresets.IssuedAt:yyyy-MM-dd HH:mm} 버전)"));
}
