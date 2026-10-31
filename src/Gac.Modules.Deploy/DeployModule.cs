using System.Globalization;
using System.Text;
using Gac.Shared;
using Gac.Shared.Contracts;
using Gac.Shared.Modules;

namespace Gac.Modules.Deploy;

/// <summary>
/// Batch Deploy &amp; Run (CLAUDE.md 4.7) plus the old GAC "run command". Packages are local files served by the GAC Server;
/// every package is SHA-256 verified before it runs. Runs as LocalSystem.
/// </summary>
public sealed class DeployModule(IPackageSource packages, IProcessRunner runner, string workDir) : IAgentModule
{
    public const int MaxOutputChars = 8000;
    public const int MaxTimeoutSeconds = 4 * 3600;

    public string Id => ModuleIds.Deploy;

    public async Task<ModuleOutcome> ExecuteAsync(ModuleRequest request, CancellationToken ct)
    {
        switch (request.Action)
        {
            case ModuleActions.DeployInstall:
            {
                var req = GacJson.Deserialize<DeployPackageRequest>(request.PayloadJson);
                if (req == null || string.IsNullOrWhiteSpace(req.PackageId) || string.IsNullOrWhiteSpace(req.Sha256)) return ModuleOutcome.Fail("배포 명령 내용이 올바르지 않습니다.");
                string path;
                try { path = await packages.DownloadVerifiedAsync(req.PackageId, req.FileName, req.Sha256, ct); }
                catch (InvalidDataException) { return ModuleOutcome.Fail("내려받은 설치 파일의 해시(SHA-256)가 일치하지 않아 실행하지 않았습니다."); }
                catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException) { return ModuleOutcome.Fail("GAC 서버에서 설치 파일을 내려받지 못했습니다: " + e.Message); }

                var (file, args) = BuildInstallCommand(path, req.SilentArgs);
                var r = await runner.RunAsync(file, args, Timeout(req.TimeoutSeconds), ct);
                var result = ToResult(r);
                var ok = !r.TimedOut && r.ExitCode is 0 or 3010 or 1641;
                var summary = r.TimedOut ? $"설치 시간 초과 ({req.TimeoutSeconds}초)" :
                    ok ? (r.ExitCode == 0 ? "설치 완료" : "설치 완료 (재부팅 필요)") : $"설치 실패 (종료 코드 {r.ExitCode})";
                TryDelete(path);
                return new ModuleOutcome(ok, $"{req.FileName}: {summary}", result);
            }
            case ModuleActions.DeployRunScript:
            {
                var req = GacJson.Deserialize<RunScriptRequest>(request.PayloadJson);
                if (req == null || string.IsNullOrWhiteSpace(req.Script)) return ModuleOutcome.Fail("실행할 명령이 비어 있습니다.");
                Directory.CreateDirectory(workDir);
                var scriptPath = Path.Combine(workDir, Guid.NewGuid().ToString("N") + (req.Shell == ScriptShell.PowerShell ? ".ps1" : ".cmd"));
                try
                {
                    WriteScript(scriptPath, req);
                    var (file, args) = BuildScriptCommand(scriptPath, req.Shell);
                    var r = await runner.RunAsync(file, args, Timeout(req.TimeoutSeconds), ct);
                    var ok = !r.TimedOut && r.ExitCode == 0;
                    var summary = r.TimedOut ? $"시간 초과 ({req.TimeoutSeconds}초)" : $"종료 코드 {r.ExitCode}";
                    return new ModuleOutcome(ok, $"명령 실행 {(ok ? "성공" : "실패")}: {summary}", ToResult(r));
                }
                finally { TryDelete(scriptPath); }
            }
            default:
                return ModuleOutcome.Fail("알 수 없는 명령입니다.");
        }
    }

    public static (string File, string Args) BuildInstallCommand(string path, string? silentArgs)
    {
        var args = (silentArgs ?? "").Trim();
        if (path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            var extra = args.Length == 0 ? "/qn /norestart" : args;
            return (Path.Combine(Environment.SystemDirectory, "msiexec.exe"), $"/i \"{path}\" {extra}".TrimEnd());
        }
        return (path, args);
    }

    public static (string File, string Args) BuildScriptCommand(string scriptPath, ScriptShell shell) => shell switch
    {
        ScriptShell.PowerShell => (Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\""),
        _ => (Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /c \"\"{scriptPath}\"\""),
    };

    private static void WriteScript(string path, RunScriptRequest req)
    {
        if (req.Shell == ScriptShell.PowerShell)
            File.WriteAllText(path, req.Script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        else
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            File.WriteAllText(path, "@echo off\r\n" + req.Script.ReplaceLineEndings("\r\n") + "\r\n", oem);
        }
    }

    private static TimeSpan Timeout(int seconds) => TimeSpan.FromSeconds(Math.Clamp(seconds <= 0 ? 600 : seconds, 5, MaxTimeoutSeconds));

    public static DeployResult ToResult(ProcessRunResult r) =>
        new(r.ExitCode, r.TimedOut, Truncate(r.StdOut), Truncate(r.StdErr), Math.Round(r.Duration.TotalSeconds, 1));

    public static string Truncate(string s) => s.Length <= MaxOutputChars ? s : s[..MaxOutputChars] + "\n…(이하 생략)";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
