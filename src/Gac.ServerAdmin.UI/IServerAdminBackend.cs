using Gac.Shared.Contracts;

namespace Gac.ServerAdmin.UI;

/// <summary>Snapshot shown in the 상태 section of GAC 서버 관리 (CHANGE-002 §4.1).</summary>
public sealed record ServerAdminStatus(
    bool ServiceRunning,
    bool InstallComplete,
    string ServerName,
    int Port,
    IReadOnlyList<string> Ranges,
    bool FirewallRuleOk,
    int PcCount,
    int OnlineCount,
    DateTimeOffset? LastEnrollment,
    int BlockedCount,
    string? SetupCode,
    string Thumbprint);

public sealed record ServerLogLine(DateTimeOffset At, string Level, string Text);

/// <summary>
/// Everything the GAC 서버 관리 window needs. The real backend (Gac.ServerStatus) talks only to the local service and
/// database and asks for Windows administrator rights; the demo backend is in memory. Methods return a Korean error
/// message, or null on success, so the UI never needs to know which backend it has.
/// </summary>
public interface IServerAdminBackend
{
    bool IsDemo { get; }
    ServerAdminStatus GetStatus();

    string? ServiceControl(string verb);          // "start" | "stop" | "restart"
    string? DetectRange();                        // suggestion for the firewall step / re-detect
    string? ReapplyDetectedRange();               // [대역 다시 감지]
    string? AddRange(string cidr);                // [대역 추가]
    /// <summary>[방화벽 열기]: TCP, that port only, remote = that range only, all profiles; read back to verify.</summary>
    string? OpenFirewall(string cidr, int port);
    string? ExportRollout(IWin32Window owner);    // [클라이언트 설치 파일 만들기]

    ServerShutdownSettings GetShutdown();
    string? SaveShutdown(ServerShutdownSettings settings);
    ServerShutdownStatus GetShutdownStatus();
    string? SkipShutdownToday();
    string? ExtendShutdown();

    OverloadSettings GetOverload();
    string? SaveOverload(OverloadSettings settings);
    IReadOnlyList<OverloadBlock> GetBlocks();
    string? Unblock(string key);
    string? BlockPermanently(string key);

    IReadOnlyList<ServerLogLine> GetLogs();
    string? OpenLogFolder();
}
