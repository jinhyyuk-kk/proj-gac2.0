using Gac.Modules.ClassMode;
using Gac.Tray.UI;

namespace Gac.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetDefaultFont(Ui.Font);

        // CHANGE-001 1.1, check site #4: the tray refuses to run on a laptop (own probes A + E).
        if (TrayStartupGuard.IsLaptopOrUnknown())
        {
            Ui.Error(null, TrayStartupGuard.Message);
            return;
        }

        // Desktop shortcut: Gac.Tray.exe --run-preset <id> runs one preset with the teacher's rights and exits.
        var i = Array.IndexOf(args, "--run-preset");
        if (i >= 0 && i + 1 < args.Length)
        {
            RunPresetOnce(args[i + 1]);
            return;
        }

        // One tray per user session.
        using var mutex = new Mutex(true, @"Local\GAC.Tray.v1", out var created);
        if (!created) return;
        Application.Run(new TrayApp());
    }

    private static void RunPresetOnce(string id)
    {
        using var pipe = new AgentPipeClient();
        pipe.Start();
        Thread.Sleep(500);
        var status = pipe.GetStatusAsync().GetAwaiter().GetResult();
        var (shared, _) = PresetStore.LoadShared(status?.SharedPresets, status?.PresetPublicKeyPem);
        var (user, _) = new PresetStore(PresetStore.DefaultUserPath).LoadUser();
        var preset = shared.Concat(user.Presets).FirstOrDefault(p => p.Id == id);
        if (preset == null) { Ui.Error(null, "이 바로 가기의 프리셋을 찾을 수 없습니다. 삭제되었을 수 있습니다."); return; }
        var results = new PresetRunner(new WindowsPresetActionExecutor()).RunAsync(preset, null, CancellationToken.None).GetAwaiter().GetResult();
        var failed = results.Where(r => !r.Success).ToList();
        if (failed.Count > 0) Ui.Error(null, $"'{preset.Name}' 일부 동작 실패:\n" + string.Join("\n", failed.Select(f => $"{f.Description}: {f.Error}")));
    }
}
