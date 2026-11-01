using Gac.Tray.UI;

namespace Gac.ServerStatus;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetDefaultFont(Ui.Font);
        Application.Run(new StatusForm());
    }
}
