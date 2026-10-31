using System.Runtime.InteropServices;
using Gac.Shared;
using Gac.Shared.Contracts;
using Gac.Shared.Modules;
using Gac.Shared.Pipe;
using Microsoft.Win32;

namespace Gac.Modules.Inspect;

public sealed record UninstallEntry(string Key, string Name, string? Publisher, string? Version, string? QuietUninstall, string? MsiProductCode);

public interface IInspectSource
{
    (string? Edition, string? EditionId, string? Version) WindowsEdition();
    (DateTimeOffset? LastSuccess, bool RebootRequired) UpdateStatus();
    IReadOnlyList<UninstallEntry> InstalledPrograms();
}

public sealed class WindowsInspectSource : IInspectSource
{
    public (string? Edition, string? EditionId, string? Version) WindowsEdition()
    {
        using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var name = k?.GetValue("ProductName") as string;
        var build = k?.GetValue("CurrentBuild") as string;
        // Windows 11 still reports "Windows 10" in ProductName; correct it from the build number.
        if (name != null && int.TryParse(build, out var b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
        var ver = k?.GetValue("DisplayVersion") as string;
        return (name, k?.GetValue("EditionID") as string, ver == null ? build : $"{ver} (빌드 {build})");
    }

    public (DateTimeOffset? LastSuccess, bool RebootRequired) UpdateStatus()
    {
        DateTimeOffset? last = null;
        try
        {
            dynamic au = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.AutoUpdate", true)!)!;
            object v = au.Results.LastInstallationSuccessDate;
            if (v is DateTime dt && dt.Year > 2000) last = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToLocalTime();
        }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        using var rk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        return (last, rk != null);
    }

    public IReadOnlyList<UninstallEntry> InstalledPrograms()
    {
        var list = new List<UninstallEntry>();
        void Read(RegistryHive hive, RegistryView view, string prefix)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (root == null) return;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k == null) continue;
                var name = k.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                if (k.GetValue("ParentKeyName") != null) continue;
                if (k.GetValue("ReleaseType") is string rt && rt is "Update" or "Hotfix" or "Security Update") continue;
                var isMsi = k.GetValue("WindowsInstaller") is int wi && wi == 1 && Guid.TryParse(sub, out _);
                list.Add(new UninstallEntry($"{prefix}|{sub}", name.Trim(), k.GetValue("Publisher") as string, k.GetValue("DisplayVersion") as string,
                    k.GetValue("QuietUninstallString") as string, isMsi ? sub : null));
            }
        }
        Read(RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM64");
        Read(RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM32");
        return list.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(e => e.Name).ToList();
    }
}

/// <summary>End-of-term inspection (old GAC LITE). Produces a pass/fail checklist per PC (CLAUDE.md 4.9).</summary>
public sealed class InspectModule(ITrayBridge tray, IInspectSource source, IProcessRunner runner, TimeProvider time) : IAgentModule
{
    private static readonly string[] HomeEditions = ["Core", "CoreSingleLanguage", "CoreCountrySpecific", "CoreN"];
    public string Id => ModuleIds.Inspect;

    public async Task<ModuleOutcome> ExecuteAsync(ModuleRequest request, CancellationToken ct)
    {
        switch (request.Action)
        {
            case ModuleActions.InspectRun:
            {
                var req = GacJson.Deserialize<InspectRequest>(string.IsNullOrWhiteSpace(request.PayloadJson) ? "{}" : request.PayloadJson) ?? new InspectRequest();
                var report = await RunAsync(req, request.Policy.Inspect, ct);
                var failed = report.Items.Count(i => i.Passed == false);
                var summary = failed == 0 ? $"점검 통과 ({report.Items.Count(i => i.Passed == true)}개 항목)" : $"점검 항목 {failed}개 불합격";
                if (report.UnwantedPrograms.Count > 0) summary += $" · 허용 목록에 없는 프로그램 {report.UnwantedPrograms.Count}개";
                return ModuleOutcome.Ok(summary, report);
            }
            case ModuleActions.InspectUninstall:
            {
                var req = GacJson.Deserialize<UninstallRequest>(request.PayloadJson);
                if (req == null || req.Keys.Count == 0) return ModuleOutcome.Fail("제거할 프로그램을 선택하세요.");
                var results = await UninstallAsync(req.Keys, ct);
                var ok = results.Count(r => r.Success);
                return new ModuleOutcome(ok == results.Count, $"프로그램 제거 {ok}/{results.Count}개 성공", results);
            }
            default:
                return ModuleOutcome.Fail("알 수 없는 명령입니다.");
        }
    }

    public async Task<InspectReport> RunAsync(InspectRequest req, InspectPolicy policy, CancellationToken ct)
    {
        var items = new List<InspectItem>();

        if (req.ResetWallpaper)
        {
            if (!tray.AnyUserSession) items.Add(new("바탕화면 초기화", null, "로그인한 사용자가 없어 건너뜀"));
            else
            {
                var r = await tray.RequestAsync(ServiceEvent.ResetWallpaper, new WallpaperBody(policy.WallpaperPath), TimeSpan.FromSeconds(30), ct);
                items.Add(new("바탕화면 초기화", r?.Ok, r?.Message ?? "트레이 앱 응답 없음"));
            }
        }

        if (req.LedTest)
        {
            if (!tray.AnyUserSession) items.Add(new("모니터/전자칠판 LED 점검", null, "로그인한 사용자가 없어 건너뜀"));
            else
            {
                var r = await tray.RequestAsync(ServiceEvent.LedTest, null, TimeSpan.FromMinutes(15), ct);
                var led = r?.DataJson == null ? null : GacJson.Deserialize<LedTestResult>(r.DataJson);
                items.Add(led == null
                    ? new("모니터/전자칠판 LED 점검", null, r?.Message ?? "시간 안에 점검이 끝나지 않음")
                    : new("모니터/전자칠판 LED 점검", led.Passed, led.Passed ? "이상 없음" : "이상 있음" + (string.IsNullOrWhiteSpace(led.Note) ? "" : $": {led.Note}")));
            }
        }

        var (edition, editionId, version) = source.WindowsEdition();
        if (req.CheckUpdates)
        {
            var (last, reboot) = source.UpdateStatus();
            var recent = last.HasValue && time.GetLocalNow() - last.Value < TimeSpan.FromDays(60);
            items.Add(new("Windows 업데이트", recent && !reboot,
                (last.HasValue ? $"마지막 설치 {last:yyyy-MM-dd}" : "설치 기록 없음") + (reboot ? " · 재부팅 필요" : "") + (recent ? "" : " · 60일 이상 업데이트 안 됨")));
            var home = editionId != null && HomeEditions.Contains(editionId, StringComparer.OrdinalIgnoreCase);
            items.Add(new("Windows 에디션", editionId == null ? null : !home, $"{edition} {version}" + (home ? " · Home 에디션은 학교 관리에 적합하지 않음" : "")));
        }

        var unwanted = new List<InstalledProgram>();
        if (req.CheckPrograms)
        {
            foreach (var p in source.InstalledPrograms())
            {
                var allowed = IsAllowed(p.Name, p.Publisher, policy.Allowlist);
                if (!allowed) unwanted.Add(new InstalledProgram(p.Name, p.Publisher, p.Version, false, p.QuietUninstall != null || p.MsiProductCode != null, p.Key));
            }
            items.Add(new("불필요한 프로그램", unwanted.Count == 0, unwanted.Count == 0 ? "허용 목록 밖 프로그램 없음" : $"허용 목록에 없는 프로그램 {unwanted.Count}개"));
        }
        return new InspectReport(items, edition, version, unwanted);
    }

    public static bool IsAllowed(string name, string? publisher, IEnumerable<string> allowlist) =>
        allowlist.Any(a => !string.IsNullOrWhiteSpace(a) &&
            (name.Contains(a.Trim(), StringComparison.OrdinalIgnoreCase) || (publisher?.Contains(a.Trim(), StringComparison.OrdinalIgnoreCase) ?? false)));

    private async Task<List<UninstallResult>> UninstallAsync(IReadOnlyList<string> keys, CancellationToken ct)
    {
        var installed = source.InstalledPrograms().ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
        var results = new List<UninstallResult>();
        foreach (var key in keys.Distinct())
        {
            if (!installed.TryGetValue(key, out var p)) { results.Add(new(key, key, false, "설치된 프로그램 목록에서 찾을 수 없습니다.")); continue; }
            string file, args;
            if (p.MsiProductCode != null)
                (file, args) = (Path.Combine(Environment.SystemDirectory, "msiexec.exe"), $"/x {p.MsiProductCode} /qn /norestart");
            else if (p.QuietUninstall != null)
                (file, args) = (Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /c \"{p.QuietUninstall}\"");
            else { results.Add(new(key, p.Name, false, "자동(무인) 제거를 지원하지 않는 프로그램입니다. 직접 제거하세요.")); continue; }
            var r = await runner.RunAsync(file, args, TimeSpan.FromMinutes(20), ct);
            var ok = !r.TimedOut && r.ExitCode is 0 or 3010 or 1641;
            results.Add(new(key, p.Name, ok, r.TimedOut ? "시간 초과" : ok ? "제거 완료" : $"제거 실패 (종료 코드 {r.ExitCode})"));
        }
        return results;
    }
}
