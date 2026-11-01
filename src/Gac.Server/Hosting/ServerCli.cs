using System.Diagnostics;
using Gac.Server.Data;
using Gac.Server.Pcs;

namespace Gac.Server.Hosting;

/// <summary>
/// Command-line verbs used by the installer and the GAC 서버 관리 app:
///   Gac.Server.exe                                   run (as service or console)
///   Gac.Server.exe install [--port N]                create data, cert, service — the install stays "설치 미완료"
///   Gac.Server.exe firewall --range CIDR [--port N] [--result FILE]
///                                                    CHANGE-002 §3: create the rule, read it back, mark the install complete
///   Gac.Server.exe apply-firewall [--range CIDR | --add CIDR | (re-detect)] [--result FILE]
///   Gac.Server.exe detect-range [--result FILE]
///   Gac.Server.exe uninstall [--delete-data]
///   Gac.Server.exe show-code
/// --result writes "OK" or "ERROR: message" (UTF-8) for the Inno Setup pages, which cannot read stdout.
/// </summary>
public static class ServerCli
{
    public const string ServiceName = "GACServer";
    public const string FirewallRuleName = "GAC 2.0 Server";

    public static async Task<int> RunAsync(string[] args)
    {
        var verb = args.FirstOrDefault()?.ToLowerInvariant() ?? "run";
        var dataDir = Environment.GetEnvironmentVariable("GAC_SERVER_DATA") is { Length: > 0 } d ? d : ServerOptions.DefaultDataDir;
        var result = ParseArg(args, "--result");
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
            case "firewall":
                return Report(result, OpenFirewall(dataDir, ParseArg(args, "--range"), ParsePortOrNull(args)));
            case "apply-firewall":
                return Report(result, ApplyFirewall(dataDir, ParseArg(args, "--range"), ParseArg(args, "--add")));
            case "uninstall":
                return Uninstall(dataDir, args.Contains("--delete-data", StringComparer.OrdinalIgnoreCase));
            case "show-code":
                Console.WriteLine(ShowCode(dataDir));
                return 0;
            case "detect-range":
            {
                var cidr = LanDetection.DetectCidr() ?? "";
                Console.WriteLine(cidr);
                if (result != null) File.WriteAllText(result, cidr);
                return 0;
            }
            default:
                Console.Error.WriteLine("Usage: Gac.Server.exe [run|install [--port N]|firewall --range CIDR [--port N]|apply-firewall [--range CIDR|--add CIDR]|detect-range|uninstall [--delete-data]|show-code] [--result FILE]");
                return 2;
        }
    }

    private static int Report(string? resultFile, string? error)
    {
        if (error == null) Console.WriteLine("OK"); else Console.Error.WriteLine(error);
        if (resultFile != null)
            try { File.WriteAllText(resultFile, error == null ? "OK" : "ERROR: " + error, new System.Text.UTF8Encoding(false)); } catch (IOException) { }
        return error == null ? 0 : 1;
    }

    private static int ParsePort(string[] args) => ParsePortOrNull(args) ?? ServerOptions.DefaultPort;

    private static int? ParsePortOrNull(string[] args) =>
        ParseArg(args, "--port") is { } s && int.TryParse(s, out var p) && p is > 0 and < 65536 ? p : null;

    private static string? ParseArg(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string ShowCode(string dataDir)
    {
        var db = new Db(Path.Combine(dataDir, "gac.db"));
        db.EnsureSchema();
        var pcs = new PcService(db, new SettingsStore(db), new AuditLog(db, TimeProvider.System), TimeProvider.System);
        return pcs.EnrollCode;
    }

    private static string? SetupCode(string dataDir, bool ensure)
    {
        var db = new Db(Path.Combine(dataDir, "gac.db"));
        db.EnsureSchema();
        var acc = new Accounts.AccountService(db, new AuditLog(db, TimeProvider.System), TimeProvider.System);
        return ensure ? acc.EnsureSetupCode() : acc.GetSetupCode();
    }

    /// <summary>
    /// enrollment.txt: address, enroll code, range, thumbprint — and the setup code only once the install is complete
    /// (CHANGE-002 §3.2: the setup code is shown only after the firewall step).
    /// </summary>
    internal static void WriteEnrollmentFile(string dataDir)
    {
        var cfg = ServerIdentity.LoadConfig(dataDir);
        using var id = ServerIdentity.LoadOrCreate(dataDir);
        var setup = cfg.IsInstallComplete ? SetupCode(dataDir, ensure: false) : null;
        var ranges = cfg.Ranges is { Length: > 0 } r ? string.Join(", ", r) : "(미설정)";
        File.WriteAllText(Path.Combine(dataDir, "enrollment.txt"),
            $"서버 주소: {Environment.MachineName}:{cfg.Port}\r\n포트: {cfg.Port}\r\n교내망 대역: {ranges}\r\n등록 코드: {ShowCode(dataDir)}\r\n" +
            (cfg.IsInstallComplete
                ? $"설치 코드(최초 회원가입용): {setup ?? "(회원가입 완료됨)"}\r\n"
                : "설치 코드: 방화벽 열기를 마치면 표시됩니다.\r\n") +
            $"인증서 지문(SHA-256): {id.Thumbprint}\r\n");
    }

    /// <summary>
    /// CHANGE-002 §3.1 step 3: TCP, that port only, remote address = that LAN range only, all profiles. The rule is read
    /// back and verified; only then is the install marked complete and the setup code written.
    /// </summary>
    internal static string? OpenFirewall(string dataDir, string? range, int? port,
        Func<int, IReadOnlyList<string>, string?>? apply = null, Func<FirewallRuleInfo?>? readBack = null, Action<string>? serviceVerb = null)
    {
        if (!LanDetection.IsValidCidr(range)) return "교내망 대역 형식이 올바르지 않습니다 (예: 10.20.0.0/22).";
        var cfg = ServerIdentity.LoadConfig(dataDir);
        var newPort = port ?? cfg.Port;
        string[] ranges = [Firewall.Normalize(range!)];
        var err = (apply ?? ((p, r) => Firewall.Apply(p, r, Environment.ProcessPath!)))(newPort, ranges);
        if (err != null) return err;
        err = Firewall.Verify((readBack ?? Firewall.ReadBack)(), newPort, ranges);
        if (err != null) return "방화벽 규칙을 확인하지 못했습니다: " + err;
        ServerIdentity.SaveConfig(dataDir, cfg with { Port = newPort, Ranges = ranges, InstallComplete = true });
        WriteEnrollmentFile(dataDir);
        if (newPort != cfg.Port)
        {
            // Kestrel binds the port at start-up, so a changed port needs a service restart.
            var svc = serviceVerb ?? (v => Run("sc.exe", $"{v} {ServiceName}", ignoreErrors: true));
            svc("stop");
            Thread.Sleep(2000);
            svc("start");
        }
        return null;
    }

    /// <summary>[대역 다시 감지] (no args), replace with --range, or [대역 추가] with --add. Keeps the install state.</summary>
    private static string? ApplyFirewall(string dataDir, string? replace, string? add)
    {
        var cfg = ServerIdentity.LoadConfig(dataDir);
        string[] ranges;
        if (add != null)
        {
            if (!LanDetection.IsValidCidr(add)) return "대역 형식이 올바르지 않습니다 (예: 10.20.0.0/22).";
            ranges = [.. (cfg.Ranges ?? []).Append(Firewall.Normalize(add)).Distinct()];
        }
        else if (replace != null)
        {
            if (!LanDetection.IsValidCidr(replace)) return "대역 형식이 올바르지 않습니다 (예: 10.20.0.0/22).";
            ranges = [Firewall.Normalize(replace)];
        }
        else ranges = LanDetection.DetectCidr() is { } c ? [c] : [];
        if (ranges.Length == 0) return "교내망 대역을 감지하지 못했습니다. [대역 추가]로 직접 입력하세요.";
        var err = Firewall.Apply(cfg.Port, ranges, Environment.ProcessPath!) ?? Firewall.Verify(Firewall.ReadBack(), cfg.Port, ranges);
        if (err != null) return err;
        ServerIdentity.SaveConfig(dataDir, cfg with { Ranges = ranges });
        WriteEnrollmentFile(dataDir);
        Console.WriteLine(string.Join(",", ranges));
        return null;
    }

    /// <summary>Idempotent: safe to run again on upgrade. Keeps existing cert, keys, database and install state.</summary>
    private static int Install(string dataDir, int port)
    {
        if (Path.GetDirectoryName(dataDir) is { } parent) Gac.Shared.Security.SecureDirectory.Ensure(parent);
        Gac.Shared.Security.SecureDirectory.Ensure(dataDir);
        var hadConfig = File.Exists(Path.Combine(dataDir, "server.json"));
        var existing = ServerIdentity.LoadConfig(dataDir);
        // A fresh install is "설치 미완료" until the firewall step; an upgrade keeps its state (old configs count as complete).
        var cfg = hadConfig ? existing : new ServerFileConfig(port, [], InstallComplete: false);
        ServerIdentity.SaveConfig(dataDir, cfg);
        using (ServerIdentity.LoadOrCreate(dataDir)) { }
        SetupCode(dataDir, ensure: true);
        WriteEnrollmentFile(dataDir);

        var exe = Environment.ProcessPath!;
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
        Firewall.Delete();
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
