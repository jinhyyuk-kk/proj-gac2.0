using Gac.Shared.ClassMode;
using Gac.Shared.Contracts;
using Gac.Tray.UI;

namespace Gac.Demo;

/// <summary>
/// CHANGE-001 §7 "클라이언트 PC 화면": a simulated classroom desktop with the Tray App (menu, class-mode editor/runner,
/// network self-diagnosis, the auto-shutdown warning popup), the PC lock screen, and the agent installer screen with the
/// "노트북에는 설치할 수 없습니다" and "관리자 프로그램이 설치된 PC에는 클라이언트를 설치할 수 없습니다" refusals.
/// Everything is simulated: no volume/display/process/network/registry/service change ever happens.
/// </summary>
public sealed class DemoClientPcForm : DemoViewForm
{
    private const string LaptopRefusal = "이 프로그램은 데스크톱 PC에만 설치할 수 있습니다. 노트북에는 설치할 수 없습니다.";
    private const string ConsoleFirstRefusal = "관리자 프로그램이 설치된 PC에는 클라이언트를 설치할 수 없습니다.";

    private readonly DemoServerClient _client;
    private PresetFile _presets = new()
    {
        Presets =
        [
            new ClassPreset { Name = "1교시 수업 시작", Actions = [new() { Type = PresetActionTypes.Unmute }, new() { Type = PresetActionTypes.SetVolume, Value = 40 }, new() { Type = PresetActionTypes.DisplayMode, Mode = "clone" }, new() { Type = PresetActionTypes.OpenUrl, Url = "https://www.ebs.co.kr" }] },
            new ClassPreset { Name = "시험 시간", Actions = [new() { Type = PresetActionTypes.Mute }, new() { Type = PresetActionTypes.CloseApp, ProcessName = "chrome" }] },
        ],
    };
    private readonly Label _status = new() { Dock = DockStyle.Fill, ForeColor = Color.White, BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleCenter, Font = Ui.Big };

    public DemoClientPcForm(DemoServerClient client)
    {
        _client = client;
        Ui.Apply(this, "클라이언트(교실 PC) 화면 (체험)", new Size(820, 560));
        StartPosition = FormStartPosition.CenterScreen;

        // A faux desktop: dark "wallpaper" with a taskbar and a GAC tray icon at the bottom-right.
        var desktop = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(32, 54, 86) };
        _status.Text = "교실 PC 바탕화면\n\n오른쪽 아래 작업 표시줄의 GAC 아이콘을 누르면 선생님용 트레이 메뉴가 나옵니다.";
        desktop.Controls.Add(_status);

        var taskbar = new Panel { Dock = DockStyle.Bottom, Height = 40, BackColor = Color.FromArgb(24, 28, 36) };
        var clock = new Label { Dock = DockStyle.Right, Width = 70, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter, Text = DateTime.Now.ToString("HH:mm") };
        var tray = new Button { Dock = DockStyle.Right, Width = 44, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(24, 28, 36), Image = GacIcons.GacSized(32).ToBitmap(), ImageAlign = ContentAlignment.MiddleCenter };
        tray.FlatAppearance.BorderSize = 0;
        var menu = BuildTrayMenu();
        tray.Click += (_, _) => menu.Show(tray, new Point(0, -menu.Height));
        var start = new Label { Dock = DockStyle.Left, Width = 90, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter, Text = "⊞ 시작" };
        taskbar.Controls.Add(clock);
        taskbar.Controls.Add(tray);
        taskbar.Controls.Add(start);
        desktop.Controls.Add(taskbar);

        // A side panel with the same actions as buttons, so every feature is one click away.
        var side = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 240, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(10), BackColor = Ui.Panel };
        side.Controls.Add(Ui.Label("트레이 앱 기능 (체험)", Ui.Bold));
        side.Controls.Add(SideButton("트레이 메뉴 열기", (_, _) => menu.Show(this, PointToClient(Cursor.Position))));
        side.Controls.Add(SideButton("수업 모드 프리셋 편집", (_, _) => OpenPresetEditor()));
        side.Controls.Add(SideButton("네트워크 자가 진단", (_, _) => OpenNetDiag()));
        side.Controls.Add(SideButton("자동 종료 경고 창", (_, _) => OpenShutdownWarning()));
        side.Controls.Add(SideButton("기간 잠금 실행 (점심시간 컴퓨터실 잠금)", (_, _) => OpenLock()));
        _exemptToggle.CheckedChanged += (_, _) => OnExemptToggled();
        side.Controls.Add(_exemptToggle);
        side.Controls.Add(new Label { Height = 10, Width = 1 });
        side.Controls.Add(Ui.Label("설치 (체험)", Ui.Bold));
        side.Controls.Add(SideButton("에이전트 설치 화면 보기", (_, _) => OpenAgentInstaller()));

        Controls.Add(desktop);
        Controls.Add(side);
        Controls.Add(Ui.ButtonBar(Ui.Button("닫기", (_, _) => Close(), 80)));

        InstallChrome(DemoView.Client);

        // The agent re-checks the exemption list on every poll; if a day becomes an exemption day (e.g. added in the
        // console calendar) while the lock window is open, the lock closes and explorer.exe restarts (CHANGE-002 §2.2).
        _exemptToggle.Checked = IsExemptToday();
        _poll.Tick += (_, _) => { if (_lock != null && IsExemptToday()) CloseLockForExemption(); };
        Load += (_, _) => _poll.Start();
        FormClosed += (_, _) => _poll.Dispose();
    }

    private readonly CheckBox _exemptToggle = new() { Text = "오늘을 잠금 해제일로 체험\n(" + ExemptionCalendar.PresetName + ")", AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 2000 };
    private LockForm? _lock;

    internal LockForm? CurrentLock => _lock;
    internal CheckBox ExemptToggle => _exemptToggle;

    /// <summary>The same decision the agent makes: lock_pc never runs when today (Asia/Seoul, trusted time) is an exemption day.</summary>
    internal bool IsExemptToday() => ExemptionCalendar.IsExempt(_client.AgentExemptDates(), DateTimeOffset.Now);

    private void OnExemptToggled()
    {
        if (_exemptToggle.Checked == IsExemptToday() && !(_exemptToggle.Checked && _lock != null)) return;
        _client.SetDemoExemptToday(_exemptToggle.Checked);
        if (_exemptToggle.Checked && _lock != null) CloseLockForExemption();
        else _status.Text = _exemptToggle.Checked
            ? "(체험) 오늘은 잠금 해제일입니다. 어떤 기간이든 PC 잠금이 실행되지 않습니다.\n자동 종료·자동 정리 등 다른 기능은 평소처럼 동작합니다."
            : "(체험) 오늘은 잠금 해제일이 아닙니다. 기간 잠금이 다시 동작합니다.";
    }

    internal void CloseLockForExemption()
    {
        _lock?.ForceClose();
        _lock = null;
        _status.Text = "(체험) 잠금 해제일이 시작되어 잠금 화면을 닫고 탐색기(explorer.exe)를 다시 시작했습니다.\n실제 PC에서는 아무 프로그램도 실행되지 않았습니다.";
    }

    private static Button SideButton(string text, EventHandler onClick)
    {
        var b = Ui.Button(text, onClick, 215);
        b.Height = 34;
        b.Margin = new Padding(0, 4, 0, 0);
        return b;
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip { Font = Ui.Font };
        foreach (var p in DemoSchool.SharedPresets().Presets)
            menu.Items.Add(new ToolStripMenuItem($"▶ {p.Name}  (관리자 배포)", null, async (_, _) => await RunPresetAsync(SetShared(p))));
        foreach (var p in _presets.Presets)
        {
            var preset = p;
            menu.Items.Add(new ToolStripMenuItem($"▶ {p.Name}", null, async (_, _) => await RunPresetAsync(preset)));
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("✎ 수업 모드 프리셋 편집…", null, (_, _) => OpenPresetEditor()));
        menu.Items.Add(new ToolStripMenuItem("🌐 네트워크 자가 진단…", null, (_, _) => OpenNetDiag()));
        menu.Items.Add(new ToolStripMenuItem("⏻ 자동 종료 경고 창 미리 보기", null, (_, _) => OpenShutdownWarning()));
        return menu;
    }

    private static ClassPreset SetShared(ClassPreset p) { p.Shared = true; return p; }

    private void OpenPresetEditor()
    {
        using var editor = new PresetEditorForm(_presets, allowRunCommand: false, f => { _presets = f; return Task.FromResult<string?>(null); },
            RunPresetAsync, "수업 모드 프리셋 편집 (체험)");
        editor.ShowDialog(this);
    }

    private void OpenNetDiag()
    {
        using var f = new NetDiagForm(new DemoNetDiagHost());
        f.ShowDialog(this);
    }

    private void OpenShutdownWarning()
    {
        using var w = new ShutdownWarningForm(DateTimeOffset.Now.AddMinutes(5), 2, 30);
        w.ExtendClicked += (_, _) => { Ui.Info(w, "(체험) 종료가 30분 연장되었습니다."); w.Close(); };
        w.ShutdownNowClicked += (_, _) => { Ui.Info(w, "(체험) 실제 제품에서는 PC가 지금 종료됩니다. 체험 모드에서는 아무 일도 일어나지 않습니다."); w.Close(); };
        w.ShowDialog(this);
    }

    /// <summary>Shows the full-screen lock exactly as a client PC would during a period. Nothing is actually locked.</summary>
    internal void OpenLock()
    {
        if (_lock != null) return;
        if (IsExemptToday())
        {
            _status.Text = "(체험) 오늘은 잠금 해제일이라 잠금 화면이 나타나지 않습니다.";
            Ui.Info(this, $"오늘은 잠금 해제일({ExemptionCalendar.PresetName})입니다.\n\n기간 프리셋에 PC 잠금이 들어 있어도 오늘은 잠금 화면이 나타나지 않고, 탐색기도 끝내지 않습니다.\n(잠금 해제일은 00:00~23:59:59, 한국 시간 기준이며 PC 시계를 바꿔도 서버 시간으로 판단합니다.)");
            return;
        }
        var lf = new LockForm("점심시간 컴퓨터실 잠금", 25)
        {
            Unlock = (_, pw) => Task.FromResult(string.IsNullOrEmpty(pw)
                ? (false, "비밀번호를 입력하세요.")
                : (true, "(체험) 관리자 확인됨 — 실제 제품에서는 서버가 비밀번호와 권한을 확인합니다.")),
        };
        lf.ExitRequested += (_, _) => { Ui.Info(lf, "(체험) ‘종료하기’는 실제 제품에서 PC를 끕니다. 체험 모드에서는 잠금 화면만 닫습니다."); lf.ForceClose(); };
        lf.Expired += (_, _) => { Ui.Info(this, "(체험) 시간이 다 되어 실제 제품에서는 PC가 종료됩니다."); lf.ForceClose(); };
        lf.FormClosed += (_, _) => { if (_lock == lf) _lock = null; };
        _lock = lf;
        lf.Show(this);
    }

    private async Task RunPresetAsync(ClassPreset p)
    {
        var lines = new List<string>();
        foreach (var a in p.Actions)
        {
            _status.Text = $"(체험) 실행 중: {a.Describe()}";
            await Task.Delay(a.Type == PresetActionTypes.Wait ? Math.Min(3, a.Seconds ?? 1) * 300 : 350);
            lines.Add("✔ " + a.Describe());
        }
        _status.Text = $"(체험) '{p.Name}' 실행 완료 — 실제 PC 설정은 바뀌지 않았습니다.";
        Ui.Info(this, $"'{p.Name}' 프리셋을 실행한 것처럼 보여 드렸습니다:\n\n{string.Join("\n", lines)}\n\n(체험 모드에서는 볼륨·화면·프로그램이 실제로 바뀌지 않습니다.)");
    }

    /// <summary>Fake agent installer with the two server-enforced refusals from CHANGE-001 §1.</summary>
    private void OpenAgentInstaller()
    {
        using var f = new Form();
        Ui.Apply(f, "GAC 클라이언트(에이전트) 설치 (체험)", new Size(560, 360), resizable: false);
        f.StartPosition = FormStartPosition.CenterParent;

        var intro = Ui.Label("GAC 클라이언트는 교실·교무실의 데스크톱 PC에 설치합니다.\n설치할 PC 종류를 눌러 설치가 어떻게 진행되는지 체험해 보세요.\n\n(노트북 여부와 관리자 PC 여부는 설치 프로그램과 서버가 함께 확인하며, 서버가 최종 판단합니다.)");
        intro.Dock = DockStyle.Top;
        intro.Height = 120;
        intro.Padding = new Padding(16, 14, 16, 0);
        intro.MaximumSize = new Size(520, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(40, 6, 40, 6) };
        buttons.Controls.Add(Big("데스크톱 PC에 설치 (정상)", (_, _) =>
            Ui.Info(f, "설치할 수 있는 PC입니다. (체험)\n\n에이전트 서비스와 트레이 앱을 설치하고, 서버에 등록한 뒤 자동으로 시작합니다.\n체험 모드에서는 실제로 아무것도 설치되지 않습니다.")));
        buttons.Controls.Add(Big("노트북에 설치 시도", (_, _) =>
            Ui.Error(f, LaptopRefusal + "\n\n(하드웨어 감지: 섀시 유형·덮개·배터리 등으로 노트북으로 판정되어 서버가 설치를 거부했습니다.)")));
        buttons.Controls.Add(Big("관리자 프로그램(콘솔)이 설치된 PC에 설치 시도", (_, _) =>
            Ui.Error(f, ConsoleFirstRefusal + "\n\n(이 PC는 콘솔이 먼저 설치된 기록이 있어, 콘솔을 지워도 클라이언트가 될 수 없습니다.)")));

        f.Controls.Add(buttons);
        f.Controls.Add(intro);
        f.Controls.Add(Ui.ButtonBar(Ui.Button("닫기", (_, _) => f.Close(), 80)));
        f.ShowDialog(this);
    }

    private static Button Big(string text, EventHandler onClick)
    {
        var b = Ui.Button(text, onClick, 460);
        b.Height = 44;
        b.Margin = new Padding(0, 6, 0, 0);
        return b;
    }
}
