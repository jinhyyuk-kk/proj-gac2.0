using System.Runtime.InteropServices;
using Gac.Shared;
using Gac.Shared.Contracts;
using Gac.Shared.Modules;

namespace Gac.Modules.Update;

public interface IWindowsUpdateClient
{
    UpdateReport Scan(CancellationToken ct);
    UpdateReport Install(CancellationToken ct);
}

/// <summary>
/// Windows Update Agent API through late-bound COM (Microsoft.Update.Session). Uses whatever update source Windows is
/// already configured for; GAC never downloads anything itself and never reboots.
/// </summary>
public sealed class WuaClient : IWindowsUpdateClient
{
    private const string Criteria = "IsInstalled=0 and IsHidden=0 and Type='Software'";

    public UpdateReport Scan(CancellationToken ct) => OnMta<UpdateReport>(() =>
    {
        var (lastSuccess, reboot) = Status();
        try
        {
            dynamic session = Create("Microsoft.Update.Session");
            session.ClientApplicationID = "GAC 2.0";
            dynamic searcher = session.CreateUpdateSearcher();
            searcher.Online = true;
            dynamic result = searcher.Search(Criteria);
            return new UpdateReport(true, null, ToList(result.Updates), lastSuccess, reboot);
        }
        catch (COMException e)
        {
            return new UpdateReport(false, Describe(e), [], lastSuccess, reboot);
        }
    });

    public UpdateReport Install(CancellationToken ct) => OnMta<UpdateReport>(() =>
    {
        var (lastSuccess, reboot) = Status();
        try
        {
            dynamic session = Create("Microsoft.Update.Session");
            session.ClientApplicationID = "GAC 2.0";
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic found = searcher.Search(Criteria).Updates;
            var pending = ToList(found);
            if ((int)found.Count == 0) return new UpdateReport(true, null, [], lastSuccess, reboot, 0, "설치할 업데이트가 없습니다.");

            dynamic toInstall = Create("Microsoft.Update.UpdateColl");
            for (var i = 0; i < (int)found.Count; i++)
            {
                dynamic u = found.Item(i);
                if (!(bool)u.EulaAccepted) u.AcceptEula();
                if ((bool)u.InstallationBehavior.CanRequestUserInput) continue; // would hang unattended
                toInstall.Add(u);
            }
            ct.ThrowIfCancellationRequested();
            dynamic downloader = session.CreateUpdateDownloader();
            downloader.Updates = toInstall;
            downloader.Download();
            ct.ThrowIfCancellationRequested();

            dynamic ready = Create("Microsoft.Update.UpdateColl");
            for (var i = 0; i < (int)toInstall.Count; i++)
                if ((bool)toInstall.Item(i).IsDownloaded) ready.Add(toInstall.Item(i));
            if ((int)ready.Count == 0) return new UpdateReport(false, "업데이트를 내려받지 못했습니다. 업데이트 원본에 연결할 수 없는지 확인하세요.", pending, lastSuccess, reboot, 0, "다운로드 실패");

            dynamic installer = session.CreateUpdateInstaller();
            installer.Updates = ready;
            installer.AllowSourcePrompts = false;
            dynamic res = installer.Install();
            int code = res.ResultCode; // 2 = succeeded, 3 = succeeded with errors
            bool needReboot = res.RebootRequired;
            var text = code switch { 2 => "설치 완료", 3 => "일부 설치 완료", 4 => "설치 실패", 5 => "설치 중단됨", _ => $"결과 코드 {code}" };
            var (ls2, rb2) = Status();
            return new UpdateReport(true, null, pending, ls2 ?? lastSuccess, needReboot || rb2, (int)ready.Count, text);
        }
        catch (COMException e)
        {
            return new UpdateReport(false, Describe(e), [], lastSuccess, reboot, 0, "실패");
        }
    });

    private static (DateTimeOffset? LastSuccess, bool Reboot) Status()
    {
        DateTimeOffset? last = null;
        var reboot = false;
        try
        {
            dynamic au = Create("Microsoft.Update.AutoUpdate");
            object v = au.Results.LastInstallationSuccessDate;
            if (v is DateTime dt && dt.Year > 2000) last = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToLocalTime();
        }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        try
        {
            dynamic si = Create("Microsoft.Update.SystemInfo");
            reboot = (bool)si.RebootRequired;
        }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        return (last, reboot);
    }

    private static List<PendingUpdate> ToList(dynamic updates)
    {
        var list = new List<PendingUpdate>();
        for (var i = 0; i < (int)updates.Count; i++)
        {
            dynamic u = updates.Item(i);
            string? kb = null;
            if ((int)u.KBArticleIDs.Count > 0) kb = "KB" + (string)u.KBArticleIDs.Item(0);
            var security = false;
            for (var c = 0; c < (int)u.Categories.Count; c++)
                if (((string)u.Categories.Item(c).Name).Contains("Security", StringComparison.OrdinalIgnoreCase)) security = true;
            double size = 0;
            try { size = Convert.ToDouble(u.MaxDownloadSize) / (1024 * 1024); } catch { }
            list.Add(new PendingUpdate((string)u.Title, kb, security, Math.Round(size, 1)));
        }
        return list;
    }

    private static dynamic Create(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)!;

    public static string Describe(COMException e) => (uint)e.HResult switch
    {
        0x8024402C or 0x80072EE7 or 0x80072EFD or 0x80072EFE or 0x8024401C => "업데이트 원본(WSUS 또는 Microsoft Update)에 연결할 수 없습니다. 이 PC의 업데이트 원본 설정과 네트워크를 확인하세요.",
        0x8024002E => "Windows Update가 정책으로 꺼져 있습니다.",
        0x80240438 or 0x8024500C => "업데이트 원본에 접근이 차단되어 있습니다.",
        _ => $"Windows Update 오류 0x{(uint)e.HResult:X8}",
    };

    // WUA calls can block for a long time; run on a dedicated MTA thread.
    private static T OnMta<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception e) { error = e; } }) { IsBackground = true, Name = "GAC WUA" };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join();
        if (error != null) throw new InvalidOperationException("Windows Update call failed", error);
        return result;
    }
}

public sealed class UpdateModule(IWindowsUpdateClient client) : IAgentModule
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string Id => ModuleIds.Update;

    public async Task<ModuleOutcome> ExecuteAsync(ModuleRequest request, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return ModuleOutcome.Fail("이미 업데이트 작업이 진행 중입니다.");
        try
        {
            var report = request.Action switch
            {
                ModuleActions.UpdateScan => await Task.Run(() => client.Scan(ct), ct),
                ModuleActions.UpdateInstall => await Task.Run(() => client.Install(ct), ct),
                _ => null,
            };
            if (report == null) return ModuleOutcome.Fail("알 수 없는 명령입니다.");
            return new ModuleOutcome(report.SourceReachable, Summarize(report), report);
        }
        finally { _gate.Release(); }
    }

    public static string Summarize(UpdateReport r)
    {
        var parts = new List<string>();
        if (!r.SourceReachable) parts.Add(r.SourceError ?? "업데이트 원본에 연결할 수 없습니다.");
        else if (r.InstallResult != null) parts.Add($"{r.InstallResult} ({r.InstalledCount ?? 0}개)");
        else parts.Add(r.Pending.Count == 0 ? "대기 중인 업데이트 없음" : $"대기 중인 업데이트 {r.Pending.Count}개 (보안 {r.Pending.Count(p => p.IsSecurity)}개)");
        if (r.RebootRequired) parts.Add("재부팅 필요");
        if (r.LastSuccessfulInstall is { } t) parts.Add($"마지막 설치 {t:yyyy-MM-dd}");
        return string.Join(" · ", parts);
    }
}
