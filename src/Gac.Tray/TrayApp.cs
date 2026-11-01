using System.Runtime.InteropServices;
using Gac.Modules.ClassMode;
using Gac.Modules.NetDiag;
using Gac.Shared;
using Gac.Shared.ClassMode;
using Gac.Shared.Contracts;
using Gac.Shared.Pipe;
using Gac.Tray.UI;
using Microsoft.Win32;

namespace Gac.Tray;

/// <summary>Teacher-facing tray icon (user session, no admin rights).</summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _icon = new() { Text = "GAC 2.0", Visible = true };
    private readonly ContextMenuStrip _menu = new() { Font = Ui.Font };
    private readonly AgentPipeClient _pipe = new();
    private readonly PresetRunner _runner = new(new WindowsPresetActionExecutor());
    private readonly SynchronizationContext _ui;
    private readonly Control _invoker = new();
    private readonly Dictionary<string, LockForm> _locks = new();
    private readonly System.Windows.Forms.Timer _explorerKiller = new() { Interval = 1500 };
    private ShutdownWarningForm? _warning;
    private AgentStatus? _status;
    private bool _loginPresetsDone;

    public TrayApp()
    {
        _invoker.CreateControl();
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _icon.Icon = TrayIcon.Create();
        _icon.ContextMenuStrip = _menu;
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMenu(); };
        _menu.Opening += async (_, _) => await BuildMenuAsync();
        _explorerKiller.Tick += OnExplorerKillerTick;
        _pipe.OnEvent = OnServiceEventAsync;
        _pipe.Start();
        _ = RunLoginPresetsAsync();
    }

    private void ShowMenu()
    {
        // NotifyIcon only opens the context menu on right click; reuse it for left click.
        typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(_icon, null);
    }

    private async Task<IReadOnlyList<ClassPreset>> AllPresetsAsync()
    {
        _status = await _pipe.GetStatusAsync() ?? _status;
        var (shared, _) = PresetStore.LoadShared(_status?.SharedPresets, _status?.PresetPublicKeyPem);
        var (user, _) = await LoadPcPresetsAsync();
        return [.. shared, .. user.Presets];
    }

    // Per-PC teacher presets live with the SYSTEM service (CHANGE-001 2.1); the tray reads/writes them over the pipe.
    private async Task<(PresetFile File, string? Error)> LoadPcPresetsAsync()
    {
        var r = await _pipe.RequestAsync(TrayRequest.GetPresets, null, TimeSpan.FromSeconds(5));
        if (!r.Ok || r.DataJson == null) return (new PresetFile(), r.Ok ? null : r.Message);
        var (file, result) = PresetValidator.Parse(r.DataJson, allowRunCommand: false);
        return file == null ? (new PresetFile(), result.ErrorText) : (file, null);
    }

    private async Task<string?> SavePcPresetsAsync(PresetFile file)
    {
        var r = await _pipe.RequestAsync(TrayRequest.SavePresets, new PresetsBody(GacJson.Serialize(file)), TimeSpan.FromSeconds(5));
        return r.Ok ? null : r.Message;
    }

    private async Task BuildMenuAsync()
    {
        _menu.Items.Clear();
        var presets = await AllPresetsAsync();
        var title = new ToolStripMenuItem("원클릭 수업 모드") { Enabled = false, Font = Ui.Bold };
        _menu.Items.Add(title);
        if (presets.Count == 0) _menu.Items.Add(new ToolStripMenuItem("(프리셋이 없습니다 – 아래에서 만드세요)") { Enabled = false });
        foreach (var p in presets)
        {
            var preset = p;
            _menu.Items.Add(new ToolStripMenuItem("▶ " + p.Name + (p.Shared ? "  (관리자 배포)" : ""), null, async (_, _) => await RunPresetAsync(preset, showResult: true)));
        }
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("수업 모드 프리셋 편집…", null, (_, _) => OpenEditor()));
        var shortcuts = new ToolStripMenuItem("바탕화면에 바로 가기 만들기");
        foreach (var p in presets)
        {
            var preset = p;
            shortcuts.DropDownItems.Add(new ToolStripMenuItem(p.Name, null, (_, _) => CreateShortcut(preset)));
        }
        shortcuts.Enabled = presets.Count > 0;
        _menu.Items.Add(shortcuts);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("네트워크 자가 진단…", null, (_, _) => OpenNetDiag()));
        var state = _pipe.Connected
            ? (_status?.LastServerContact is { } t ? $"GAC 서버 연결: {t.ToLocalTime():HH:mm} 확인" : "GAC 서비스 연결됨")
            : "GAC 서비스에 연결되지 않음";
        _menu.Items.Add(new ToolStripMenuItem(state) { Enabled = false });
    }

    private async void OpenEditor()
    {
        var (file, error) = await LoadPcPresetsAsync();
        if (error != null) Ui.Error(null, error);
        using var editor = new PresetEditorForm(file, allowRunCommand: false, SavePcPresetsAsync, p => RunPresetAsync(p, showResult: true), "수업 모드 프리셋 편집");
        editor.StartPosition = FormStartPosition.CenterScreen;
        editor.ShowDialog();
    }

    public async Task RunPresetAsync(ClassPreset p, bool showResult)
    {
        _icon.ShowBalloonTip(2000, "GAC 수업 모드", $"'{p.Name}' 실행 중…", ToolTipIcon.None);
        var results = await _runner.RunAsync(p, null, CancellationToken.None);
        var failed = results.Where(r => !r.Success).ToList();
        if (!showResult) return;
        if (failed.Count == 0) _icon.ShowBalloonTip(3000, "GAC 수업 모드", $"'{p.Name}' 완료", ToolTipIcon.Info);
        else _icon.ShowBalloonTip(6000, "GAC 수업 모드", $"'{p.Name}': 일부 동작 실패\n" + string.Join("\n", failed.Select(f => $"{f.Description}: {f.Error}")), ToolTipIcon.Warning);
    }

    private async Task RunLoginPresetsAsync()
    {
        if (_loginPresetsDone) return;
        _loginPresetsDone = true;
        await Task.Delay(TimeSpan.FromSeconds(8)); // let the desktop and pipe settle
        foreach (var p in (await AllPresetsAsync()).Where(p => p.Trigger == PresetTrigger.Login))
            await RunPresetAsync(p, showResult: false);
    }

    private void OpenNetDiag()
    {
        using var f = new NetDiagForm(new TrayNetDiagHost(_pipe, () => _status));
        f.ShowDialog();
    }

    private static void CreateShortcut(ClassPreset p)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var safe = string.Concat(p.Name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
            var link = Path.Combine(desktop, $"수업 모드 - {safe}.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell 없음");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic sc = shell.CreateShortcut(link);
            sc.TargetPath = Environment.ProcessPath;
            sc.Arguments = $"--run-preset {p.Id}";
            sc.WorkingDirectory = AppContext.BaseDirectory;
            sc.IconLocation = Environment.ProcessPath + ",0";
            sc.Save();
            Ui.Info(null, "바탕화면에 바로 가기를 만들었습니다.");
        }
        catch (Exception e) { Ui.Error(null, "바로 가기를 만들지 못했습니다: " + e.Message); }
    }

    // ---------- service events ----------
    private async Task<PipeResponse?> OnServiceEventAsync(PipeMessage msg)
    {
        switch (msg.Type)
        {
            case nameof(ServiceEvent.ShutdownWarning):
                var w = msg.Body<ShutdownWarningBody>();
                if (w != null) OnUi(() => ShowShutdownWarning(w));
                return null;
            case nameof(ServiceEvent.ShutdownCancelled):
                OnUi(() => { _warning?.Close(); _warning = null; });
                return null;
            case nameof(ServiceEvent.LedTest):
            {
                var tcs = new TaskCompletionSource<LedTestResult>();
                OnUi(() =>
                {
                    using var f = new LedTestForm();
                    f.ShowDialog();
                    tcs.TrySetResult(new LedTestResult(f.Passed ?? false, f.Passed == null ? "점검을 취소함" : f.Note));
                });
                var result = await tcs.Task;
                return new PipeResponse(true, result.Passed ? "이상 없음" : "이상 있음", GacJson.Serialize(result));
            }
            case nameof(ServiceEvent.ResetWallpaper):
            {
                var body = msg.Body<WallpaperBody>();
                return Wallpaper.Reset(body?.Path);
            }
            case nameof(ServiceEvent.Lock):
            {
                var body = msg.Body<LockBody>();
                if (body != null) OnUi(() => ShowLock(body));
                return new PipeResponse(true, "ok");
            }
            case nameof(ServiceEvent.Unlock):
            {
                var body = msg.Body<LockBody>();
                if (body != null) OnUi(() => CloseLock(body.PeriodId));
                return new PipeResponse(true, "ok");
            }
            default:
                return new PipeResponse(false, "알 수 없는 요청");
        }
    }

    // ---------- PC lock (CHANGE-001 2.4) ----------
    private void ShowLock(LockBody body)
    {
        if (_locks.ContainsKey(body.PeriodId)) return; // already locked for this period
        var form = new LockForm(body.PeriodName, body.CountdownMinutes * 60)
        {
            Unlock = async (user, pw) =>
            {
                var r = await _pipe.RequestAsync(TrayRequest.Unlock, new UnlockRequest(body.PeriodId, user, pw), TimeSpan.FromSeconds(15));
                if (r.Ok) OnUi(() => { RestartExplorer(); StopExplorerKiller(); });
                return (r.Ok, r.Message);
            },
        };
        form.ExitRequested += async (_, _) => await _pipe.RequestAsync(TrayRequest.ShutdownNow, null, TimeSpan.FromSeconds(10));
        form.Expired += async (_, _) => await _pipe.RequestAsync(TrayRequest.ShutdownNow, null, TimeSpan.FromSeconds(10));
        // When the last lock closes for any reason (unlock, period end, exemption day starting) the desktop comes back.
        form.FormClosed += (_, _) => { _locks.Remove(body.PeriodId); if (_locks.Count == 0) { StopExplorerKiller(); RestartExplorer(); } };
        _locks[body.PeriodId] = form;
        EndExplorer();
        StartExplorerKiller();
        form.Show();
        form.Activate();
    }

    private void CloseLock(string periodId)
    {
        if (_locks.TryGetValue(periodId, out var form)) form.ForceClose();
    }

    private void StartExplorerKiller() { _explorerKiller.Start(); }
    private void StopExplorerKiller() { if (_locks.Count == 0) _explorerKiller.Stop(); }

    private void OnExplorerKillerTick(object? sender, EventArgs e) { if (_locks.Count > 0) EndExplorer(); }

    private static void EndExplorer()
    {
        // Ends the user's own explorer.exe (desktop, taskbar, Start). User-context: no admin rights needed.
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("explorer"))
        {
            try { if (p.SessionId == System.Diagnostics.Process.GetCurrentProcess().SessionId) p.Kill(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { p.Dispose(); }
        }
    }

    private static void RestartExplorer()
    {
        // Starting explorer.exe while the shell is already running only opens a File Explorer window, so skip that case.
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var running = System.Diagnostics.Process.GetProcessesByName("explorer");
        try { if (running.Any(p => p.SessionId == session)) return; }
        finally { foreach (var p in running) p.Dispose(); }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private void ShowShutdownWarning(ShutdownWarningBody w)
    {
        _warning?.Close();
        _warning = new ShutdownWarningForm(w.Deadline, w.ExtensionsLeft, w.ExtensionMinutes);
        _warning.ExtendClicked += async (_, _) =>
        {
            var r = await _pipe.RequestAsync(TrayRequest.ShutdownExtend, null, TimeSpan.FromSeconds(10));
            _icon.ShowBalloonTip(4000, "GAC 자동 종료", r.Message, r.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
            if (r.Ok) { _warning?.Close(); _warning = null; }
        };
        _warning.ShutdownNowClicked += async (_, _) =>
        {
            _warning?.Close();
            _warning = null;
            await _pipe.RequestAsync(TrayRequest.ShutdownNow, null, TimeSpan.FromSeconds(10));
        };
        _warning.Show();
        _warning.Activate();
    }

    private void OnUi(Action a) => _invoker.BeginInvoke(a);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _pipe.Dispose();
            _invoker.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Network self-diagnosis in the tray: checks run as the teacher; privileged repairs go through the pipe enum.</summary>
internal sealed class TrayNetDiagHost(AgentPipeClient pipe, Func<AgentStatus?> status) : INetDiagHost, INetRepairChannel, IProxyReset
{
    public async Task<NetDiagReport> RunAsync(Func<string, bool> confirmReboot, IProgress<string> progress, CancellationToken ct)
    {
        var s = status() ?? await pipe.GetStatusAsync();
        var options = new NetDiagOptions(s?.ServerHost, s?.ServerPort ?? 8443, s?.NetDiag.DnsTestName, s?.NetDiag.ExternalTarget);
        var flow = new NetDiagFlow(new NetDiagnostics(new SystemNetProbe(readUserProxy: true)), this, this);
        return await flow.RunAsync(options, confirmReboot, progress, ct);
    }

    public async Task<string> SubmitAsync(NetDiagReport report, CancellationToken ct) =>
        (await pipe.RequestAsync(TrayRequest.SubmitNetDiagReport, report, TimeSpan.FromSeconds(10))).Message;

    public Task<PipeResponse> RepairAsync(NetRepairAction action, CancellationToken ct) =>
        pipe.RequestAsync(TrayRequest.NetRepair, new NetRepairBody(action), TimeSpan.FromMinutes(3));

    /// <summary>Turns off the teacher's own (HKCU) proxy settings.</summary>
    public void Reset()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true);
        if (k == null) return;
        k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        k.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        InternetSetOption(IntPtr.Zero, 39 /* INTERNET_OPTION_SETTINGS_CHANGED */, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37 /* INTERNET_OPTION_REFRESH */, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int length);
}

/// <summary>Inspection: reset the current user's wallpaper to the school default.</summary>
internal static class Wallpaper
{
    public static PipeResponse Reset(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
            return new PipeResponse(false, $"학교 기본 바탕화면 파일을 찾을 수 없습니다: {path}");
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".bmp")) return new PipeResponse(false, "바탕화면은 jpg/png/bmp 파일만 지정할 수 있습니다.");
        var ok = SystemParametersInfo(0x0014 /* SPI_SETDESKWALLPAPER */, 0, path, 0x01 | 0x02 /* SPIF_UPDATEINIFILE | SPIF_SENDCHANGE */);
        return ok ? new PipeResponse(true, "학교 기본 바탕화면으로 바꿨습니다.") : new PipeResponse(false, "바탕화면을 바꾸지 못했습니다.");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, string vparam, uint winIni);
}

/// <summary>Draws the tray icon at runtime (no binary assets needed).</summary>
internal static class TrayIcon
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var b = new SolidBrush(Ui.Accent);
            g.FillEllipse(b, 1, 1, 30, 30);
            using var f = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, "G", f, new Rectangle(0, 0, 32, 32), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
