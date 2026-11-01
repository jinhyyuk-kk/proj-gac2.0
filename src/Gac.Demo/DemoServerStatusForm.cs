using Gac.Shared.Contracts;
using Gac.Tray.UI;

namespace Gac.Demo;

/// <summary>
/// CHANGE-001 §7 "서버 PC 화면": the fake server installer steps (incl. the 감지된 교내망 page and the end-of-setup
/// screen with address and setup code) and a fake "GAC 서버 상태" window with live-looking numbers and log lines.
/// This does NOT reuse the real <c>Gac.ServerStatus.StatusForm</c>, which runs <c>sc.exe</c> and reads the real DB —
/// everything here is simulated and touches nothing on the machine.
/// </summary>
public sealed class DemoServerStatusForm : DemoViewForm
{
    private const string DetectedRange = "10.10.0.0/16";
    private readonly DemoServerClient _client;
    private readonly EnrollmentInfo _enroll;
    private readonly Label _state = Ui.Label("", Ui.Big);
    private readonly Label _summary = Ui.Label("", color: Ui.Muted);
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f), BackColor = Color.White };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly Random _rng = new();
    private readonly List<string> _lines = [];

    public DemoServerStatusForm(DemoServerClient client)
    {
        _client = client;
        _enroll = client.EnrollmentNow();
        Ui.Apply(this, "GAC 서버 상태 (체험)", new Size(680, 600));
        StartPosition = FormStartPosition.CenterScreen;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(14, 10, 14, 6), WrapContents = false };
        _summary.MaximumSize = new Size(630, 0);
        top.Controls.Add(_state);
        top.Controls.Add(_summary);

        var info = Ui.Grid();
        Ui.AddRow(info, "서버 주소", Ro($"{_enroll.ServerAddress}:{_enroll.Port}"));
        Ui.AddRow(info, "감지된 교내망", Ro(DetectedRange));
        Ui.AddRow(info, "방화벽", Ro($"이 대역({DetectedRange})과 포트 {_enroll.Port}만 허용 · 인터넷 열지 않음"));
        Ui.AddRow(info, "설치 코드", Ro(_enroll.Code));
        var infoBox = new GroupBox { Text = "서버 정보", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 4, 10, 8) };
        infoBox.Controls.Add(info);

        var svc = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 2, 0, 4), WrapContents = true };
        svc.Controls.AddRange(
        [
            Ui.Button("시작", (_, _) => Fake("서비스를 시작"), 70),
            Ui.Button("중지", (_, _) => Fake("서비스를 중지"), 70),
            Ui.Button("다시 시작", (_, _) => Fake("서비스를 다시 시작"), 90),
            Ui.Button("대역 다시 감지", (_, _) => Ui.Info(this, $"(체험) 교내망을 다시 감지했습니다: {DetectedRange}\n실제 제품에서는 이 대역과 포트 {_enroll.Port}만 방화벽에 등록합니다."), 120),
            Ui.Button("클라이언트 설치 파일 만들기", (_, _) => Ui.Info(this, "(체험) 실제 제품에서는 서버 주소·포트·인증서 지문·등록 코드가 담긴 gac-agent.json과 함께\n조용히(무인) 설치할 수 있는 클라이언트 설치 폴더를 만듭니다."), 200),
            Ui.Button("서버 설치 과정 보기", (_, _) => RunInstallWizard(), 150),
        ]);

        var logBox = new GroupBox { Text = "최근 서버 로그 (체험)", Dock = DockStyle.Fill, Padding = new Padding(8) };
        logBox.Controls.Add(_log);

        Controls.Add(logBox);
        Controls.Add(svc);
        Controls.Add(infoBox);
        Controls.Add(top);
        Controls.Add(Ui.ButtonBar(Ui.Button("닫기", (_, _) => Close(), 80)));

        InstallChrome(DemoView.Server);

        Refresh2();
        for (var i = 0; i < 6; i++) AddLogLine(DateTimeOffset.Now.AddSeconds(-_rng.Next(5, 240)));
        FlushLog();
        _timer.Tick += (_, _) => { Refresh2(); AddLogLine(DateTimeOffset.Now); FlushLog(); };
        Load += (_, _) => _timer.Start();
        FormClosed += (_, _) => _timer.Dispose();

        // First open also walks through the installer steps, so the server story is seen even without clicking the button.
        Shown += (_, _) => BeginInvoke(RunInstallWizard);
    }

    private static TextBox Ro(string text) => new() { Text = text, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(244, 246, 249), Width = 440, Font = Ui.Bold };

    private void Fake(string what) { AddLogLine(DateTimeOffset.Now, $"관리자가 {what}했습니다 (체험)"); FlushLog(); Ui.Info(this, $"(체험) 실제 제품에서는 Windows 서비스를 {what}합니다. 체험 모드에서는 아무 일도 일어나지 않습니다."); }

    private void Refresh2()
    {
        var pcs = _client.Pcs;
        var online = pcs.Count(p => p.Online);
        _state.Text = "● 실행 중";
        _state.ForeColor = Ui.Ok;
        _summary.Text = $"등록된 PC {pcs.Count}대 · 온라인 {online}대 · 교내망 {DetectedRange} · 포트 {_enroll.Port} · 데이터베이스 정상";
    }

    private void AddLogLine(DateTimeOffset at, string? forced = null)
    {
        var pcs = _client.Pcs;
        string line;
        if (forced != null) line = forced;
        else if (pcs.Count == 0) line = "서버 대기 중";
        else
        {
            var pc = pcs[_rng.Next(pcs.Count)];
            line = _rng.Next(5) switch
            {
                0 => $"{pc.Name} heartbeat ok",
                1 => $"{pc.Name} health report received (free {pc.FreePercent:0}% )",
                2 => $"{pc.Name} enrolled / verified (desktop)",
                3 => $"command delivered → {pc.Name}",
                _ => $"{pc.Name} poll ok",
            };
        }
        _lines.Add($"[{at:HH:mm:ss}] {line}");
        if (_lines.Count > 200) _lines.RemoveRange(0, _lines.Count - 200);
    }

    private void FlushLog()
    {
        _log.Text = string.Join(Environment.NewLine, _lines);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    /// <summary>Fake, read-only walkthrough of the server installer — no files, no service, no firewall.</summary>
    private void RunInstallWizard()
    {
        using var wiz = new Form();
        Ui.Apply(wiz, "GAC 서버 설치 (체험)", new Size(580, 420), resizable: false);
        wiz.StartPosition = FormStartPosition.CenterParent;

        var title = new Label { Dock = DockStyle.Top, Height = 44, Font = Ui.Title, ForeColor = Ui.Accent, Padding = new Padding(14, 8, 0, 0) };
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 6, 18, 6) };

        (string Title, string Body)[] pages =
        [
            ("환영합니다", "이 설치 프로그램은 이 PC(관리 PC)에 GAC 서버를 Windows 서비스로 설치합니다.\n\n서버 전용 하드웨어나 Windows Server가 필요 없습니다. 보통 데스크톱 PC에 설치하며, 인터넷 연결 없이 교내망 안에서만 동작합니다."),
            ("설치 폴더", "프로그램: C:\\Program Files\\GAC 2.0\\Server\n데이터베이스: C:\\ProgramData\\GAC\\Server\\gac.db (SQLite)\n\n함께 ‘GAC 서버 상태’ 앱도 설치되어, 이 PC에서 서버 상태를 보고 서비스를 켜고 끌 수 있습니다."),
            ("감지된 교내망", $"현재 교내망 대역이 자동으로 감지되었습니다:\n\n        {DetectedRange}   (포트 {_enroll.Port})\n\nGAC는 방화벽에서 이 대역과 포트만 엽니다. 다른 PC나 인터넷에는 아무 포트도 열지 않습니다.\n필요하면 설치 후 ‘GAC 서버 상태’에서 대역을 다시 감지하거나 추가할 수 있습니다."),
            ("설치 완료", $"GAC 서버 설치가 끝났습니다. (체험 모드 – 실제로는 아무것도 설치되지 않았습니다.)\n\n서버 주소: {_enroll.ServerAddress}:{_enroll.Port}\n설치 코드: {_enroll.Code}\n\n콘솔에서 처음 회원가입할 때 이 설치 코드를 입력하세요. 코드는 C:\\ProgramData\\GAC\\Server\\enrollment.txt 에도 저장됩니다."),
        ];

        var step = 0;
        var back = Ui.Button("이전", null, 80);
        var nextB = Ui.Button("다음", null, 100);

        void Render()
        {
            title.Text = $"{step + 1}/{pages.Length}  {pages[step].Title}";
            body.Controls.Clear();
            var lbl = Ui.Label(pages[step].Body);
            lbl.Dock = DockStyle.Fill;
            lbl.MaximumSize = new Size(520, 0);
            body.Controls.Add(lbl);
            back.Enabled = step > 0;
            nextB.Text = step == pages.Length - 1 ? "닫기" : "다음";
        }

        back.Click += (_, _) => { if (step > 0) { step--; Render(); } };
        nextB.Click += (_, _) => { if (step < pages.Length - 1) { step++; Render(); } else wiz.Close(); };

        wiz.Controls.Add(body);
        wiz.Controls.Add(title);
        wiz.Controls.Add(Ui.ButtonBar(back, nextB));
        wiz.AcceptButton = nextB;
        Render();
        wiz.ShowDialog(this);
    }
}
