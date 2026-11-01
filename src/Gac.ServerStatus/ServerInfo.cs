using System.Diagnostics;
using Dapper;
using Gac.Server.Accounts;
using Gac.Server.Data;
using Gac.Server.Hosting;

namespace Gac.ServerStatus;

/// <summary>Reads the local GAC Server state (DB, config, service, logs). Admin-only; talks only to the local machine.</summary>
public sealed class ServerInfo
{
    public string DataDir { get; } = Environment.GetEnvironmentVariable("GAC_SERVER_DATA") is { Length: > 0 } d ? d : ServerOptions.DefaultDataDir;
    private string DbPath => Path.Combine(DataDir, "gac.db");

    public sealed record Snapshot(bool ServiceRunning, string ServerName, int Port, string[] Ranges, string Thumbprint,
        int PcCount, int OnlineCount, int BlockedCount, int PendingCount, DateTimeOffset? LastEnrollment, string? SetupCode, IReadOnlyList<string> RecentLog);

    public Snapshot Read()
    {
        var cfg = ServerIdentity.LoadConfig(DataDir);
        var running = ServiceRunning();
        int pcs = 0, online = 0, blocked = 0, pending = 0; DateTimeOffset? lastEnroll = null; string? setup = null; var thumb = "";
        try
        {
            var db = new Db(DbPath);
            db.EnsureSchema();
            using var c = db.Open();
            pcs = c.ExecuteScalar<int>("SELECT COUNT(*) FROM pcs");
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeMilliseconds();
            online = c.ExecuteScalar<int>("SELECT COUNT(*) FROM pcs WHERE last_seen >= @c AND revoked = 0", new { c = cutoff });
            blocked = c.ExecuteScalar<int>("SELECT COUNT(*) FROM devices WHERE status IN ('blocked','revoked')");
            pending = c.ExecuteScalar<int>("SELECT COUNT(*) FROM devices WHERE status = 'pending'");
            var last = c.ExecuteScalar<long?>("SELECT MAX(enrolled_at) FROM pcs");
            if (last is { } ms) lastEnroll = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
            setup = new AccountService(db, new AuditLog(db, TimeProvider.System), TimeProvider.System).GetSetupCode();
        }
        catch { }
        try { using var id = ServerIdentity.LoadOrCreate(DataDir); thumb = id.Thumbprint; } catch { }
        return new Snapshot(running, Environment.MachineName, cfg.Port, cfg.Ranges ?? [], thumb, pcs, online, blocked, pending, lastEnroll, setup, RecentLog());
    }

    public static bool ServiceRunning()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("sc.exe", "query GACServer") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return o.Contains("RUNNING");
        }
        catch { return false; }
    }

    public IReadOnlyList<string> RecentLog()
    {
        try
        {
            var dir = Path.Combine(DataDir, "logs");
            var file = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.log").OrderByDescending(File.GetLastWriteTime).FirstOrDefault() : null;
            if (file == null) return [];
            return File.ReadLines(file).Reverse().Take(200).Reverse().ToList();
        }
        catch { return []; }
    }

    public static void Service(string verb)
    {
        try { Process.Start(new ProcessStartInfo("sc.exe", $"{verb} GACServer") { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(); } catch { }
    }

    public string RunServerVerb(string args)
    {
        var exe = Path.Combine(DataDir.Length > 0 ? ServerExeDir() : AppContext.BaseDirectory, "Gac.Server.exe");
        if (!File.Exists(exe)) exe = Path.Combine(AppContext.BaseDirectory, "Gac.Server.exe");
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return o.Trim();
        }
        catch (Exception e) { return e.Message; }
    }

    private static string ServerExeDir() => AppContext.BaseDirectory;

    public AgentRolloutConfig RolloutConfig(Snapshot s) =>
        new(s.ServerName, s.Port, s.Thumbprint, EnrollCode());

    public string EnrollCode()
    {
        try
        {
            var db = new Db(DbPath);
            db.EnsureSchema();
            return new Gac.Server.Pcs.PcService(db, new SettingsStore(db), new AuditLog(db, TimeProvider.System), TimeProvider.System).EnrollCode;
        }
        catch { return ""; }
    }
}
