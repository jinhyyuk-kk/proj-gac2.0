using Gac.Console;
using Gac.Console.UI;

namespace Gac.Demo;

/// <summary>
/// GAC-Demo.exe: the real Console UI in demo mode, backed by an in-memory fake server. Portable, no admin rights,
/// no network, nothing saved. Closing the window discards everything.
/// </summary>
public static class DemoProgram
{
    public const string Banner = "체험 모드 · 실제 PC에는 아무 변화도 없습니다";

    public static ConsoleOptions CreateOptions(bool showTour) => new()
    {
        AllowFileExport = false,
        Banner = Banner,
        ShowTour = showTour,
        ExtraViewMenu =
        [
            ("선생님용 트레이 미리 보기…", owner => { using var f = new TrayPreviewForm(); f.ShowDialog(owner); }),
            ("둘러보기 다시 보기", owner => { if (owner is MainForm m) GuidedTour.Show(m); }),
        ],
    };

    [STAThread]
    public static void Main(string[] args)
    {
        ConsoleApp.InitializeWinForms();
        using var client = new DemoServerClient();
        ConsoleApp.RunWith(client, client.Owner, CreateOptions(showTour: !args.Contains("--no-tour")));
    }
}
