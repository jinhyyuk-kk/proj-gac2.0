using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gac.Agent.Runtime;

/// <summary>
/// CHANGE-001 2.2: the SYSTEM service keeps the tray running. If the logged-in user's session has no Gac.Tray.exe, the
/// service relaunches it (in that user's session, with their own rights) within a few seconds. The tray has no "exit"
/// item; lock/preset state lives in the service, so killing the tray never clears it.
/// </summary>
public sealed class TrayWatchdog(ILogger<TrayWatchdog> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Only meaningful when running as a Windows service (SYSTEM). In console/dev runs the tray starts itself at logon.
        if (!WindowsServiceHelpers()) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { EnsureTrayRunning(); }
            catch (Exception e) { log.LogDebug(e, "Tray watchdog tick failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    private static bool WindowsServiceHelpers() => Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();

    private void EnsureTrayRunning()
    {
        var session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF) return; // no active console session
        // A tray already in that session? Then nothing to do.
        foreach (var p in Process.GetProcessesByName("Gac.Tray"))
            using (p)
                try { if ((uint)p.SessionId == session) return; } catch { }

        if (!WTSQueryUserToken(session, out var userToken)) return; // no interactive user logged on
        try
        {
            var exe = TrayExePath();
            if (exe == null || !File.Exists(exe)) return;
            LaunchAsUser(userToken, exe);
        }
        finally { CloseHandle(userToken); }
    }

    private static string? TrayExePath()
    {
        // Installed layout: ...\GAC 2.0\Agent\Gac.Agent.exe and ...\GAC 2.0\Tray\Gac.Tray.exe
        var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Tray", "Gac.Tray.exe"));
        return File.Exists(candidate) ? candidate : null;
    }

    private void LaunchAsUser(IntPtr userToken, string exePath)
    {
        var env = IntPtr.Zero;
        if (!CreateEnvironmentBlock(out env, userToken, false)) env = IntPtr.Zero;
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
        var cmd = "\"" + exePath + "\"";
        try
        {
            if (!CreateProcessAsUser(userToken, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env, Path.GetDirectoryName(exePath), ref si, out var pi))
            {
                log.LogDebug("CreateProcessAsUser failed: {Err}", Marshal.GetLastWin32Error());
                return;
            }
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            log.LogInformation("Tray watchdog relaunched Gac.Tray.exe in session");
        }
        finally { if (env != IntPtr.Zero) DestroyEnvironmentBlock(env); }
    }

    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess; public IntPtr hThread; public uint dwProcessId; public uint dwThreadId; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? appName, string cmdLine, IntPtr procAttr, IntPtr threadAttr,
        bool inherit, uint flags, IntPtr env, string? curDir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
}
