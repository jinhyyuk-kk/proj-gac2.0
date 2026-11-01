using System.Diagnostics;
using System.Net;

namespace Gac.Server.Hosting;

/// <summary>What the Windows Firewall reports for the GAC rule (read back through the HNetCfg.FwPolicy2 COM API).</summary>
public sealed record FirewallRuleInfo(bool Enabled, int Direction, int Action, int Protocol, string LocalPorts, string RemoteAddresses, int Profiles);

/// <summary>
/// The Admin PC's single inbound rule (CHANGE-001 §2, CHANGE-002 §3): TCP, the one port, remote address = the school
/// LAN range(s) only, all profiles. Created with netsh, then read back through the firewall COM API — its values are
/// not localized, unlike netsh's text output on Korean Windows — and verified before the install counts as complete.
/// </summary>
public static class Firewall
{
    public const string RuleName = ServerCli.FirewallRuleName;
    private const int NET_FW_RULE_DIR_IN = 1, NET_FW_ACTION_ALLOW = 1, NET_FW_IP_PROTOCOL_TCP = 6, NET_FW_PROFILE2_ALL = 0x7FFFFFFF;

    /// <summary>Creates (replaces) the rule. Returns null on success or a Korean error message.</summary>
    public static string? Apply(int port, IReadOnlyList<string> ranges, string exePath)
    {
        var valid = ranges.Where(LanDetection.IsValidCidr).Select(Normalize).Distinct().ToArray();
        if (valid.Length == 0) return "교내망 대역 형식이 올바르지 않습니다 (예: 10.20.0.0/22).";
        if (port is < 1 or > 65535) return "포트는 1~65535 사이여야 합니다.";
        Run("netsh", $"advfirewall firewall delete rule name=\"{RuleName}\"");
        var (code, output) = Run("netsh", $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} remoteip={string.Join(",", valid)} program=\"{exePath}\" profile=any enable=yes");
        return code == 0 ? null : $"방화벽 규칙을 만들지 못했습니다 (netsh {code}). 관리자 권한으로 실행했는지 확인하세요.\n{output.Trim()}";
    }

    /// <summary>Reads the rule back; null when it does not exist or the firewall API is unavailable.</summary>
    public static FirewallRuleInfo? ReadBack()
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type == null) return null;
            dynamic policy = Activator.CreateInstance(type)!;
            foreach (dynamic rule in policy.Rules)
            {
                if ((string)rule.Name != RuleName) continue;
                return new FirewallRuleInfo((bool)rule.Enabled, (int)rule.Direction, (int)rule.Action, (int)rule.Protocol,
                    (string)(rule.LocalPorts ?? ""), (string)(rule.RemoteAddresses ?? ""), (int)rule.Profiles);
            }
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>Pure check of a read-back rule: inbound TCP allow, exactly the port, exactly the ranges, all profiles.</summary>
    public static string? Verify(FirewallRuleInfo? rule, int port, IReadOnlyList<string> ranges)
    {
        if (rule == null) return "방화벽 규칙을 찾을 수 없습니다.";
        if (!rule.Enabled) return "방화벽 규칙이 꺼져 있습니다.";
        if (rule.Direction != NET_FW_RULE_DIR_IN || rule.Action != NET_FW_ACTION_ALLOW || rule.Protocol != NET_FW_IP_PROTOCOL_TCP) return "방화벽 규칙의 방향·동작·프로토콜이 올바르지 않습니다.";
        if (rule.LocalPorts.Trim() != port.ToString(System.Globalization.CultureInfo.InvariantCulture)) return $"방화벽 규칙의 포트가 {port}이(가) 아닙니다 ({rule.LocalPorts}).";
        if (rule.Profiles != NET_FW_PROFILE2_ALL && (rule.Profiles & 0x7) != 0x7) return "방화벽 규칙이 모든 네트워크 프로필(도메인·개인·공용)에 적용되지 않았습니다.";
        var actual = rule.RemoteAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(TryNormalize).ToHashSet();
        var expected = ranges.Where(LanDetection.IsValidCidr).Select(Normalize).ToHashSet();
        if (expected.Count == 0 || actual.Contains(null) || !actual.SetEquals(expected!)) return $"방화벽 규칙의 원격 주소가 교내망 대역과 다릅니다 ({rule.RemoteAddresses}).";
        return null;
    }

    /// <summary>"10.20.3.7/22" → "10.20.0.0/22".</summary>
    public static string Normalize(string cidr)
    {
        var parts = cidr.Trim().Split('/');
        return LanDetection.ToCidr(IPAddress.Parse(parts[0]), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Accepts "a.b.c.d/len", "a.b.c.d/255.255.0.0" (COM format) or a single address; null if unparsable.</summary>
    public static string? TryNormalize(string s)
    {
        var parts = s.Split('/');
        if (!IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
        if (parts.Length == 1) return LanDetection.ToCidr(ip, 32);
        if (int.TryParse(parts[1], out var len) && len is >= 0 and <= 32) return LanDetection.ToCidr(ip, len);
        if (!IPAddress.TryParse(parts[1], out var mask)) return null;
        var bits = mask.GetAddressBytes().Aggregate(0u, (acc, b) => (acc << 8) | b);
        var prefix = System.Numerics.BitOperations.PopCount(bits);
        if (bits != (prefix == 0 ? 0u : uint.MaxValue << (32 - prefix))) return null; // non-contiguous mask
        return LanDetection.ToCidr(ip, prefix);
    }

    public static void Delete() => Run("netsh", $"advfirewall firewall delete rule name=\"{RuleName}\"");

    private static (int Code, string Output) Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o);
    }
}
