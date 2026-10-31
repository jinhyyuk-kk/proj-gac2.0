using System.Net.NetworkInformation;
using System.Net.Sockets;
using Gac.Shared;
using Gac.Shared.Contracts;
using Gac.Shared.Modules;
using Gac.Shared.Pipe;

namespace Gac.Modules.NetDiag;

/// <summary>
/// Service-side (SYSTEM) implementations of the fixed repair enum. Each value maps to one hard-coded command;
/// nothing from the tray is ever passed through to a command line except the enum value.
/// </summary>
public sealed class NetRepairService(IProcessRunner runner)
{
    private static string Sys(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    public async Task<PipeResponse> RepairAsync(NetRepairAction action, CancellationToken ct)
    {
        switch (action)
        {
            case NetRepairAction.FlushDns:
            {
                var r = await runner.RunAsync(Sys("ipconfig.exe"), "/flushdns", TimeSpan.FromSeconds(30), ct);
                return new PipeResponse(r.ExitCode == 0, r.ExitCode == 0 ? "DNS 캐시를 비웠습니다." : "DNS 캐시를 비우지 못했습니다.");
            }
            case NetRepairAction.RenewDhcp:
            {
                await runner.RunAsync(Sys("ipconfig.exe"), "/release", TimeSpan.FromSeconds(60), ct);
                var r = await runner.RunAsync(Sys("ipconfig.exe"), "/renew", TimeSpan.FromSeconds(90), ct);
                return new PipeResponse(r.ExitCode == 0, r.ExitCode == 0 ? "IP 주소를 다시 받았습니다." : "IP 주소를 다시 받지 못했습니다.");
            }
            case NetRepairAction.ResetDnsToDhcp:
            {
                var names = DhcpAdapters();
                if (names.Count == 0) return new PipeResponse(false, "자동 IP를 쓰는 어댑터가 없어 DNS 설정을 바꾸지 않았습니다.");
                var ok = true;
                foreach (var n in names)
                {
                    var safe = n.Replace("\"", "");
                    var r = await runner.RunAsync(Sys("netsh.exe"), $"interface ipv4 set dnsservers name=\"{safe}\" source=dhcp", TimeSpan.FromSeconds(30), ct);
                    ok &= r.ExitCode == 0;
                }
                await runner.RunAsync(Sys("ipconfig.exe"), "/flushdns", TimeSpan.FromSeconds(30), ct);
                return new PipeResponse(ok, ok ? "DNS 서버 설정을 자동으로 되돌렸습니다." : "DNS 설정을 되돌리지 못했습니다.");
            }
            case NetRepairAction.ResetWinsock:
            {
                var r = await runner.RunAsync(Sys("netsh.exe"), "winsock reset", TimeSpan.FromSeconds(60), ct);
                return new PipeResponse(r.ExitCode == 0, r.ExitCode == 0 ? "Winsock을 초기화했습니다. PC를 다시 시작해야 적용됩니다." : "Winsock 초기화에 실패했습니다.");
            }
            default:
                return new PipeResponse(false, "허용되지 않은 작업입니다.");
        }
    }

    /// <summary>Adapters that are up and already obtain their IP via DHCP (only those get DNS reset).</summary>
    private static List<string> DhcpAdapters() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Where(n =>
            {
                try { return n.GetIPProperties().GetIPv4Properties()?.IsDhcpEnabled == true && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork); }
                catch (NetworkInformationException) { return false; }
            })
            .Select(n => n.Name).ToList();
}

/// <summary>Console-requested diagnosis, run by the service (no proxy check: that is per-user). No repairs are attempted.</summary>
public sealed class NetDiagModule(INetProbe probe, Func<(string? Host, int Port)> server) : IAgentModule
{
    public string Id => ModuleIds.NetDiag;

    public async Task<ModuleOutcome> ExecuteAsync(ModuleRequest request, CancellationToken ct)
    {
        if (request.Action != ModuleActions.NetDiagRun) return ModuleOutcome.Fail("알 수 없는 명령입니다.");
        var (host, port) = server();
        var (checks, _, _) = await new NetDiagnostics(probe).DiagnoseAsync(
            new NetDiagOptions(host, port, request.Policy.NetDiag.DnsTestName, request.Policy.NetDiag.ExternalTarget), ct);
        var failed = checks.Where(c => c.Status == NetCheckStatus.Failed).ToList();
        var summary = failed.Count == 0 ? "네트워크 연결 정상" : "문제 발견: " + string.Join(", ", failed.Select(f => f.Name));
        return ModuleOutcome.Ok(summary, new NetDiagReport(checks, [], failed.Count == 0, summary));
    }
}
