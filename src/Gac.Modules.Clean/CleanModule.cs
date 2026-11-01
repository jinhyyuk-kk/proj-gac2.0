using System.Diagnostics;
using Gac.Shared;
using Gac.Shared.Contracts;
using Gac.Shared.Modules;

namespace Gac.Modules.Clean;

public sealed class CleanModule(ICleanEnvironment env, TimeProvider time) : IAgentModule
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Id => ModuleIds.Clean;

    public async Task<ModuleOutcome> ExecuteAsync(ModuleRequest request, CancellationToken ct)
    {
        if (request.Action != ModuleActions.CleanRun) return ModuleOutcome.Fail("알 수 없는 명령입니다.");
        if (LaptopPreRunCheck.IsLaptopOrUnknown()) return ModuleOutcome.Fail(LaptopPreRunCheck.Message); // check site #5
        var req = string.IsNullOrWhiteSpace(request.PayloadJson) ? null : GacJson.Deserialize<CleanRequest>(request.PayloadJson);
        var dryRun = req?.DryRun ?? request.Policy.Clean.DryRun;

        if (!await _gate.WaitAsync(0, ct)) return ModuleOutcome.Fail("이미 정리가 진행 중입니다.");
        try
        {
            return await Task.Run(() => Run(request.Policy.Clean, dryRun, ct), ct);
        }
        finally { _gate.Release(); }
    }

    public ModuleOutcome Run(CleanPolicy policy, bool dryRun, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var notes = new List<string>();
        var targets = CleanPlanner.BuildTargets(policy, env, notes);
        var cleaner = new SafeCleaner(time);
        var samples = new List<string>();
        var results = new List<CleanTargetResult>();
        foreach (var t in targets)
        {
            ct.ThrowIfCancellationRequested();
            var r = cleaner.Clean(t, dryRun, samples);
            if (r.Files > 0 || r.Skipped > 0) results.Add(r);
        }
        // Merge per-target rows with the same display name (e.g. recycle bin per SID).
        var merged = results.GroupBy(r => r.Target)
            .Select(g => new CleanTargetResult(g.Key, g.Sum(x => x.Bytes), g.Sum(x => x.Files), g.Sum(x => x.Skipped))).ToList();
        var report = new CleanReport(dryRun, merged.Sum(r => r.Bytes), merged.Sum(r => r.Files), merged.Sum(r => r.Skipped),
            sw.Elapsed.TotalSeconds, merged, samples);

        var size = FormatBytes(report.BytesFreed);
        var summary = dryRun
            ? $"[미리 보기] 삭제 예정 {size}, 파일 {report.FileCount:N0}개 (건너뜀 {report.SkippedCount:N0}개)"
            : $"정리 완료: {size} 확보, 파일 {report.FileCount:N0}개 삭제 (건너뜀 {report.SkippedCount:N0}개)";
        if (notes.Count > 0) summary += " · " + string.Join(", ", notes);
        return ModuleOutcome.Ok(summary, report);
    }

    public static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.##}GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.#}MB",
        >= 1L << 10 => $"{b / 1024.0:0}KB",
        _ => $"{b}B",
    };
}
