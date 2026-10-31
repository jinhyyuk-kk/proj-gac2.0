using System.Text;
using Gac.Shared;
using Gac.Shared.Contracts;

namespace Gac.Console.UI;

/// <summary>Turns module result JSON into readable Korean text for the console detail panes.</summary>
public static class ReportFormatter
{
    public static string Format(string module, string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson)) return "(자세한 내용 없음)";
        try
        {
            return module switch
            {
                ModuleIds.Clean => Clean(GacJson.Deserialize<CleanReport>(dataJson)!),
                ModuleIds.Health => Health(GacJson.Deserialize<HealthReport>(dataJson)!),
                ModuleIds.Errors => Errors(GacJson.Deserialize<ErrorsReport>(dataJson)!),
                ModuleIds.Update => Update(GacJson.Deserialize<UpdateReport>(dataJson)!),
                ModuleIds.Deploy => DeployOrUninstall(dataJson),
                ModuleIds.Inspect => InspectOrUninstall(dataJson),
                ModuleIds.NetDiag => NetDiag(GacJson.Deserialize<NetDiagReport>(dataJson)!),
                ModuleIds.Shutdown => Shutdown(GacJson.Deserialize<ShutdownLog>(dataJson)!),
                _ => dataJson,
            };
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or NullReferenceException) { return dataJson; }
    }

    public static string Bytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.##}GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.#}MB",
        >= 1L << 10 => $"{b / 1024.0:0}KB",
        _ => $"{b}B",
    };

    private static string Clean(CleanReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.DryRun ? "[미리 보기 – 실제로 삭제하지 않았습니다]" : "[정리 실행]");
        sb.AppendLine($"합계: {Bytes(r.BytesFreed)}, 파일 {r.FileCount:N0}개, 건너뜀 {r.SkippedCount:N0}개, {r.DurationSeconds:0.#}초");
        sb.AppendLine();
        foreach (var t in r.Targets) sb.AppendLine($"  • {t.Target}: {Bytes(t.Bytes)} ({t.Files:N0}개, 건너뜀 {t.Skipped})");
        if (r.SampleFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(r.DryRun ? "삭제 예정 파일 예시:" : "삭제한 파일 예시:");
            foreach (var f in r.SampleFiles.Take(30)) sb.AppendLine("    " + f);
        }
        return sb.ToString();
    }

    private static string Health(HealthReport r)
    {
        var sb = new StringBuilder();
        if (r.Warnings.Count > 0) { sb.AppendLine("⚠ 주의:"); foreach (var w in r.Warnings) sb.AppendLine("  • " + w); sb.AppendLine(); }
        sb.AppendLine("디스크 공간:");
        foreach (var v in r.Volumes) sb.AppendLine($"  • {v.Name} {v.Label}: {Bytes(v.FreeBytes)} 남음 / {Bytes(v.TotalBytes)} ({v.FreePercent:0.#}%)");
        if (r.Disks.Count > 0)
        {
            sb.AppendLine("디스크 상태:");
            foreach (var d in r.Disks)
                sb.AppendLine($"  • {d.Model}: {d.HealthStatus}" + (d.PowerOnHours is { } h ? $", 사용 {h:N0}시간" : "") + (d.Temperature is { } t ? $", {t}℃" : "") + (d.Wear is { } w ? $", 마모 {w}%" : ""));
        }
        if (r.TotalRamBytes > 0) sb.AppendLine($"메모리: {Bytes(r.UsedRamBytes)} / {Bytes(r.TotalRamBytes)} ({r.UsedRamBytes * 100 / r.TotalRamBytes}%)");
        sb.AppendLine($"가동 시간: {r.UptimeHours:0.#}시간 (부팅 {r.BootTime.ToLocalTime():yyyy-MM-dd HH:mm})");
        if (r.LastBootDurationSeconds is { } bd) sb.AppendLine($"마지막 부팅 소요: {bd:0.#}초");
        return sb.ToString();
    }

    private static string Errors(ErrorsReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"수집 기간: {r.Since.ToLocalTime():yyyy-MM-dd HH:mm} 이후");
        if (r.FailedServices.Count > 0)
        {
            sb.AppendLine("멈춘 자동 시작 서비스:");
            foreach (var s in r.FailedServices) sb.AppendLine($"  • {s.DisplayName} ({s.Name}) – {s.Status}");
        }
        sb.AppendLine(r.Events.Count == 0 ? "오류 이벤트 없음" : "오류 이벤트:");
        foreach (var e in r.Events)
            sb.AppendLine($"  • [{(e.Level == "Critical" ? "위험" : "오류")}] {e.Log}/{e.Source} #{e.EventId} ×{e.Count} (마지막 {e.LastSeen.ToLocalTime():MM-dd HH:mm})\n      {e.Message}");
        return sb.ToString();
    }

    private static string Update(UpdateReport r)
    {
        var sb = new StringBuilder();
        if (!r.SourceReachable) sb.AppendLine("✖ " + (r.SourceError ?? "업데이트 원본에 연결할 수 없습니다."));
        if (r.InstallResult != null) sb.AppendLine($"설치 결과: {r.InstallResult} ({r.InstalledCount ?? 0}개)");
        sb.AppendLine($"마지막 설치 성공: {(r.LastSuccessfulInstall is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "기록 없음")}");
        sb.AppendLine($"재부팅 필요: {(r.RebootRequired ? "예 (GAC는 자동으로 재부팅하지 않습니다)" : "아니요")}");
        sb.AppendLine($"대기 중인 업데이트 {r.Pending.Count}개:");
        foreach (var p in r.Pending) sb.AppendLine($"  • {(p.IsSecurity ? "[보안] " : "")}{p.Title} {p.Kb} ({p.SizeMb:0.#}MB)");
        return sb.ToString();
    }

    private static string DeployOrUninstall(string json)
    {
        var r = GacJson.Deserialize<DeployResult>(json)!;
        var sb = new StringBuilder();
        sb.AppendLine(r.TimedOut ? "시간 초과로 중단됨" : $"종료 코드: {r.ExitCode}");
        sb.AppendLine($"소요 시간: {r.DurationSeconds:0.#}초");
        if (!string.IsNullOrWhiteSpace(r.StdOut)) { sb.AppendLine("--- 출력 ---"); sb.AppendLine(r.StdOut.TrimEnd()); }
        if (!string.IsNullOrWhiteSpace(r.StdErr)) { sb.AppendLine("--- 오류 출력 ---"); sb.AppendLine(r.StdErr.TrimEnd()); }
        return sb.ToString();
    }

    private static string InspectOrUninstall(string json)
    {
        if (json.TrimStart().StartsWith('['))
        {
            var list = GacJson.Deserialize<List<UninstallResult>>(json)!;
            if (list.Count == 0) return "제거한 프로그램이 없습니다.";
            return string.Join(Environment.NewLine, list.Select(u => $"{(u.Success ? "✔" : "✖")} {u.Name}: {u.Detail}"));
        }
        var r = GacJson.Deserialize<InspectReport>(json)!;
        var sb = new StringBuilder();
        sb.AppendLine($"Windows: {r.WindowsEdition} {r.WindowsVersion}");
        sb.AppendLine();
        foreach (var i in r.Items) sb.AppendLine($"{(i.Passed switch { true => "✔ 합격", false => "✖ 불합격", null => "– 미실시" })}  {i.Name}: {i.Detail}");
        if (r.UnwantedPrograms.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("허용 목록에 없는 프로그램:");
            foreach (var p in r.UnwantedPrograms) sb.AppendLine($"  • {p.Name} {p.Version} ({p.Publisher}){(p.CanUninstall ? "" : " – 자동 제거 불가")}");
        }
        return sb.ToString();
    }

    private static string NetDiag(NetDiagReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary);
        sb.AppendLine();
        foreach (var c in r.Checks)
            sb.AppendLine($"{c.Status switch { NetCheckStatus.Ok => "✔", NetCheckStatus.Repaired => "✔", NetCheckStatus.Warning => "⚠", NetCheckStatus.Failed => "✖", _ => "–" }} {c.Name}: {c.Message}");
        if (r.RepairsTried.Count > 0) { sb.AppendLine(); sb.AppendLine("시도한 자동 수리: " + string.Join(", ", r.RepairsTried)); }
        return sb.ToString();
    }

    private static string Shutdown(ShutdownLog l) =>
        $"{l.At.ToLocalTime():yyyy-MM-dd HH:mm} · {l.Outcome switch { ShutdownOutcome.ShutDown => "종료", ShutdownOutcome.Extended => "연장", ShutdownOutcome.Cancelled => "취소", ShutdownOutcome.Forced => "강제 종료", _ => "건너뜀" }}" +
        $" · 연장 {l.ExtensionsUsed}회" + (l.By != null ? $" · {l.By}" : "");

    /// <summary>
    /// Simple CSV (UTF-8 with BOM so Excel opens Korean correctly). Cells come partly from PCs (program names, event
    /// text), so cells starting with a formula character are prefixed with ' to prevent CSV/formula injection.
    /// </summary>
    public static string ToCsv(IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        foreach (var r in rows) sb.AppendLine(string.Join(",", r.Select(c => "\"" + Neutralize(c ?? "").Replace("\"", "\"\"") + "\"")));
        return sb.ToString();
    }

    public static string Neutralize(string cell) =>
        cell.Length > 0 && cell[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + cell : cell;
}
