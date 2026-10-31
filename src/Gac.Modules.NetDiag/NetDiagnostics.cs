using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Gac.Shared.Contracts;
using Gac.Shared.Pipe;
using Microsoft.Win32;

namespace Gac.Modules.NetDiag;

public sealed record AdapterInfo(string Name, string Description, bool Up, IPAddress? IPv4, IPAddress? Gateway, IReadOnlyList<IPAddress> DnsServers, bool DhcpEnabled);
public sealed record ProxyInfo(bool Enabled, string? Server, string? AutoConfigUrl);

/// <summary>All network probing goes through this interface so the diagnosis logic can be unit-tested.</summary>
public interface INetProbe
{
    IReadOnlyList<AdapterInfo> Adapters();
    Task<bool> PingAsync(string host, CancellationToken ct);
    Task<bool> ResolveAsync(string name, CancellationToken ct);
    Task<bool> TcpConnectAsync(string host, int port, CancellationToken ct);
    ProxyInfo? UserProxy();
}

public sealed class SystemNetProbe(bool readUserProxy) : INetProbe
{
    public IReadOnlyList<AdapterInfo> Adapters() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Where(n => !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                        && !n.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase) && !n.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
            .Select(n =>
            {
                var p = n.GetIPProperties();
                var ip = p.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                var gw = p.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))?.Address;
                bool dhcp;
                try { dhcp = p.GetIPv4Properties()?.IsDhcpEnabled ?? false; } catch (NetworkInformationException) { dhcp = false; }
                return new AdapterInfo(n.Name, n.Description, n.OperationalStatus == OperationalStatus.Up, ip, gw,
                    p.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).ToList(), dhcp);
            }).ToList();

    public async Task<bool> PingAsync(string host, CancellationToken ct)
    {
        using var ping = new Ping();
        for (var i = 0; i < 2; i++)
        {
            try
            {
                var r = await ping.SendPingAsync(host, TimeSpan.FromMilliseconds(1500), cancellationToken: ct);
                if (r.Status == IPStatus.Success) return true;
            }
            catch (PingException) { }
        }
        return false;
    }

    public async Task<bool> ResolveAsync(string name, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var r = await Dns.GetHostAddressesAsync(name, cts.Token);
            return r.Length > 0;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException) { return false; }
    }

    public async Task<bool> TcpConnectAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException) { return false; }
    }

    public ProxyInfo? UserProxy()
    {
        if (!readUserProxy) return null;
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        if (k == null) return new ProxyInfo(false, null, null);
        return new ProxyInfo(k.GetValue("ProxyEnable") is int e && e == 1, k.GetValue("ProxyServer") as string, k.GetValue("AutoConfigURL") as string);
    }
}

public sealed record NetDiagOptions(string? ServerHost, int ServerPort, string? DnsTestName, string? ExternalTarget);

public static class CheckNames
{
    public const string Adapter = "네트워크 어댑터";
    public const string Ip = "IP 주소";
    public const string Gateway = "게이트웨이";
    public const string Dns = "DNS";
    public const string Server = "GAC 서버";
    public const string Proxy = "프록시 설정";
    public const string External = "외부 주소";
}

/// <summary>Runs the checks in the order from CLAUDE.md 4.4.</summary>
public sealed class NetDiagnostics(INetProbe probe)
{
    public async Task<(List<NetCheck> Checks, AdapterInfo? Adapter, ProxyInfo? Proxy)> DiagnoseAsync(NetDiagOptions o, CancellationToken ct)
    {
        var checks = new List<NetCheck>();
        var adapters = probe.Adapters();
        var up = adapters.Where(a => a.Up && a.IPv4 != null).ToList();
        var adapter = up.FirstOrDefault(a => a.Gateway != null) ?? up.FirstOrDefault();

        if (adapter == null)
        {
            checks.Add(new(CheckNames.Adapter, NetCheckStatus.Failed, "랜선 또는 Wi-Fi 연결이 끊겨 있습니다. 케이블이 꽂혀 있는지 확인하세요."));
            foreach (var n in new[] { CheckNames.Ip, CheckNames.Gateway, CheckNames.Dns, CheckNames.Server })
                checks.Add(new(n, NetCheckStatus.Skipped, "어댑터가 연결되지 않아 건너뜀"));
            return (checks, null, probe.UserProxy());
        }
        checks.Add(new(CheckNames.Adapter, NetCheckStatus.Ok, $"연결됨 ({adapter.Name})"));

        var ip = adapter.IPv4!;
        var apipa = ip.GetAddressBytes() is [169, 254, ..];
        checks.Add(apipa
            ? new(CheckNames.Ip, NetCheckStatus.Failed, $"IP 주소를 받지 못했습니다 ({ip}). 공유기나 DHCP 서버에 문제가 있을 수 있습니다.")
            : new(CheckNames.Ip, NetCheckStatus.Ok, $"{ip}" + (adapter.DhcpEnabled ? " (자동)" : " (고정)")));

        if (adapter.Gateway == null) checks.Add(new(CheckNames.Gateway, NetCheckStatus.Failed, "게이트웨이가 설정되어 있지 않습니다."));
        else if (await probe.PingAsync(adapter.Gateway.ToString(), ct)) checks.Add(new(CheckNames.Gateway, NetCheckStatus.Ok, $"{adapter.Gateway} 응답함"));
        else checks.Add(new(CheckNames.Gateway, NetCheckStatus.Failed, $"게이트웨이({adapter.Gateway})가 응답하지 않습니다."));

        var dnsName = !string.IsNullOrWhiteSpace(o.DnsTestName) ? o.DnsTestName
            : o.ServerHost != null && !IPAddress.TryParse(o.ServerHost, out _) ? o.ServerHost : null;
        if (adapter.DnsServers.Count == 0) checks.Add(new(CheckNames.Dns, NetCheckStatus.Failed, "DNS 서버가 설정되어 있지 않습니다."));
        else if (dnsName == null) checks.Add(new(CheckNames.Dns, NetCheckStatus.Skipped, "확인할 이름이 설정되지 않아 건너뜀"));
        else if (await probe.ResolveAsync(dnsName, ct)) checks.Add(new(CheckNames.Dns, NetCheckStatus.Ok, $"'{dnsName}' 이름 확인 성공"));
        else checks.Add(new(CheckNames.Dns, NetCheckStatus.Failed, $"'{dnsName}' 이름을 찾지 못했습니다 (DNS 서버: {string.Join(", ", adapter.DnsServers)})."));

        if (string.IsNullOrWhiteSpace(o.ServerHost)) checks.Add(new(CheckNames.Server, NetCheckStatus.Skipped, "GAC 서버 주소가 설정되지 않음"));
        else if (await probe.TcpConnectAsync(o.ServerHost, o.ServerPort, ct)) checks.Add(new(CheckNames.Server, NetCheckStatus.Ok, $"{o.ServerHost}:{o.ServerPort} 연결됨"));
        else checks.Add(new(CheckNames.Server, NetCheckStatus.Failed, $"GAC 서버({o.ServerHost}:{o.ServerPort})에 연결할 수 없습니다."));

        var proxy = probe.UserProxy();
        if (proxy != null)
        {
            if (proxy.Enabled || !string.IsNullOrEmpty(proxy.AutoConfigUrl))
                checks.Add(new(CheckNames.Proxy, NetCheckStatus.Warning, "프록시가 설정되어 있습니다: " + (proxy.Server ?? proxy.AutoConfigUrl)));
            else checks.Add(new(CheckNames.Proxy, NetCheckStatus.Ok, "프록시 사용 안 함"));
        }

        if (!string.IsNullOrWhiteSpace(o.ExternalTarget))
            checks.Add(await probe.PingAsync(o.ExternalTarget, ct)
                ? new(CheckNames.External, NetCheckStatus.Ok, $"{o.ExternalTarget} 응답함")
                : new(CheckNames.External, NetCheckStatus.Failed, $"{o.ExternalTarget}에 연결할 수 없습니다."));
        return (checks, adapter, proxy);
    }
}

/// <summary>Repairs requested from the SYSTEM service through the fixed <see cref="NetRepairAction"/> enum.</summary>
public interface INetRepairChannel
{
    Task<PipeResponse> RepairAsync(NetRepairAction action, CancellationToken ct);
}

/// <summary>User-context proxy reset (HKCU). Implemented by the tray.</summary>
public interface IProxyReset
{
    void Reset();
}

/// <summary>Diagnose → safe repairs → re-check. Reboot-requiring repairs need user confirmation.</summary>
public sealed class NetDiagFlow(NetDiagnostics diagnostics, INetRepairChannel repairs, IProxyReset? proxyReset)
{
    public async Task<NetDiagReport> RunAsync(NetDiagOptions o, Func<string, bool> confirmReboot, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("네트워크 상태를 확인하는 중…");
        var (checks, adapter, proxy) = await diagnostics.DiagnoseAsync(o, ct);
        if (!HasFailure(checks)) return Final(checks, [], "네트워크 연결 정상");

        var tried = new List<string>();
        var fixedMsgs = new List<string>();
        bool Failed(string name) => checks.Any(c => c.Name == name && c.Status == NetCheckStatus.Failed);

        if (Failed(CheckNames.Adapter))
            return Final(checks, tried, "네트워크 케이블 또는 Wi-Fi 연결을 확인하세요. 자동으로 고칠 수 없는 문제입니다.");

        if (Failed(CheckNames.Ip) || Failed(CheckNames.Gateway))
        {
            progress?.Report("IP 주소를 다시 받는 중…");
            tried.Add("IP 주소 다시 받기(DHCP)");
            await repairs.RepairAsync(NetRepairAction.RenewDhcp, ct);
            (checks, adapter, proxy) = await diagnostics.DiagnoseAsync(o, ct);
            if (!Failed(CheckNames.Ip) && !Failed(CheckNames.Gateway)) fixedMsgs.Add("IP 주소 문제를 고쳤습니다");
        }

        if (Failed(CheckNames.Dns))
        {
            progress?.Report("DNS 캐시를 비우는 중…");
            tried.Add("DNS 캐시 비우기");
            await repairs.RepairAsync(NetRepairAction.FlushDns, ct);
            (checks, adapter, proxy) = await diagnostics.DiagnoseAsync(o, ct);
            if (Failed(CheckNames.Dns) && adapter is { DhcpEnabled: true })
            {
                progress?.Report("DNS 설정을 자동으로 되돌리는 중…");
                tried.Add("DNS 서버 설정을 자동(DHCP)으로 되돌리기");
                await repairs.RepairAsync(NetRepairAction.ResetDnsToDhcp, ct);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                (checks, adapter, proxy) = await diagnostics.DiagnoseAsync(o, ct);
            }
            if (!Failed(CheckNames.Dns)) fixedMsgs.Add("DNS 문제를 고쳤습니다");
        }

        if ((Failed(CheckNames.Server) || Failed(CheckNames.External)) && proxyReset != null && proxy is { } px && (px.Enabled || px.AutoConfigUrl != null))
        {
            progress?.Report("프록시 설정을 초기화하는 중…");
            tried.Add("사용자 프록시 설정 초기화");
            proxyReset.Reset();
            (checks, adapter, proxy) = await diagnostics.DiagnoseAsync(o, ct);
            if (!Failed(CheckNames.Server)) fixedMsgs.Add("프록시 설정 문제를 고쳤습니다");
        }

        if (HasFailure(checks) && (Failed(CheckNames.Gateway) || Failed(CheckNames.Dns) || Failed(CheckNames.Server)))
        {
            if (confirmReboot("네트워크 설정(Winsock)을 초기화하면 문제가 해결될 수 있습니다.\n초기화 후에는 PC를 다시 시작해야 합니다. 진행할까요?"))
            {
                tried.Add("Winsock 초기화(재시작 필요)");
                var r = await repairs.RepairAsync(NetRepairAction.ResetWinsock, ct);
                if (r.Ok) fixedMsgs.Add("네트워크 설정을 초기화했습니다. PC를 다시 시작하세요");
            }
        }

        string summary;
        if (!HasFailure(checks)) summary = fixedMsgs.Count > 0 ? string.Join(". ", fixedMsgs) + "." : "네트워크 연결 정상";
        else summary = (fixedMsgs.Count > 0 ? string.Join(". ", fixedMsgs) + ". " : "") +
                       "아직 해결되지 않은 문제가 있습니다: " + string.Join(", ", checks.Where(c => c.Status == NetCheckStatus.Failed).Select(c => c.Name)) +
                       ". 진단 결과를 관리자에게 보냈습니다.";
        return Final(checks, tried, summary);
    }

    private static bool HasFailure(IEnumerable<NetCheck> checks) => checks.Any(c => c.Status == NetCheckStatus.Failed);

    private static NetDiagReport Final(List<NetCheck> checks, List<string> tried, string summary) =>
        new(checks, tried, !HasFailure(checks), summary);
}
