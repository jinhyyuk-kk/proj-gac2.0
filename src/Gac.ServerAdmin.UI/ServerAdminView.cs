using Gac.Shared.Contracts;
using Gac.Tray.UI;

namespace Gac.ServerAdmin.UI;

/// <summary>
/// GAC 서버 관리 (CHANGE-002 §4): plain layout with a left-side menu — 상태 / 자동 종료 / 과부하 차단 / 로그.
/// While the installer's firewall step is not done, 상태 shows the big "설치를 완료하려면 방화벽을 여세요" panel.
/// </summary>
public sealed class ServerAdminView : UserControl
{
    public const string FirewallPrompt = "설치를 완료하려면 방화벽을 여세요";
    public const string FirewallText = "교내망에서만 서버에 접속할 수 있도록 방화벽을 엽니다.";

    private readonly IServerAdminBackend _backend;
    private readonly Panel _content = new() { Dock = DockStyle.Fill, Padding = new Padding(12) };
    private readonly Dictionary<string, Button> _nav = [];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    private string _section = "상태";
    private bool _noticeShown;

    // 상태
    private readonly Label _service = Ui.Label("", Ui.Title);
    private readonly Label _summary = Ui.Label("");
    private readonly Panel _firewallPanel = new() { Dock = DockStyle.Top, Height = 175, BackColor = Color.FromArgb(255, 244, 229), Padding = new Padding(14), Visible = false };
    private readonly TextBox _fwRange = new() { Width = 180 };
    private readonly NumericUpDown _fwPort = new() { Minimum = 1, Maximum = 65535, Value = 8443, Width = 90 };
    private readonly Label _fwResult = Ui.Label("");
    // 자동 종료
    private readonly ShutdownScheduleEditor _schedule = new();
    private readonly Label _shutdownStatus = Ui.Label("", color: Ui.Muted);
    // 과부하 차단
    private readonly OverloadEditor _overload = new();
    private readonly ListView _blocks = Ui.List(("PC 별명", 110), ("Windows 이름", 120), ("IP", 110), ("제한", 140), ("차단 해제 시각", 120), ("횟수", 50));
    // 로그
    private readonly ComboBox _level = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly ListView _logs = Ui.List(("시각", 130), ("수준", 60), ("내용", 560));

    private readonly Dictionary<string, Control> _pages = [];

    public ServerAdminView(IServerAdminBackend backend)
    {
        _backend = backend;
        Dock = DockStyle.Fill;
        var left = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 150, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Ui.Panel, Padding = new Padding(8, 12, 8, 8) };
        foreach (var s in new[] { "상태", "자동 종료", "과부하 차단", "로그" })
        {
            var b = new Button { Text = s, Width = 132, Height = 38, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft, Font = Ui.Big, Margin = new Padding(0, 0, 0, 4) };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (_, _) => Show(s);
            _nav[s] = b;
            left.Controls.Add(b);
        }
        _pages["상태"] = BuildStatus();
        _pages["자동 종료"] = BuildShutdown();
        _pages["과부하 차단"] = BuildOverload();
        _pages["로그"] = BuildLogs();
        Controls.Add(_content);
        Controls.Add(left);
        Show("상태");
        _timer.Tick += (_, _) => Tick();
        HandleCreated += (_, _) => _timer.Start();
        Disposed += (_, _) => _timer.Dispose();
    }

    public string Section => _section;
    internal Panel FirewallPanel => _firewallPanel;
    internal Label FirewallResult => _fwResult;
    internal Label Summary => _summary;

    public void Show(string section)
    {
        _section = section;
        foreach (var (name, b) in _nav) { b.BackColor = name == section ? Ui.Accent : Ui.Panel; b.ForeColor = name == section ? Color.White : SystemColors.ControlText; }
        _content.Controls.Clear();
        _content.Controls.Add(_pages[section]);
        Reload();
    }

    private void Reload()
    {
        try
        {
            switch (_section)
            {
                case "상태": RefreshStatus(); break;
                case "자동 종료": _schedule.Value = _backend.GetShutdown(); _shutdownStatus.Text = ShutdownText(_backend.GetShutdownStatus()); break;
                case "과부하 차단": _overload.Value = _backend.GetOverload(); FillBlocks(); break;
                case "로그": FillLogs(); break;
            }
        }
        catch (Exception e) { _summary.Text = "읽지 못했습니다: " + e.Message; }
    }

    private void Tick()
    {
        try
        {
            if (_section == "상태") RefreshStatus();
            else if (_section == "로그") FillLogs();
            else if (_section == "자동 종료") _shutdownStatus.Text = ShutdownText(_backend.GetShutdownStatus());
            CheckShutdownWarning();
        }
        catch (Exception) { /* transient: next tick */ }
    }

    /// <summary>10 minutes before the server PC shuts down: popup with [오늘은 끄지 않기] / [30분 연장] (CHANGE-002 §4.2).</summary>
    public void CheckShutdownWarning()
    {
        var s = _backend.GetShutdownStatus();
        if (!s.Warning) { _noticeShown = false; return; }
        if (_noticeShown) return;
        _noticeShown = true;
        var f = new ServerShutdownNoticeForm(s.NextShutdown, canChange: true, s.WaitingFor, onServerPc: true);
        f.SkipTodayClicked += (_, _) => Report(_backend.SkipShutdownToday(), "오늘은 서버 PC를 끄지 않습니다.");
        f.ExtendClicked += (_, _) => Report(_backend.ExtendShutdown(), "서버 PC 종료를 30분 연장했습니다.");
        f.FormClosed += (_, _) => { f.Dispose(); Reload(); };
        f.Show(FindForm());
    }

    // ---------- 상태 ----------
    private Control BuildStatus()
    {
        var page = new Panel { Dock = DockStyle.Fill };
        var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        _summary.MaximumSize = new Size(680, 0);
        head.Controls.Add(_service);
        head.Controls.Add(_summary);

        var svc = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 8, 0, 0) };
        svc.Controls.AddRange([
            Ui.Button("시작", (_, _) => Report(_backend.ServiceControl("start"), "서비스를 시작했습니다."), 70),
            Ui.Button("중지", (_, _) => Report(_backend.ServiceControl("stop"), "서비스를 중지했습니다."), 70),
            Ui.Button("다시 시작", (_, _) => Report(_backend.ServiceControl("restart"), "서비스를 다시 시작했습니다."), 90),
            Ui.Button("대역 다시 감지", (_, _) => Report(_backend.ReapplyDetectedRange(), "교내망 대역을 다시 감지해 방화벽에 적용했습니다."), 120),
            Ui.Button("대역 추가", (_, _) => AddRange(), 90),
            Ui.Button("클라이언트 설치 파일 만들기", (_, _) => { var m = _backend.ExportRollout(FindForm()!); if (m != null) Ui.Info(FindForm(), m); }, 200),
        ]);

        var title = Ui.Label(FirewallPrompt, new Font(Ui.Font.FontFamily, 18f, FontStyle.Bold), Ui.Bad);
        var text = Ui.Label(FirewallText + "\n서버 설치가 끝나지 않아 지금은 PC 등록·교실 PC 연결·콘솔 회원가입을 받지 않습니다.");
        text.MaximumSize = new Size(640, 0);
        var row = Ui.Row(Ui.Label("교내망 대역"), _fwRange, Ui.Label("포트"), _fwPort, Ui.Button("방화벽 열기", (_, _) => OpenFirewall(), 120));
        var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        stack.Controls.AddRange([title, text, row, _fwResult]);
        _firewallPanel.Controls.Add(stack);

        page.Controls.Add(svc);
        page.Controls.Add(head);
        page.Controls.Add(_firewallPanel);
        return page;
    }

    private void RefreshStatus()
    {
        var s = _backend.GetStatus();
        _service.Text = !s.InstallComplete ? "◐ 설치 미완료" : s.ServiceRunning ? "● 서비스 실행 중" : "○ 서비스 중지됨";
        _service.ForeColor = !s.InstallComplete ? Ui.Warn : s.ServiceRunning ? Ui.Ok : Ui.Bad;
        if (!s.InstallComplete && !_firewallPanel.Visible)
        {
            _fwRange.Text = s.Ranges.FirstOrDefault() ?? _backend.DetectRange() ?? "";
            _fwPort.Value = Math.Clamp(s.Port, 1, 65535);
        }
        _firewallPanel.Visible = !s.InstallComplete;
        var ranges = s.Ranges.Count == 0 ? "(미설정)" : string.Join(", ", s.Ranges);
        _summary.Text =
            $"주소: {s.ServerName}:{s.Port}\n" +
            $"교내망 대역: {ranges}   ·   방화벽 규칙: {(s.FirewallRuleOk ? "✓ 열림 (이 대역 + 포트만)" : "✗ 없음")}\n" +
            $"연결된 PC: {s.OnlineCount}/{s.PcCount}대   ·   마지막 등록: {(s.LastEnrollment is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "없음")}   ·   지금 차단된 수: {s.BlockedCount}\n" +
            (s.InstallComplete
                ? s.SetupCode != null ? $"설치 코드(최초 회원가입용): {s.SetupCode}   ← 콘솔에서 처음 회원가입할 때 입력하세요. 적어 두세요." : "설치 코드: (회원가입 완료됨)"
                : "설치 코드: 방화벽 열기를 마치면 표시됩니다.") +
            (string.IsNullOrEmpty(s.Thumbprint) ? "" : $"\n인증서 지문: {s.Thumbprint}");
    }

    private void OpenFirewall()
    {
        var err = _backend.OpenFirewall(_fwRange.Text.Trim(), (int)_fwPort.Value);
        _fwResult.Text = err == null ? "✓ 방화벽 규칙을 만들고 확인했습니다." : "✗ " + err;
        _fwResult.ForeColor = err == null ? Ui.Ok : Ui.Bad;
        if (err == null)
        {
            RefreshStatus();
            var s = _backend.GetStatus();
            Ui.Info(FindForm(), "설치가 끝났습니다.\n\n" + $"서버 주소: {s.ServerName}:{s.Port}\n교내망 대역: {string.Join(", ", s.Ranges)}\n설치 코드: {s.SetupCode ?? "(회원가입 완료됨)"}\n\n설치 코드는 콘솔에서 처음 회원가입할 때 필요합니다. 적어 두세요.");
        }
    }

    private void AddRange()
    {
        using var f = new Form();
        Ui.Apply(f, "대역 추가", new Size(360, 130), resizable: false);
        var t = new TextBox { Left = 16, Top = 16, Width = 300, PlaceholderText = "예: 10.20.0.0/22" };
        f.Controls.Add(t);
        f.Controls.Add(Ui.ButtonBar(Ui.Button("취소", (_, _) => f.DialogResult = DialogResult.Cancel, 70), Ui.Button("추가", (_, _) => f.DialogResult = DialogResult.OK, 70)));
        if (f.ShowDialog(FindForm()) != DialogResult.OK) return;
        Report(_backend.AddRange(t.Text.Trim()), "대역을 추가해 방화벽에 적용했습니다.");
    }

    // ---------- 자동 종료 ----------
    private Control BuildShutdown()
    {
        var page = new Panel { Dock = DockStyle.Fill };
        var intro = Ui.Label("이 PC(관리 PC)가 자동으로 꺼지는 시간입니다. 콘솔의 [서버 관리]에서 바꾼 설정과 같은 설정입니다.\n" +
                             "10분 전에 이 PC와 로그인한 콘솔에 알림이 뜨고, 배포·업데이트 작업이 진행 중이면 끝날 때까지(최대 30분) 기다립니다.\n" +
                             "PC가 꺼져 있어 예정 시각을 놓치면 다음에 켰을 때 끄지 않습니다.", color: Ui.Muted);
        intro.Dock = DockStyle.Top;
        intro.MaximumSize = new Size(700, 0);
        _shutdownStatus.Dock = DockStyle.Top;
        _shutdownStatus.Padding = new Padding(0, 6, 0, 6);
        page.Controls.Add(_schedule);
        page.Controls.Add(_shutdownStatus);
        page.Controls.Add(intro);
        page.Controls.Add(Ui.ButtonBar(
            Ui.Button("저장", (_, _) => { if (_schedule.Value is { } v) Report(_backend.SaveShutdown(v), "서버 PC 자동 종료 설정을 저장했습니다."); }, 80),
            Ui.Button("30분 연장", (_, _) => Report(_backend.ExtendShutdown(), "서버 PC 종료를 30분 연장했습니다."), 100),
            Ui.Button("오늘은 끄지 않기", (_, _) => Report(_backend.SkipShutdownToday(), "오늘은 서버 PC를 끄지 않습니다."), 140)));
        return page;
    }

    private static string ShutdownText(ServerShutdownStatus s) => ServerShutdownNoticeForm.Describe(s);

    // ---------- 과부하 차단 ----------
    private Control BuildOverload()
    {
        var page = new Panel { Dock = DockStyle.Fill };
        var intro = Ui.Label("PC 한 대가 요청을 너무 많이 보내면 그 PC와 주소를 잠시 막고(HTTP 429) 운영자 PC로 알림을 보냅니다.\n콘솔 로그인은 인증 실패 한도 외에는 영향을 받지 않습니다.", color: Ui.Muted);
        intro.Dock = DockStyle.Top;
        intro.MaximumSize = new Size(700, 0);
        var box = new GroupBox { Text = "지금 차단된 PC", Dock = DockStyle.Fill, Padding = new Padding(6) };
        box.Controls.Add(_blocks);
        box.Controls.Add(Ui.ButtonBar(
            Ui.Button("영구 차단", (_, _) => { if (SelectedBlock() is { } b && Ui.Confirm(FindForm(), $"{b.WindowsName ?? b.Ip}을(를) 영구 차단할까요? ‘차단된 기기’ 목록으로 옮겨집니다.")) Report(_backend.BlockPermanently(b.Key), "영구 차단했습니다."); }, 100),
            Ui.Button("차단 해제", (_, _) => { if (SelectedBlock() is { } b) Report(_backend.Unblock(b.Key), "차단을 해제했습니다."); }, 100)));
        page.Controls.Add(box);
        page.Controls.Add(_overload);
        page.Controls.Add(intro);
        page.Controls.Add(Ui.ButtonBar(Ui.Button("한도 저장", (_, _) => Report(_backend.SaveOverload(_overload.Value), "과부하 차단 설정을 저장했습니다."), 100)));
        return page;
    }

    private OverloadBlock? SelectedBlock()
    {
        if (_blocks.SelectedItems.Count == 0) { Ui.Error(FindForm(), "차단된 PC를 선택하세요."); return null; }
        return (OverloadBlock)_blocks.SelectedItems[0].Tag!;
    }

    private void FillBlocks()
    {
        _blocks.Items.Clear();
        foreach (var b in _backend.GetBlocks())
            _blocks.Items.Add(new ListViewItem([b.PcAlias ?? "-", b.WindowsName ?? "-", b.Ip, OverloadText.Korean(b.Limit), b.BlockedUntil.ToLocalTime().ToString("MM-dd HH:mm"), b.Offences.ToString()]) { Tag = b });
    }

    // ---------- 로그 ----------
    private Control BuildLogs()
    {
        var page = new Panel { Dock = DockStyle.Fill };
        _level.Items.AddRange(["전체", "정보", "경고", "오류"]);
        _level.SelectedIndex = 0;
        _level.SelectedIndexChanged += (_, _) => FillLogs();
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        bar.Controls.AddRange([Ui.Label("수준"), _level, Ui.Button("로그 폴더 열기", (_, _) => { var m = _backend.OpenLogFolder(); if (m != null) Ui.Info(FindForm(), m); }, 120)]);
        page.Controls.Add(_logs);
        page.Controls.Add(bar);
        return page;
    }

    private void FillLogs()
    {
        var level = _level.SelectedItem as string ?? "전체";
        var lines = _backend.GetLogs().Where(l => level == "전체" || l.Level == level).TakeLast(300).ToList();
        if (_logs.Items.Count == lines.Count && lines.Count > 0 && _logs.Items[^1].Tag == (object)lines[^1]) return;
        _logs.BeginUpdate();
        _logs.Items.Clear();
        foreach (var l in lines)
        {
            var item = new ListViewItem([l.At.ToLocalTime().ToString("MM-dd HH:mm:ss"), l.Level, l.Text]) { Tag = l };
            if (l.Level == "오류") item.ForeColor = Ui.Bad; else if (l.Level == "경고") item.ForeColor = Ui.Warn;
            _logs.Items.Add(item);
        }
        _logs.EndUpdate();
        if (_logs.Items.Count > 0) _logs.EnsureVisible(_logs.Items.Count - 1);
    }

    private void Report(string? error, string ok)
    {
        if (error != null) Ui.Error(FindForm(), error); else Ui.Info(FindForm(), ok);
        Reload();
    }
}

/// <summary>The GAC 서버 관리 window with its tray icon (Admin PC). Closing the window keeps the tray icon.</summary>
public sealed class ServerAdminForm : Form
{
    private readonly NotifyIcon _tray = new() { Text = "GAC 서버 관리", Visible = true };
    private bool _exit;

    public ServerAdminForm(IServerAdminBackend backend)
    {
        Ui.Apply(this, "GAC 서버 관리", new Size(900, 620));
        Icon = GacIcons.Gac;
        ShowIcon = true;
        StartPosition = FormStartPosition.CenterScreen;
        View = new ServerAdminView(backend);
        Controls.Add(View);
        _tray.Icon = GacIcons.GacSized(16);
        var menu = new ContextMenuStrip { Font = Ui.Font };
        menu.Items.Add("열기", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("끝내기", null, (_, _) => { _exit = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        FormClosing += (_, e) =>
        {
            if (_exit || e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true; // keep running in the tray so the T−10 shutdown popup can still appear
            Hide();
            _tray.ShowBalloonTip(3000, "GAC 서버 관리", "알림 영역에서 계속 실행됩니다. 끝내려면 아이콘을 오른쪽 클릭하세요.", ToolTipIcon.Info);
        };
        FormClosed += (_, _) => { _tray.Visible = false; _tray.Dispose(); };
    }

    public ServerAdminView View { get; }
}
