using Gac.Console.Services;
using Gac.ServerAdmin.UI;
using Gac.Shared.Contracts;

namespace Gac.Demo;

/// <summary>
/// In-memory backend for the real GAC 서버 관리 UI inside the demo (CHANGE-002 §5). It reads and writes the same fake
/// server as the console view, so a schedule changed here shows up in the Console's 서버 관리 and vice versa.
/// No service, firewall, file or process is ever touched.
/// </summary>
public sealed class DemoServerAdminBackend(DemoServerClient server) : IServerAdminBackend
{
    private const string Actor = "Windows 관리자(서버 PC)";
    private readonly List<ServerLogLine> _logs = [];
    private readonly Random _rng = new();
    private bool _running = true;

    public bool IsDemo => true;

    public ServerAdminStatus GetStatus()
    {
        var (pcs, online, blocked, last) = server.ServerCounts();
        var complete = server.InstallComplete;
        return new ServerAdminStatus(_running && complete, complete, "ADMIN-PC", server.Port,
            server.LanRange.Split(", ", StringSplitOptions.RemoveEmptyEntries), complete, pcs, _running ? online : 0, last, blocked,
            server.SetupCodeForDisplay, "체험 모드 (인증서 없음)");
    }

    public string? ServiceControl(string verb)
    {
        _running = verb != "stop";
        Log("정보", verb switch { "start" => "GAC 서버 서비스 시작 (체험)", "stop" => "GAC 서버 서비스 중지 (체험)", _ => "GAC 서버 서비스 다시 시작 (체험)" });
        return null;
    }

    public string? DetectRange() => "10.10.0.0/16";

    public string? ReapplyDetectedRange()
    {
        Log("정보", "교내망 대역 다시 감지: 10.10.0.0/16 → 방화벽 규칙 다시 적용 (체험)");
        return null;
    }

    public string? AddRange(string cidr)
    {
        if (!DemoServerClient.IsCidr(cidr)) return "대역 형식이 올바르지 않습니다 (예: 10.20.0.0/22).";
        server.AddRange(cidr);
        Log("정보", $"교내망 대역 추가: {cidr} (체험)");
        return null;
    }

    public string? OpenFirewall(string cidr, int port)
    {
        var err = server.OpenFirewall(cidr, port);
        Log(err == null ? "정보" : "오류", err == null ? $"방화벽 규칙 생성·확인: TCP {port}, 원격 주소 {cidr}, 모든 프로필 (체험)" : "방화벽 열기 실패: " + err);
        return err;
    }

    public string? ExportRollout(IWin32Window owner) =>
        "(체험) 실제 제품에서는 서버 주소·포트·인증서 지문·등록 코드가 담긴 gac-agent.json과 함께\n조용히(무인) 설치할 수 있는 클라이언트 설치 폴더를 만듭니다. 체험 모드에서는 파일을 만들지 않습니다.";

    public ServerShutdownSettings GetShutdown() => server.ServerShutdownNow();
    public string? SaveShutdown(ServerShutdownSettings s) => Try(() => server.SaveServerShutdown(s, Actor), "서버 PC 자동 종료 일정 변경");
    public ServerShutdownStatus GetShutdownStatus() => server.ServerShutdownStatusNow();
    public string? SkipShutdownToday() => Try(() => server.SkipServerShutdownToday(Actor), "오늘은 끄지 않기");
    public string? ExtendShutdown() => Try(() => server.ExtendServerShutdown(Actor), "30분 연장");

    public OverloadSettings GetOverload() => server.OverloadNow();
    public string? SaveOverload(OverloadSettings s) => Try(() => server.SaveOverload(s, Actor), "과부하 차단 한도 변경");
    public IReadOnlyList<OverloadBlock> GetBlocks() => server.BlocksNow();
    public string? Unblock(string key) => Try(() => server.Unblock(key, Actor), "차단 해제");
    public string? BlockPermanently(string key) => Try(() => server.BlockPermanently(key, Actor), "영구 차단");

    public IReadOnlyList<ServerLogLine> GetLogs()
    {
        // Live-looking fake traffic: a new line on each read.
        var pcs = server.Pcs;
        if (_running && pcs.Count > 0)
        {
            var pc = pcs[_rng.Next(pcs.Count)];
            var (level, text) = _rng.Next(8) switch
            {
                0 => ("정보", $"{pc.Name} 상태 보고 수신 (남은 공간 {pc.FreePercent:0}%)"),
                1 => ("정보", $"명령 전달 → {pc.Name}"),
                2 => ("경고", $"{pc.Name} 응답 지연 (재시도 대기)"),
                3 => ("정보", $"{pc.Name} 데스크톱 확인됨 (하드웨어 검사 통과)"),
                _ => ("정보", $"{pc.Name} 폴링 정상"),
            };
            Log(level, text);
        }
        return _logs.ToList();
    }

    public string? OpenLogFolder() => "(체험) 실제 제품에서는 C:\\ProgramData\\GAC\\Server\\logs 폴더를 엽니다.";

    public void Log(string level, string text)
    {
        _logs.Add(new ServerLogLine(DateTimeOffset.Now, level, text));
        if (_logs.Count > 400) _logs.RemoveRange(0, _logs.Count - 400);
    }

    private string? Try(Action a, string what)
    {
        try { a(); Log("정보", what + " (체험)"); return null; }
        catch (GacClientException e) { return e.Message; }
    }
}
