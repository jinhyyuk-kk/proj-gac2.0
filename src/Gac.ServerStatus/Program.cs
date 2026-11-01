using System.Diagnostics;
using System.Security.Principal;
using Gac.ServerAdmin.UI;
using Gac.Tray.UI;

namespace Gac.ServerStatus;

/// <summary>
/// GAC 서버 관리 (CHANGE-002 §4) on the Admin PC.
///   Gac.ServerStatus.exe --tray   tray icon at logon (no admin rights): T−10 shutdown popup, opens the window
///   Gac.ServerStatus.exe          settings window; relaunches itself elevated because changing settings needs
///                                 Windows administrator rights
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetDefaultFont(Ui.Font);
        Ui.AppIcon = GacIcons.Gac;

        if (args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
        {
            using var single = new Mutex(true, @"Local\GAC.ServerAdmin.Tray", out var first);
            if (!first) return;
            Application.Run(new TrayContext());
            return;
        }
        if (!IsElevated()) { OpenElevatedWindow(); return; }
        using var window = new Mutex(true, @"Global\GAC.ServerAdmin.Window", out var firstWindow);
        if (!firstWindow) return;
        // The separate tray process shows the T−10 popup, so the window has no tray icon of its own.
        Application.Run(new ServerAdminForm(new LocalServerBackend(), trayIcon: false));
    }

    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Starts the settings window with a UAC prompt ("runas").</summary>
    public static void OpenElevatedWindow()
    {
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { /* the user declined the UAC prompt */ }
    }
}
