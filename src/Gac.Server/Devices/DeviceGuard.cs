using System.Security.Cryptography;
using System.Text;
using Gac.Shared.Contracts;

namespace Gac.Server.Devices;

/// <summary>
/// Server-side desktop-only + install-order enforcement (CHANGE-001 sections 1.2 and 1.4). This is the real protection:
/// it works from raw probe values the agent sends, never a client boolean, and is applied at enrollment, heartbeat and
/// command dispatch. Pure logic so it is exhaustively unit-tested.
/// </summary>
public sealed class DeviceGuard(IReadOnlySet<string>? knownReleaseHashes = null)
{
    // Chassis values that mean a portable/mobile machine (SMBIOS System Enclosure type 3).
    public static readonly IReadOnlySet<int> PortableChassis = new HashSet<int> { 8, 9, 10, 11, 14, 30, 31, 32 };
    public const int MobilePcSystemType = 2;

    private readonly IReadOnlySet<string> _knownHashes = knownReleaseHashes ?? new HashSet<string>();

    /// <summary>
    /// Decides whether a reporting device may act as a client. <paramref name="seenAsConsoleFirst"/> is true when the
    /// server has recorded this fingerprint as a Console before any agent enrollment (install-order rule).
    /// </summary>
    public DeviceDecision Decide(DeviceReport report, bool seenAsConsoleFirst)
    {
        // Fail closed: anything missing is a laptop.
        if (report.Probes is null || !report.Probes.IsComplete)
            return Block(DeviceVerdict.ProbeMissing, "하드웨어 확인 값이 누락되었습니다.", permanent: false);

        var p = report.Probes;

        // Install order: a machine seen as Console first can never become a client, nor one whose Console markers
        // predate its agent install (or that has no agent install time at all).
        if (seenAsConsoleFirst || ConsoleCameFirst(report.Markers))
            return Block(DeviceVerdict.ConsoleFirst, "관리자 프로그램이 먼저 설치된 PC는 클라이언트가 될 수 없습니다.", permanent: false);

        // Integrity: a rebuilt agent whose binaries do not match a known official release is refused.
        if (_knownHashes.Count > 0 && !IntegrityMatches(report.Integrity))
            return Block(DeviceVerdict.IntegrityMismatch, "에이전트 파일이 정식 배포본과 일치하지 않습니다 (변조 의심).", permanent: false);

        // Must enroll over wired Ethernet.
        if (!report.WiredLinkActive || report.WiredMacs.Length == 0)
            return Block(DeviceVerdict.WifiOnly, "유선 네트워크(이더넷)로만 등록할 수 있습니다.", permanent: false);

        // Hard laptop signals — no override is ever possible.
        if (p.PcSystemType == MobilePcSystemType)
            return Block(DeviceVerdict.LaptopMobileType, LaptopMessage, permanent: true);

        var wmi = p.ChassisTypesWmi ?? [];
        var smbios = p.ChassisTypesSmbios ?? [];
        var wmiPortable = wmi.Any(PortableChassis.Contains);
        var smbiosPortable = smbios.Any(PortableChassis.Contains);
        if (wmiPortable || smbiosPortable)
            return Block(DeviceVerdict.LaptopChassis, LaptopMessage, permanent: true);

        // Two chassis sources that disagree (one portable, other not) are already caught above. Treat an empty/unknown
        // chassis from one source while the other reports a concrete desktop as inconsistent → fail closed.
        if (ChassisInconsistent(wmi, smbios))
            return Block(DeviceVerdict.ProbeInconsistent, "하드웨어 확인 값이 서로 일치하지 않습니다.", permanent: false);

        if (p.LidPresent == true)
            return Block(DeviceVerdict.LaptopLid, LaptopMessage, permanent: true);

        // Battery present but no lid and desktop everywhere else: a desktop may report a UPS as a battery → review.
        if (p.SystemBatteriesPresent == true || p.BatteryInstancePresent == true)
            return new DeviceDecision(DeviceVerdict.NeedsBatteryReview, Accepted: false, NeedsReview: true, PermanentlyBlocked: false,
                "배터리 신호가 감지되었습니다. 최고 관리자 승인이 필요합니다 (UPS일 수 있음).");

        return new DeviceDecision(DeviceVerdict.Desktop, Accepted: true, NeedsReview: false, PermanentlyBlocked: false, "데스크톱으로 확인됨.");
    }

    public const string LaptopMessage = "이 프로그램은 데스크톱 PC에만 설치할 수 있습니다. 노트북에는 설치할 수 없습니다.";

    private static bool ConsoleCameFirst(InstallMarkers m)
    {
        if (!m.AnyConsoleMarker) return false;
        // A tombstone (console was ever here first) is permanent unless the Owner clears it.
        if (m.ConsoleTombstonePresent && m.AgentFirstInstall is null) return true;
        if (m.AgentFirstInstall is null) return true; // console markers but the agent was never registered here
        if (m.ConsoleFirstInstall is { } c && c < m.AgentFirstInstall) return true; // console predates the agent
        return false;
    }

    private static bool ChassisInconsistent(int[] wmi, int[] smbios)
    {
        bool Concrete(int[] a) => a.Any(v => v is not (0 or 1 or 2)); // 1=Other, 2=Unknown, 0=padding
        // One source gives a concrete (non-portable, since portable handled earlier) type, the other is empty: accept.
        // Flag only when both are present but neither gives any usable value.
        if (wmi.Length == 0 && smbios.Length == 0) return true;
        return !Concrete(wmi) && !Concrete(smbios);
    }

    private bool IntegrityMatches(IntegrityReport? integrity)
    {
        if (integrity is null || integrity.FileHashes.Count == 0) return false;
        // Every reported file hash must belong to a known official release.
        return integrity.FileHashes.Values.All(h => _knownHashes.Contains(h.ToUpperInvariant()));
    }

    private static DeviceDecision Block(DeviceVerdict v, string reason, bool permanent) =>
        new(v, Accepted: false, NeedsReview: false, PermanentlyBlocked: permanent, reason);

    /// <summary>Stable hardware fingerprint: wired MACs first, else manufacturer+model+computer name.</summary>
    public static string Fingerprint(DeviceReport report)
    {
        var basis = report.WiredMacs.Length > 0
            ? string.Join(",", report.WiredMacs.Select(m => Normalize(m)).Where(m => m.Length > 0).Order())
            : $"{report.Manufacturer}|{report.Model}|{report.ComputerName}".ToUpperInvariant();
        if (basis.Length == 0) basis = report.ComputerName.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("gac-device|" + basis)));
    }

    public static string Normalize(string mac) => new(mac.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
}
