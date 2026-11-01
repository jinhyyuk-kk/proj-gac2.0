using Gac.ServerAdmin.UI;
using Gac.Tray.UI;

namespace Gac.Demo;

/// <summary>
/// "서버 PC 화면" (CHANGE-001 §7, CHANGE-002 §5): the fake server installer (normal pages → [다음] → 방화벽 열기 →
/// 설치 완료) and the real GAC 서버 관리 window (상태 / 자동 종료 / 과부하 차단 / 로그) running on a demo backend.
/// Nothing touches the real firewall, services, files or network.
/// </summary>
public sealed class DemoServerStatusForm : DemoViewForm
{
    private readonly DemoServerClient _client;
    private readonly DemoServerAdminBackend _backend;
    private readonly ServerAdminView _view;

    public DemoServerStatusForm(DemoServerClient client)
    {
        _client = client;
        _backend = new DemoServerAdminBackend(client);
        Ui.Apply(this, "GAC 서버 관리 (체험)", new Size(960, 660));
        StartPosition = FormStartPosition.CenterScreen;
        for (var i = 0; i < 12; i++) _backend.GetLogs();

        _view = new ServerAdminView(_backend);
        var side = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10, 4, 10, 4), BackColor = Color.FromArgb(255, 250, 230) };
        side.Controls.AddRange([
            Ui.Label("체험 도구:", Ui.Bold),
            Ui.Button("서버 설치 과정 보기", (_, _) => RunInstallWizard(), 150),
            Ui.Button("자동 종료 10분 전 상황 만들기", (_, _) => { _client.SimulateServerShutdownWarning(); _backend.Log("경고", "서버 PC가 10분 후 종료됩니다. (체험)"); _view.CheckShutdownWarning(); }, 210),
            Ui.Button("교실 PC 과부하 상황 만들기", (_, _) =>
            {
                var a = _client.SimulateOverload();
                _backend.Log("경고", $"과부하 차단: {a.WindowsName} ({a.Ip}) 분당 {a.RequestCount}회 → {a.BlockedUntil:HH:mm}까지 차단, 운영자 PC로 알림");
                _view.Show("과부하 차단");
                Ui.Info(this, $"(체험) {a.WindowsName}({a.Ip})이(가) 1분에 {a.RequestCount}번 요청해 {a.BlockedUntil:HH:mm}까지 차단되었습니다.\n관리자 화면(콘솔)의 [알림] 탭과 Windows 알림으로 운영자에게 전달됩니다.");
            }, 200),
        ]);

        Controls.Add(_view);
        Controls.Add(side);
        Controls.Add(Ui.ButtonBar(Ui.Button("닫기", (_, _) => Close(), 80)));
        InstallChrome(DemoView.Server);

        // First open walks through the installer, so the server story is seen even without clicking the button.
        Shown += (_, _) => { if (!_client.InstallWizardShown) { _client.InstallWizardShown = true; BeginInvoke(RunInstallWizard); } };
    }

    internal ServerAdminView View => _view;
    internal DemoServerAdminBackend Backend => _backend;

    /// <summary>Fake server installer. Closing it before the firewall step leaves the server "설치 미완료".</summary>
    internal void RunInstallWizard()
    {
        using var wiz = new DemoServerInstallWizard(_client, _backend);
        wiz.ShowDialog(this);
        _view.Show("상태");
    }
}

/// <summary>
/// The server installer as a demo (CHANGE-002 §3.1): welcome → folder → installing → page after installation with
/// [다음] (not [마침]) → 방화벽 열기 (range + port editable, [다음] disabled until the rule exists and is verified) →
/// 설치 완료 with address, port, range and setup code — only now a [마침] button. Cancel is hidden after installation.
/// </summary>
internal sealed class DemoServerInstallWizard : Form
{
    internal const int FirewallStep = 4, FinishStep = 5;
    private readonly DemoServerClient _client;
    private readonly DemoServerAdminBackend _backend;
    private readonly Label _title = new() { Dock = DockStyle.Top, Height = 44, Font = Ui.Title, ForeColor = Ui.Accent, Padding = new Padding(14, 8, 0, 0) };
    private readonly Panel _body = new() { Dock = DockStyle.Fill, Padding = new Padding(18, 6, 18, 6) };
    private readonly Button _back = Ui.Button("이전", null, 80);
    private readonly Button _next = Ui.Button("다음", null, 100);
    private readonly Button _cancel = Ui.Button("취소", null, 80);
    private readonly TextBox _range = new() { Width = 200, Text = "10.10.0.0/16" };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 8443, Width = 90 };
    private readonly Label _fwResult = Ui.Label("");
    private bool _firewallDone;

    public int Step { get; private set; }

    public DemoServerInstallWizard(DemoServerClient client, DemoServerAdminBackend backend)
    {
        _client = client;
        _backend = backend;
        Ui.Apply(this, "GAC 2.0 서버 설치 (체험)", new Size(600, 430), resizable: false);
        Icon = GacIcons.Gac;
        StartPosition = FormStartPosition.CenterParent;
        _back.Click += (_, _) => { if (Step is > 0 and < 3) { Step--; Render(); } };
        _next.Click += (_, _) => Next();
        _cancel.Click += (_, _) => Close();
        FormClosing += (_, e) =>
        {
            if (Step >= 3 && Step < FinishStep)
            {
                // Files are installed but the firewall step is not done: in the real installer Cancel/X are blocked; here
                // closing simulates the wizard being killed, so the service stays "설치 미완료".
                if (!Ui.Confirm(this, "(체험) 방화벽 단계를 마치지 않고 닫을까요?\n실제 설치 프로그램에서는 이 단계를 건너뛸 수 없습니다. 창이 강제로 닫힌 경우를 체험하려면 [예]를 누르세요.\n\n서버는 ‘설치 미완료’ 상태가 되어 PC 등록과 회원가입을 받지 않습니다.")) { e.Cancel = true; return; }
                _client.MarkInstallIncomplete();
                _backend.Log("경고", "설치 미완료: 방화벽 단계를 마치지 않았습니다. 등록·회원가입을 받지 않습니다.");
            }
        };
        Controls.Add(_body);
        Controls.Add(_title);
        Controls.Add(Ui.ButtonBar(_cancel, _next, _back));
        AcceptButton = _next;
        Render();
    }

    internal bool FirewallDone => _firewallDone;

    internal void Next()
    {
        if (Step == FirewallStep && !_firewallDone) return;
        if (Step == FinishStep) { Close(); return; }
        Step++;
        Render();
    }

    internal string? ClickOpenFirewall()
    {
        var err = _backend.OpenFirewall(_range.Text.Trim(), (int)_port.Value);
        _firewallDone = err == null;
        _fwResult.Text = err == null ? $"✓ 방화벽 규칙을 만들고 다시 읽어 확인했습니다. (TCP {_port.Value}, {_range.Text.Trim()}만 허용, 모든 프로필)" : "✗ " + err;
        _fwResult.ForeColor = err == null ? Ui.Ok : Ui.Bad;
        _next.Enabled = _firewallDone;
        return err;
    }

    private void Render()
    {
        _body.Controls.Clear();
        var enroll = _client.EnrollmentNow();
        (string title, string text) = Step switch
        {
            0 => ("환영합니다", "이 설치 프로그램은 이 PC(관리 PC)에 GAC 서버를 Windows 서비스로 설치합니다.\n\n서버 전용 하드웨어나 Windows Server가 필요 없습니다. 보통 데스크톱 PC에 설치하며, 인터넷 연결 없이 교내망 안에서만 동작합니다."),
            1 => ("설치 폴더", "프로그램: C:\\Program Files\\GAC 2.0\\Server\n데이터베이스: C:\\ProgramData\\GAC\\Server\\gac.db (SQLite)\n\n‘GAC 서버 관리’ 앱도 함께 설치되어, 이 PC에서 서버 상태·자동 종료·과부하 차단·로그를 관리합니다."),
            2 => ("설치하는 중", "파일 복사… 완료\n인증서 만들기… 완료\n데이터베이스 준비… 완료\nWindows 서비스 등록… 완료\n\n(체험 모드 – 실제로는 아무것도 설치되지 않습니다.)"),
            3 => ("파일 설치가 끝났습니다", "프로그램 파일과 서비스가 설치되었습니다.\n\n하지만 아직 설치가 끝나지 않았습니다. 다음 단계에서 방화벽을 열어야 교실 PC가 서버에 연결할 수 있습니다.\n[다음]을 누르세요."),
            4 => ("방화벽 열기", ServerAdminView.FirewallText),
            _ => ("설치 완료", $"GAC 서버 설치가 끝났습니다. (체험 모드)\n\n서버 주소: {enroll.ServerAddress}:{_client.Port}\n포트: {_client.Port}\n교내망 대역: {_client.LanRange}\n설치 코드: {_client.SetupCodeForDisplay ?? "(이미 회원가입 완료됨)"}\n\n설치 코드는 콘솔에서 처음 회원가입할 때 필요합니다. 꼭 적어 두세요.\n(C:\\ProgramData\\GAC\\Server\\enrollment.txt 에도 저장됩니다.)"),
        };
        _title.Text = $"{Step + 1}/6  {title}";
        var lbl = Ui.Label(text);
        lbl.MaximumSize = new Size(550, 0);
        var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        stack.Controls.Add(lbl);
        if (Step == FirewallStep)
        {
            stack.Controls.Add(Ui.Label("감지된 교내망 대역 (유선 기본 게이트웨이 어댑터 기준, 고칠 수 있음):", color: Ui.Muted));
            stack.Controls.Add(Ui.Row(Ui.Label("교내망 대역 (CIDR)"), _range));
            stack.Controls.Add(Ui.Row(Ui.Label("포트"), _port));
            stack.Controls.Add(Ui.Button("방화벽 열기", (_, _) => ClickOpenFirewall(), 130));
            _fwResult.MaximumSize = new Size(550, 0);
            stack.Controls.Add(_fwResult);
        }
        _body.Controls.Add(stack);

        _back.Visible = Step is > 0 and < 3;
        _cancel.Visible = Step < 3; // after installation there is no Cancel
        _next.Text = Step == FinishStep ? "마침" : "다음"; // the page after installation says [다음], never [마침]/[종료]
        _next.Enabled = Step != FirewallStep || _firewallDone;
    }
}
