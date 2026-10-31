using System.Diagnostics;
using Gac.Server.Data;
using Gac.Server.Pcs;

namespace Gac.Server.Hosting;

/// <summary>
/// Command-line verbs used by the installer:
///   Gac.Server.exe                     run (as service or console)
///   Gac.Server.exe install [--port N]  create data, cert, firewall rule, service
///   Gac.Server.exe uninstall [--delete-data]
///   Gac.Server.exe show-code           print the enrollment code
/// </summary>
public static class ServerCli
{
    public const string ServiceName = "GACServer";
    public const string FirewallRuleName = "GAC 2.0 Server";

    public static async Task<int> RunAsync(string[] args)
    {
        var verb = args.FirstOrDefault()?.ToLowerInvariant() ?? "run";
        var dataDir = Environment.GetEnvironmentVariable("GAC_SERVER_DATA") is { Length: > 0 } d ? d : ServerOptions.DefaultDataDir;
        switch (verb)
        {
            case "run":
            {
                var cfg = ServerIdentity.LoadConfig(dataDir);
                var app = ServerApp.Build(new ServerOptions { DataDir = dataDir, Port = cfg.Port }, args.Skip(1).ToArray());
                await app.RunAsync();
                return 0;
            }
            case "install":
                return Install(dataDir, ParsePort(args));
            case "uninstall":
                return Uninstall(dataDir, args.Contains("--delete-data", StringComparer.OrdinalIgnoreCase));
            case "show-code":
                Console.WriteLine(ShowCode(dataDir));
                return 0;
            default:
                Console.Error.WriteLine("Usage: Gac.Server.exe [run|install [--port N]|uninstall [--delete-data]|show-code]");
                return 2;
        }
    }

    private static int ParsePort(string[] args)
    {
        var i = Array.FindIndex(args, a => a.Equals("--port", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var p) && p is > 0 and < 65536 ? p : ServerOptions.DefaultPort;
    }

    private static string ShowCode(string dataDir)
    {
        var db = new Db(Path.Combine(dataDir, "gac.db"));
        db.EnsureSchema();
        var pcs = new PcService(db, new SettingsStore(db), new AuditLog(db, TimeProvider.System), TimeProvider.System);
        return pcs.EnrollCode;
    }

    /// <summary>Idempotent: safe to run again on upgrade. Keeps existing cert, keys and database.</summary>
    private static int Install(string dataDir, int port)
    {
        Directory.CreateDirectory(dataDir);
        var existing = ServerIdentity.LoadConfig(dataDir);
        if (File.Exists(Path.Combine(dataDir, "server.json"))) port = existing.Port;
        ServerIdentity.SaveConfig(dataDir, new ServerFileConfig(port));
        using (var id = ServerIdentity.LoadOrCreate(dataDir))
        {
            var code = ShowCode(dataDir);
            File.WriteAllText(Path.Combine(dataDir, "enrollment.txt"),
                $"서버 주소: {Environment.MachineName}:{port}\r\n등록 코드: {code}\r\n인증서 지문(SHA-256): {id.Thumbprint}\r\n");
        }

        var exe = Environment.ProcessPath!;
        Run("netsh", $"advfirewall firewall delete rule name=\"{FirewallRuleName}\"", ignoreErrors: true);
        Run("netsh", $"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=TCP localport={port} program=\"{exe}\" profile=domain,private");

        if (Run("sc.exe", $"query {ServiceName}", ignoreErrors: true) != 0)
            Run("sc.exe", $"create {ServiceName} binPath= \"\\\"{exe}\\\"\" start= delayed-auto DisplayName= \"GAC 2.0 Server\"");
        else
            Run("sc.exe", $"config {ServiceName} binPath= \"\\\"{exe}\\\"\" start= delayed-auto");
        Run("sc.exe", $"description {ServiceName} \"GAC 2.0 학교 PC 관리 서버\"", ignoreErrors: true);
        Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/300000", ignoreErrors: true);
        Run("sc.exe", $"start {ServiceName}", ignoreErrors: true);
        return 0;
    }

    private static int Uninstall(string dataDir, bool deleteData)
    {
        Run("sc.exe", $"stop {ServiceName}", ignoreErrors: true);
        WaitForStop();
        Run("sc.exe", $"delete {ServiceName}", ignoreErrors: true);
        Run("netsh", $"advfirewall firewall delete rule name=\"{FirewallRuleName}\"", ignoreErrors: true);
        if (deleteData && Directory.Exists(dataDir))
        {
            try { Directory.Delete(dataDir, recursive: true); }
            catch (Exception e) { Console.Error.WriteLine($"Could not delete data directory: {e.Message}"); }
        }
        return 0;
    }

    private static void WaitForStop()
    {
        for (var i = 0; i < 30; i++)
        {
            if (Process.GetProcessesByName("Gac.Server").All(p => p.Id == Environment.ProcessId)) return;
            Thread.Sleep(500);
        }
    }

    private static int Run(string file, string args, bool ignoreErrors = false)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && !ignoreErrors) Console.Error.WriteLine($"{file} {args} failed ({p.ExitCode}): {err}");
        return p.ExitCode;
    }
}
