using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Gac.Shared.Modules;

namespace Gac.Agent.Runtime;

/// <summary>Runs a process hidden, captures output (capped), kills the process tree on timeout.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    private const int MaxCapture = 64 * 1024;

    static ProcessRunner() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public async Task<ProcessRunResult> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = oem,
            StandardErrorEncoding = oem,
            WorkingDirectory = Environment.SystemDirectory,
        };
        var sw = Stopwatch.StartNew();
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => Append(stdout, e.Data);
        p.ErrorDataReceived += (_, e) => Append(stderr, e.Data);
        try { p.Start(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new ProcessRunResult(null, false, "", "실행 실패: " + e.Message, sw.Elapsed);
        }
        p.StandardInput.Close();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var timedOut = false;
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            try { await p.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { }
        }
        int? exit = p.HasExited ? p.ExitCode : null;
        return new ProcessRunResult(timedOut ? null : exit, timedOut, Snapshot(stdout), Snapshot(stderr), sw.Elapsed);
    }

    private static void Append(StringBuilder sb, string? line)
    {
        if (line == null) return;
        lock (sb) { if (sb.Length < MaxCapture) sb.AppendLine(line); }
    }

    private static string Snapshot(StringBuilder sb) { lock (sb) return sb.ToString(); }
}

/// <summary>Interactive session helpers (WTS API) used to report the logged-on user.</summary>
public static class Sessions
{
    public static string? ActiveUser()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) return null;
        var user = Query(sessionId, 5 /* WTSUserName */);
        if (string.IsNullOrEmpty(user)) return null;
        var domain = Query(sessionId, 7 /* WTSDomainName */);
        return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
    }

    private static string? Query(uint session, int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, infoClass, out var buf, out _)) return null;
        try { return Marshal.PtrToStringUni(buf); }
        finally { WTSFreeMemory(buf); }
    }

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
}
