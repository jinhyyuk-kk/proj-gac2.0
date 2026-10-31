using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.ServiceProcess;
using Gac.Shared;
using Gac.Shared.Contracts;
using Gac.Shared.Modules;
using Microsoft.Win32;

namespace Gac.Modules.Errors;

public sealed record RawEvent(string Log, string Source, int EventId, string Level, DateTimeOffset Time, string Message);

public interface IErrorSource
{
    IEnumerable<RawEvent> ReadErrors(string log, DateTimeOffset since, int max);
    IEnumerable<FailedService> FailedAutoServices();
}

/// <summary>Reads Error/Critical events with an XPath query and stopped Automatic services.</summary>
public sealed class WindowsErrorSource : IErrorSource
{
    // Automatic services that are routinely stopped and only create noise.
    private static readonly HashSet<string> Benign = new(StringComparer.OrdinalIgnoreCase)
    {
        "gupdate", "edgeupdate", "MicrosoftEdgeElevationService", "sppsvc", "RemoteRegistry", "MapsBroker", "tiledatamodelsvc",
        "WbioSrvc", "clr_optimization_v4.0.30319_32", "clr_optimization_v4.0.30319_64", "GoogleUpdaterService", "GoogleUpdaterInternalService",
        "ShellHWDetection", "BITS", "UsoSvc", "WSearch", "DoSvc", "CDPSvc", "TrustedInstaller",
    };

    public IEnumerable<RawEvent> ReadErrors(string log, DateTimeOffset since, int max)
    {
        var ts = since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var query = new EventLogQuery(log, PathType.LogName, $"*[System[(Level=1 or Level=2) and TimeCreated[@SystemTime>='{ts}']]]")
        {
            ReverseDirection = true,
        };
        using var reader = new EventLogReader(query);
        for (var i = 0; i < max; i++)
        {
            using var e = reader.ReadEvent();
            if (e == null) yield break;
            string message;
            try { message = e.FormatDescription() ?? ""; } catch { message = ""; }
            if (message.Length == 0) message = $"(이벤트 ID {e.Id})";
            yield return new RawEvent(log, e.ProviderName ?? "?", e.Id, e.Level == 1 ? "Critical" : "Error",
                e.TimeCreated is { } t ? new DateTimeOffset(t) : DateTimeOffset.Now, message);
        }
    }

    public IEnumerable<FailedService> FailedAutoServices()
    {
        foreach (var s in ServiceController.GetServices())
        {
            using (s)
            {
                ServiceStartMode mode;
                ServiceControllerStatus status;
                try { mode = s.StartType; status = s.Status; } catch { continue; }
                if (mode != ServiceStartMode.Automatic || status == ServiceControllerStatus.Running || status == ServiceControllerStatus.StartPending) continue;
                if (Benign.Contains(s.ServiceName) || IsTriggerOrDelayed(s.ServiceName)) continue;
                yield return new FailedService(s.ServiceName, s.DisplayName, status.ToString());
            }
        }
    }

    private static bool IsTriggerOrDelayed(string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        if (k == null) return false;
        using var trig = k.OpenSubKey("TriggerInfo");
        return trig != null || (k.GetValue("DelayedAutostart") is int d && d == 1);
    }
}

/// <summary>Old GAC "error check", ported. Collects since the last successful report and deduplicates.</summary>
public sealed class ErrorsModule(IErrorSource source, string stateDir, TimeProvider time) : IAgentModule
{
    public string Id => ModuleIds.Errors;
    private string StatePath => Path.Combine(stateDir, "errors-last.txt");

    public Task<ModuleOutcome> ExecuteAsync(ModuleRequest request, CancellationToken ct) => Task.Run(() =>
    {
        if (request.Action != ModuleActions.ErrorsCollect) return ModuleOutcome.Fail("알 수 없는 명령입니다.");
        var now = time.GetUtcNow();
        var since = ReadSince(now);
        var max = Math.Clamp(request.Policy.Errors.MaxEvents, 10, 2000);
        var events = new List<RawEvent>();
        var failures = new List<string>();
        foreach (var log in new[] { "System", "Application" })
        {
            try { events.AddRange(source.ReadErrors(log, since, max)); }
            catch (Exception e) when (e is EventLogException or UnauthorizedAccessException) { failures.Add($"{log}: {e.Message}"); }
        }
        List<FailedService> services;
        try { services = source.FailedAutoServices().ToList(); }
        catch (Exception e) { services = []; failures.Add("services: " + e.Message); }

        var report = new ErrorsReport(since, Summarize(events), services);
        if (failures.Count == 0) WriteSince(now);
        var total = report.Events.Sum(e => e.Count);
        var summary = total == 0 && services.Count == 0
            ? "오류 없음"
            : $"오류 이벤트 {total}건 ({report.Events.Count}종), 멈춘 자동 시작 서비스 {services.Count}개";
        if (failures.Count > 0) summary += " · 일부 로그를 읽지 못함";
        return ModuleOutcome.Ok(summary, report);
    }, ct);

    public static IReadOnlyList<ErrorEventSummary> Summarize(IEnumerable<RawEvent> events) =>
        events.GroupBy(e => (e.Log, e.Source, e.EventId, e.Level))
            .Select(g =>
            {
                var latest = g.MaxBy(x => x.Time)!;
                var msg = latest.Message.ReplaceLineEndings(" ");
                return new ErrorEventSummary(g.Key.Log, g.Key.Source, g.Key.EventId, g.Key.Level, g.Count(), latest.Time, msg.Length > 300 ? msg[..300] + "…" : msg);
            })
            .OrderByDescending(s => s.Level == "Critical").ThenByDescending(s => s.Count).ThenByDescending(s => s.LastSeen)
            .ToList();

    private DateTimeOffset ReadSince(DateTimeOffset now)
    {
        try
        {
            if (File.Exists(StatePath) && long.TryParse(File.ReadAllText(StatePath), out var ms))
            {
                var t = DateTimeOffset.FromUnixTimeMilliseconds(ms);
                if (t < now && now - t < TimeSpan.FromDays(30)) return t;
            }
        }
        catch (IOException) { }
        return now.AddDays(-1);
    }

    private void WriteSince(DateTimeOffset now)
    {
        try { Directory.CreateDirectory(stateDir); File.WriteAllText(StatePath, now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)); }
        catch (IOException) { }
    }
}
