using System.Diagnostics;
using System.Net.Http.Json;
using Dapper;
using Gac.Server.Accounts;
using Gac.Server.Admin;
using Gac.Server.Data;
using Gac.Server.Hosting;
using Gac.ServerAdmin.UI;
using Gac.Shared;
using Gac.Shared.Contracts;
using Gac.Shared.Security;
using Gac.Tray.UI;

namespace Gac.ServerStatus;

/// <summary>
/// The real GAC 서버 관리 backend (CHANGE-002 §4). Runs elevated on the Admin PC and talks only to this machine:
/// settings and counts straight from the local database, the firewall through Gac.Server.exe verbs (which create and
/// verify the rule), and live overload blocks through the service's loopback admin endpoints.
/// </summary>
public sealed class LocalServerBackend : IServerAdminBackend
{
    private string DataDir { get; } = Environment.GetEnvironmentVariable("GAC_SERVER_DATA") is { Length: > 0 } d ? d : ServerOptions.DefaultDataDir;
    private static string Actor => $"Windows 관리자({Environment.UserName})";
    private Db OpenDb() { var db = new Db(Path.Combine(DataDir, "gac.db")); db.EnsureSchema(); return db; }

    public bool IsDemo => false;

    public ServerAdminStatus GetStatus()
    {
        var cfg = ServerIdentity.LoadConfig(DataDir);
        int pcs = 0, online = 0, blocked = 0; DateTimeOffset? last = null; string? setup = null; var thumb = "";
        try
        {
            var db = OpenDb();
            using var c = db.Open();
            pcs = c.ExecuteScalar<int>("SELECT COUNT(*) FROM pcs");
            online = c.ExecuteScalar<int>("SELECT COUNT(*) FROM pcs WHERE last_seen >= @c AND revoked = 0", new { c = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeMilliseconds() });
            blocked = c.ExecuteScalar<int>("SELECT COUNT(*) FROM devices WHERE status IN ('blocked','revoked')");
            if (c.ExecuteScalar<long?>("SELECT MAX(enrolled_at) FROM pcs") is { } ms) last = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
            // CHANGE-002 §3.2: the setup code is shown only after the firewall step, and only until the first sign-up.
            if (cfg.IsInstallComplete) setup = new AccountService(db, new AuditLog(db, TimeProvider.System), TimeProvider.System).GetSetupCode();
        }
        catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException) { }
        try { using var id = ServerIdentity.LoadOrCreate(DataDir); thumb = id.Thumbprint; } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { }
        try { blocked += Live<List<OverloadBlock>>(HttpMethod.Get, "overload/blocks")?.Count ?? 0; } catch (Exception e) when (IsTransport(e)) { }
        var ranges = cfg.Ranges ?? [];
        var firewallOk = ranges.Length > 0 && Firewall.Verify(Firewall.ReadBack(), cfg.Port, ranges) == null;
        return new ServerAdminStatus(ServiceRunning(), cfg.IsInstallComplete, Environment.MachineName, cfg.Port, ranges, firewallOk, pcs, online, last, blocked, setup, thumb);
    }

    public static bool ServiceRunning()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("sc.exe", "query " + ServerCli.ServiceName) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return o.Contains("RUNNING");
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public string? ServiceControl(string verb)
    {
        void Sc(string v) { using var p = Process.Start(new ProcessStartInfo("sc.exe", $"{v} {ServerCli.ServiceName}") { UseShellExecute = false, CreateNoWindow = true }); p?.WaitForExit(); }
        if (verb == "restart") { Sc("stop"); Thread.Sleep(2500); Sc("start"); } else Sc(verb);
        Thread.Sleep(1000);
        return null;
    }

    public string? DetectRange() => LanDetection.DetectCidr();
    public string? ReapplyDetectedRange() => Verb("apply-firewall");
    public string? AddRange(string cidr) => LanDetection.IsValidCidr(cidr) ? Verb($"apply-firewall --add \"{cidr}\"") : "대역 형식이 올바르지 않습니다 (예: 10.20.0.0/22).";
    public string? OpenFirewall(string cidr, int port) => LanDetection.IsValidCidr(cidr) ? Verb($"firewall --range \"{cidr}\" --port {port}") : "교내망 대역 형식이 올바르지 않습니다 (예: 10.20.0.0/22).";

    /// <summary>Runs a Gac.Server.exe verb that writes "OK" / "ERROR: ..." to a result file.</summary>
    private static string? Verb(string args)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Gac.Server.exe");
        if (!File.Exists(exe)) return "Gac.Server.exe 를 찾을 수 없습니다. 서버를 다시 설치하세요.";
        var result = Path.Combine(Path.GetTempPath(), "gac-verb-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, $"{args} --result \"{result}\"") { UseShellExecute = false, CreateNoWindow = true })!;
            p.WaitForExit();
            var text = File.Exists(result) ? File.ReadAllText(result).Trim() : $"ERROR: 결과를 읽지 못했습니다 (코드 {p.ExitCode}).";
            return text == "OK" ? null : text.StartsWith("ERROR: ", StringComparison.Ordinal) ? text[7..] : text;
        }
        catch (System.ComponentModel.Win32Exception e) { return e.Message; }
        finally { try { File.Delete(result); } catch (IOException) { } }
    }

    public string? ExportRollout(IWin32Window owner)
    {
        using var dlg = new FolderBrowserDialog { Description = "클라이언트 설치 파일을 저장할 폴더 선택" };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
        try
        {
            var cfg = ServerIdentity.LoadConfig(DataDir);
            using var id = ServerIdentity.LoadOrCreate(DataDir);
            var db = OpenDb();
            var code = new Gac.Server.Pcs.PcService(db, new SettingsStore(db), new AuditLog(db, TimeProvider.System), TimeProvider.System).EnrollCode;
            var target = Path.Combine(dlg.SelectedPath, "GAC-Agent-Rollout");
            RolloutPackage.WriteConfig(target, new AgentRolloutConfig(Environment.MachineName, cfg.Port, id.Thumbprint, code));
            return "클라이언트 설치 파일을 만들었습니다:\n" + target + "\n\n이 폴더에 GAC-Agent-Setup.exe 를 함께 넣어 교실 PC에서 실행하면 자동으로 등록됩니다.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "만들지 못했습니다: " + e.Message; }
    }

    // ---- server PC auto shutdown: same settings rows as the Console (the service re-reads them every tick) ----
    private ServerShutdownService Shutdown()
    {
        var db = OpenDb();
        return new ServerShutdownService(new SettingsStore(db), new AuditLog(db, TimeProvider.System), db, TimeProvider.System, new NoShutdown());
    }

    public ServerShutdownSettings GetShutdown() => Shutdown().Schedule;
    public string? SaveShutdown(ServerShutdownSettings s) => Try(() => Shutdown().SaveSchedule(Actor, s));
    public ServerShutdownStatus GetShutdownStatus() => Shutdown().Status();
    public string? SkipShutdownToday() => Try(() => Shutdown().SkipToday(Actor));
    public string? ExtendShutdown() => Try(() => Shutdown().Extend(Actor));

    // ---- overload: limits in the database (re-read by the service within 30 s), blocks live from the service ----
    private OverloadGuard Guard()
    {
        var db = OpenDb();
        return new OverloadGuard(db, new SettingsStore(db), new AuditLog(db, TimeProvider.System), TimeProvider.System);
    }

    public OverloadSettings GetOverload() => Guard().Settings;
    public string? SaveOverload(OverloadSettings s) => Try(() => Guard().SaveSettings(Actor, s));

    public IReadOnlyList<OverloadBlock> GetBlocks()
    {
        try { return Live<List<OverloadBlock>>(HttpMethod.Get, "overload/blocks") ?? []; }
        catch (Exception e) when (IsTransport(e)) { return []; }
    }

    public string? Unblock(string key) => LiveAction($"overload/blocks/{Uri.EscapeDataString(key)}/unblock");
    public string? BlockPermanently(string key) => LiveAction($"overload/blocks/{Uri.EscapeDataString(key)}/permanent");

    private string? LiveAction(string path)
    {
        try { Live<object>(HttpMethod.Post, path); return null; }
        catch (Exception e) when (IsTransport(e)) { return "GAC 서버 서비스에 연결할 수 없습니다. 서비스가 실행 중인지 확인하세요."; }
        catch (GacLocalException e) { return e.Message; }
    }

    private T? Live<T>(HttpMethod method, string path)
    {
        var cfg = ServerIdentity.LoadConfig(DataDir);
        var key = LocalAdmin.LoadOrCreate(DataDir);
        string thumb;
        using (var id = ServerIdentity.LoadOrCreate(DataDir)) thumb = id.Thumbprint;
        using var http = new HttpClient(CertPinning.CreatePinnedHandler(thumb)) { BaseAddress = new Uri($"https://127.0.0.1:{cfg.Port}"), Timeout = TimeSpan.FromSeconds(10) };
        using var req = new HttpRequestMessage(method, "/api/local/admin/" + path);
        req.Headers.Add(LocalAdmin.Header, key);
        req.Headers.Add("X-Gac-Local-User", Environment.UserName);
        using var resp = http.Send(req);
        if (!resp.IsSuccessStatusCode)
        {
            string? msg = null;
            try { msg = resp.Content.ReadFromJsonAsync<ApiError>(GacJson.Options).GetAwaiter().GetResult()?.Message; } catch (System.Text.Json.JsonException) { }
            throw new GacLocalException(msg ?? $"서버 오류 ({(int)resp.StatusCode})");
        }
        if (typeof(T) == typeof(object)) return default;
        return resp.Content.ReadFromJsonAsync<T>(GacJson.Options).GetAwaiter().GetResult();
    }

    private static bool IsTransport(Exception e) => e is HttpRequestException or TaskCanceledException or IOException;

    private static string? Try(Action a)
    {
        try { a(); return null; }
        catch (GacException e) { return e.Message; }
        catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException) { return "저장하지 못했습니다: " + e.Message; }
    }

    // ---- logs ----
    public IReadOnlyList<ServerLogLine> GetLogs()
    {
        try
        {
            var dir = Path.Combine(DataDir, "logs");
            var file = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "server-*.log").OrderByDescending(File.GetLastWriteTime).FirstOrDefault() : null;
            if (file == null) return [];
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            var lines = new List<string>();
            while (r.ReadLine() is { } l) { lines.Add(l); if (lines.Count > 1000) lines.RemoveAt(0); }
            return lines.Select(Parse).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>"yyyy-MM-dd HH:mm:ss.fff Level category: message" (FileLoggerProvider) → a log line with a Korean level.</summary>
    internal static ServerLogLine Parse(string line)
    {
        var at = line.Length >= 23 && DateTime.TryParse(line[..23], out var t) ? new DateTimeOffset(t) : DateTimeOffset.Now;
        var rest = line.Length > 24 ? line[24..] : line;
        var level = rest.StartsWith("Error") || rest.StartsWith("Critical") ? "오류" : rest.StartsWith("Warning") ? "경고" : "정보";
        var msg = rest.Length > 12 ? rest[12..].Trim() : rest;
        return new ServerLogLine(at, level, msg);
    }

    public string? OpenLogFolder()
    {
        var dir = Path.Combine(DataDir, "logs");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true })?.Dispose();
        return null;
    }

    private sealed class NoShutdown : IServerPowerAction { public void Shutdown() { } }
}

public sealed class GacLocalException(string message) : Exception(message);
