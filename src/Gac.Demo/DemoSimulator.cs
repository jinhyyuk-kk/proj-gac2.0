using Gac.Shared;
using Gac.Shared.Contracts;

namespace Gac.Demo;

public sealed record ModuleSimResult(bool Success, string Summary, string? DataJson);

/// <summary>Produces realistic module results for virtual PCs. Pure computation – nothing touches the real machine.</summary>
public sealed class DemoSimulator(Random rng)
{
    private static string J(object o) => GacJson.Serialize(o);
    private static string Size(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.##}GB" : $"{b / (double)(1L << 20):0.#}MB";

    public ModuleSimResult Run(DemoPc pc, string module, string action, string payload, GroupPolicy? policy, ServerSettings settings) => (module, action) switch
    {
        (ModuleIds.Clean, _) => Clean(pc, payload, policy),
        (ModuleIds.Shutdown, ModuleActions.ShutdownNow) => Shutdown(pc, payload),
        (ModuleIds.Shutdown, _) => new(true, "예약된 종료를 취소했습니다.", null),
        (ModuleIds.Errors, _) => Errors(pc),
        (ModuleIds.Update, ModuleActions.UpdateScan) => UpdateScan(pc),
        (ModuleIds.Update, _) => UpdateInstall(pc),
        (ModuleIds.Deploy, ModuleActions.DeployInstall) => DeployInstall(pc, payload),
        (ModuleIds.Deploy, _) => RunScript(pc, payload),
        (ModuleIds.Health, _) => Health(pc),
        (ModuleIds.Inspect, ModuleActions.InspectRun) => Inspect(pc, payload, settings),
        (ModuleIds.Inspect, _) => Uninstall(pc, payload),
        (ModuleIds.NetDiag, _) => NetDiag(pc),
        (ModuleIds.ClassMode, _) => new(true, $"공용 프리셋 적용됨 ({DateTimeOffset.Now.AddDays(-3):yyyy-MM-dd HH:mm} 버전)", null),
        _ => new(false, "알 수 없는 명령입니다.", null),
    };

    private ModuleSimResult Clean(DemoPc pc, string payload, GroupPolicy? policy)
    {
        var req = GacJson.Deserialize<CleanRequest>(payload);
        var dry = req?.DryRun ?? policy?.Clean.DryRun ?? true;
        var user = pc.User ?? "student";
        var targets = new List<CleanTargetResult>
        {
            new("Windows 임시 파일", rng.Next(40, 600) * 1024L * 1024, rng.Next(200, 3000), rng.Next(0, 8)),
            new($"사용자 임시 파일 ({user})", rng.Next(80, 900) * 1024L * 1024, rng.Next(500, 9000), rng.Next(0, 15)),
            new($"Chrome 캐시 ({user})", rng.Next(100, 1200) * 1024L * 1024, rng.Next(1000, 12000), 0),
            new($"휴지통 (C:)", rng.Next(0, 800) * 1024L * 1024, rng.Next(0, 300), 0),
            new($"다운로드 14일 지난 파일 ({user})", rng.Next(0, 1500) * 1024L * 1024, rng.Next(0, 60), rng.Next(0, 2)),
        };
        var total = targets.Sum(t => t.Bytes);
        var samples = new[] { $@"C:\Users\{user}\AppData\Local\Temp\tmp{rng.Next(1000, 9999)}.tmp", $@"C:\Windows\Temp\MSI{rng.Next(10000, 99999)}.LOG",
            $@"C:\Users\{user}\Downloads\수행평가_자료_{rng.Next(1, 9)}.hwp", $@"C:\Users\{user}\AppData\Local\Google\Chrome\User Data\Default\Cache\Cache_Data\f_{rng.Next(100000, 999999):x}" };
        var report = new CleanReport(dry, total, targets.Sum(t => t.Files), targets.Sum(t => t.Skipped), rng.Next(3, 40) + rng.NextDouble(), targets, samples);
        if (!dry) pc.FreePercent = Math.Min(95, pc.FreePercent + total * 100.0 / pc.DiskTotal);
        var summary = dry
            ? $"[미리 보기] 삭제 예정 {Size(total)}, 파일 {report.FileCount:N0}개 (건너뜀 {report.SkippedCount}개)"
            : $"정리 완료: {Size(total)} 확보, 파일 {report.FileCount:N0}개 삭제 (건너뜀 {report.SkippedCount}개)";
        return new(true, summary, J(report));
    }

    private static ModuleSimResult Shutdown(DemoPc pc, string payload)
    {
        var req = GacJson.Deserialize<ShutdownNowRequest>(payload) ?? new ShutdownNowRequest();
        return new(true, pc.User == null ? "로그인한 사용자가 없어 경고 없이 종료합니다. (체험 모드: 실제로는 종료하지 않음)"
            : $"{req.DelayMinutes}분 뒤 종료하도록 예약했습니다 (사용자에게 경고 표시). (체험 모드)", null);
    }

    private ModuleSimResult Errors(DemoPc pc)
    {
        var events = new List<ErrorEventSummary>();
        var now = DateTimeOffset.Now;
        if (pc.RecentErrors > 0)
        {
            events.Add(new("System", "disk", 7, "Error", pc.RecentErrors, now.AddHours(-rng.Next(1, 20)), "장치 \\Device\\Harddisk0\\DR0에 불량 블록이 있습니다."));
            if (pc.RecentErrors > 2) events.Add(new("System", "Microsoft-Windows-Kernel-Power", 41, "Critical", 1, now.AddDays(-1), "시스템이 정상적으로 종료되지 않은 상태에서 다시 부팅되었습니다."));
            events.Add(new("Application", "Application Error", 1000, "Error", rng.Next(1, 4), now.AddHours(-3), "오류 있는 응용 프로그램 이름: hwp.exe"));
        }
        var services = pc.RecentErrors > 3 ? new List<FailedService> { new("Spooler", "Print Spooler", "Stopped") } : [];
        var report = new ErrorsReport(now.AddHours(-6), events, services);
        var total = events.Sum(e => e.Count);
        return new(true, total == 0 && services.Count == 0 ? "오류 없음" : $"오류 이벤트 {total}건 ({events.Count}종), 멈춘 자동 시작 서비스 {services.Count}개", J(report));
    }

    private List<PendingUpdate> Pending(int n) => Enumerable.Range(0, n).Select(i => i switch
    {
        0 => new PendingUpdate("2026-10 x64 기반 시스템용 Windows 11 누적 업데이트", "KB5046" + rng.Next(100, 999), true, 712),
        1 => new PendingUpdate("x64용 Microsoft Defender 바이러스 백신 보안 인텔리전스 업데이트", "KB2267602", true, 98),
        2 => new PendingUpdate(".NET Framework 4.8.1 누적 업데이트", "KB5045" + rng.Next(100, 999), false, 70),
        _ => new PendingUpdate("Windows 악성 소프트웨어 제거 도구 x64", "KB890830", false, 54),
    }).ToList();

    private ModuleSimResult UpdateScan(DemoPc pc)
    {
        var r = new UpdateReport(true, null, Pending(pc.PendingUpdates), DateTimeOffset.Now.AddDays(-rng.Next(3, 40)), pc.RebootRequired);
        var s = (pc.PendingUpdates == 0 ? "대기 중인 업데이트 없음" : $"대기 중인 업데이트 {pc.PendingUpdates}개 (보안 {r.Pending.Count(p => p.IsSecurity)}개)") +
                (pc.RebootRequired ? " · 재부팅 필요" : "") + $" · 마지막 설치 {r.LastSuccessfulInstall:yyyy-MM-dd}";
        return new(true, s, J(r));
    }

    private ModuleSimResult UpdateInstall(DemoPc pc)
    {
        if (pc.Name.EndsWith("-08"))
            return new(false, "업데이트 원본(WSUS 또는 Microsoft Update)에 연결할 수 없습니다. 이 PC의 업데이트 원본 설정과 네트워크를 확인하세요.",
                J(new UpdateReport(false, "업데이트 원본에 연결할 수 없습니다.", [], DateTimeOffset.Now.AddDays(-45), false, 0, "실패")));
        var n = pc.PendingUpdates;
        var pending = Pending(n);
        pc.PendingUpdates = 0;
        pc.RebootRequired = n > 0;
        var r = new UpdateReport(true, null, pending, DateTimeOffset.Now, pc.RebootRequired, n, n == 0 ? "설치할 업데이트가 없습니다." : "설치 완료");
        return new(true, (n == 0 ? "설치할 업데이트가 없습니다." : $"설치 완료 ({n}개)") + (pc.RebootRequired ? " · 재부팅 필요" : ""), J(r));
    }

    private ModuleSimResult DeployInstall(DemoPc pc, string payload)
    {
        var req = GacJson.Deserialize<DeployPackageRequest>(payload)!;
        var fail = rng.Next(0, 12) == 0;
        var r = new DeployResult(fail ? 1603 : 0, false, "", fail ? "치명적인 설치 오류 (1603)" : "", Math.Round(rng.Next(8, 90) + rng.NextDouble(), 1));
        return new(!fail, $"{req.FileName}: {(fail ? "설치 실패 (종료 코드 1603)" : "설치 완료")}", J(r));
    }

    private ModuleSimResult RunScript(DemoPc pc, string payload)
    {
        var req = GacJson.Deserialize<RunScriptRequest>(payload)!;
        var lines = req.Script.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var output = "(체험 모드 – 명령을 실제로 실행하지 않고 결과를 흉내 냅니다)\n" + string.Join("\n", lines.Select(l =>
            l.Trim().StartsWith("echo ", StringComparison.OrdinalIgnoreCase) ? l.Trim()[5..] : l.Trim().Equals("hostname", StringComparison.OrdinalIgnoreCase) ? pc.Name : $"{pc.Name}> {l.Trim()}  … 완료"));
        return new(true, "명령 실행 성공: 종료 코드 0", J(new DeployResult(0, false, output, "", 0.4)));
    }

    private ModuleSimResult Health(DemoPc pc)
    {
        var free = (long)(pc.DiskTotal * pc.FreePercent / 100);
        var warnings = new List<string>();
        if (pc.FreePercent < 10) warnings.Add($"C: 남은 공간 {pc.FreePercent:0.#}% (10% 미만)");
        var diskStatus = pc.RecentErrors > 2 ? "주의" : "정상";
        if (diskStatus != "정상") warnings.Add("디스크 'Samsung SSD 870 EVO 250GB' 상태 주의");
        var ram = 8L * 1024 * 1024 * 1024;
        var used = (long)(ram * (0.35 + rng.NextDouble() * 0.5));
        var uptime = rng.Next(1, 90) + rng.NextDouble();
        var r = new HealthReport([new VolumeHealth("C:", null, pc.DiskTotal, free, Math.Round(pc.FreePercent, 1))],
            [new DiskHealth("Samsung SSD 870 EVO 250GB", diskStatus, rng.Next(2000, 15000), rng.Next(30, 45), pc.RecentErrors * 3, rng.Next(2, 30))],
            ram, used, Math.Round(uptime, 1), DateTimeOffset.Now.AddHours(-uptime), Math.Round(15 + rng.NextDouble() * 40, 1), warnings);
        var summary = $"C: {pc.FreePercent:0}% 남음 · 메모리 {used * 100 / ram}% 사용 · 가동 {uptime:0.#}시간";
        if (warnings.Count > 0) summary = "⚠ " + string.Join(", ", warnings) + " · " + summary;
        return new(true, summary, J(r));
    }

    private ModuleSimResult Inspect(DemoPc pc, string payload, ServerSettings settings)
    {
        var req = GacJson.Deserialize<InspectRequest>(payload) ?? new InspectRequest();
        var items = new List<InspectItem>();
        var someone = pc.User != null;
        if (req.ResetWallpaper) items.Add(someone ? new("바탕화면 초기화", true, "학교 기본 바탕화면으로 바꿨습니다.") : new("바탕화면 초기화", null, "로그인한 사용자가 없어 건너뜀"));
        if (req.LedTest)
        {
            var bad = pc.Name.Contains("전자칠판") && rng.Next(0, 3) == 0;
            items.Add(someone ? new("모니터/전자칠판 LED 점검", !bad, bad ? "이상 있음: 왼쪽 아래 줄무늬" : "이상 없음") : new("모니터/전자칠판 LED 점검", null, "로그인한 사용자가 없어 건너뜀"));
        }
        if (req.CheckUpdates)
        {
            items.Add(new("Windows 업데이트", pc.PendingUpdates == 0 && !pc.RebootRequired, $"마지막 설치 {DateTimeOffset.Now.AddDays(-rng.Next(2, 30)):yyyy-MM-dd}" + (pc.RebootRequired ? " · 재부팅 필요" : "")));
            items.Add(new("Windows 에디션", true, pc.Edition + " 23H2"));
        }
        var unwanted = new List<InstalledProgram>();
        if (req.CheckPrograms)
        {
            foreach (var p in pc.ExtraPrograms) unwanted.Add(new InstalledProgram(p, "알 수 없음", "1.0", false, true, "HKLM64|{" + p.GetHashCode().ToString("X8") + "}"));
            items.Add(new("불필요한 프로그램", unwanted.Count == 0, unwanted.Count == 0 ? "허용 목록 밖 프로그램 없음" : $"허용 목록에 없는 프로그램 {unwanted.Count}개"));
        }
        var report = new InspectReport(items, pc.Edition, "23H2 (빌드 22631)", unwanted);
        var failed = items.Count(i => i.Passed == false);
        var summary = failed == 0 ? $"점검 통과 ({items.Count(i => i.Passed == true)}개 항목)" : $"점검 항목 {failed}개 불합격";
        if (unwanted.Count > 0) summary += $" · 허용 목록에 없는 프로그램 {unwanted.Count}개";
        return new(true, summary, J(report));
    }

    private ModuleSimResult Uninstall(DemoPc pc, string payload)
    {
        var req = GacJson.Deserialize<UninstallRequest>(payload)!;
        var results = new List<UninstallResult>();
        foreach (var key in req.Keys)
        {
            var name = pc.ExtraPrograms.FirstOrDefault(p => key.Contains(p.GetHashCode().ToString("X8")));
            if (name == null) continue;
            pc.ExtraPrograms.Remove(name);
            results.Add(new UninstallResult(key, name, true, "제거 완료"));
        }
        return results.Count == 0 ? new(true, "이 PC에는 제거할 프로그램이 없습니다.", J(results)) : new(true, $"프로그램 제거 {results.Count}/{results.Count}개 성공", J(results));
    }

    private ModuleSimResult NetDiag(DemoPc pc)
    {
        var checks = new List<NetCheck>
        {
            new("네트워크 어댑터", NetCheckStatus.Ok, "연결됨 (이더넷)"),
            new("IP 주소", NetCheckStatus.Ok, pc.Ip + " (자동)"),
            new("게이트웨이", NetCheckStatus.Ok, "10.10.0.1 응답함"),
            pc.DnsBroken ? new("DNS", NetCheckStatus.Failed, "'ADMIN-PC' 이름을 찾지 못했습니다 (DNS 서버: 8.8.4.4).") : new("DNS", NetCheckStatus.Ok, "'ADMIN-PC' 이름 확인 성공"),
            new("GAC 서버", NetCheckStatus.Ok, "ADMIN-PC:8443 연결됨"),
        };
        var ok = !pc.DnsBroken;
        var summary = ok ? "네트워크 연결 정상" : "문제 발견: DNS";
        return new(true, summary, J(new NetDiagReport(checks, [], ok, summary)));
    }
}
