using Gac.Console.UI;
using Gac.Tray.UI;

namespace Gac.Demo;

/// <summary>The three sides of GAC a person can try in the demo (CHANGE-001 §7).</summary>
public enum DemoView { Console, Server, Client }

/// <summary>
/// GAC-Demo.exe start screen: pick one of the three sides (console / server PC / client PC). The same in-memory fake
/// server backs all three, so data is shared between them. Every view keeps the "체험 모드" banner and touches nothing
/// on the real machine — no files, no registry, no services, no network, no firewall.
/// </summary>
public sealed class DemoShell : Form
{
    private readonly DemoServerClient _client;
    private bool _running;

    public DemoShell(DemoServerClient client)
    {
        _client = client;
        Ui.Apply(this, "GAC 2.0 체험판", new Size(640, 560), resizable: false);
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;

        var banner = new Label { Text = DemoProgram.Banner, Dock = DockStyle.Top, Height = 32, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.FromArgb(255, 236, 153), Font = Ui.Bold };
        var head = new Label { Text = "GAC 2.0 체험판", Dock = DockStyle.Top, Height = 54, TextAlign = ContentAlignment.MiddleCenter, Font = Ui.Title, ForeColor = Ui.Accent };
        var intro = Ui.Label("설치하기 전에 GAC의 세 가지 화면을 모두 체험해 볼 수 있습니다.\n보고 싶은 화면을 고르세요. 언제든지 각 화면의 ‘화면 전환’ 메뉴로 바꿀 수 있습니다.");
        intro.Dock = DockStyle.Top;
        intro.Height = 56;
        intro.TextAlign = ContentAlignment.MiddleCenter;
        intro.ForeColor = Ui.Muted;

        var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(40, 10, 40, 10) };
        stack.Controls.Add(Card("🖥  관리자 화면 (콘솔)", "회원가입 → 로그인 → PC 관리, 계정·권한, 기기 차단 목록, 모든 모듈을 직접 조작해 보세요.", DemoView.Console));
        stack.Controls.Add(Card("🗄  서버 PC 화면", "서버 설치 과정(감지된 교내망·설치 코드)과 ‘GAC 서버 상태’ 창을 체험합니다.", DemoView.Server));
        stack.Controls.Add(Card("🏫  클라이언트(교실 PC) 화면", "트레이 앱·수업 모드·네트워크 진단·자동 종료 경고·PC 잠금 화면, 그리고 에이전트 설치 거부 화면을 봅니다.", DemoView.Client));

        var foot = Ui.Label("체험판은 실제 PC를 전혀 건드리지 않으며, 창을 닫으면 모든 내용이 사라집니다.", color: Ui.Muted);
        foot.Dock = DockStyle.Bottom;
        foot.Height = 40;
        foot.TextAlign = ContentAlignment.MiddleCenter;

        Controls.Add(stack);
        Controls.Add(foot);
        Controls.Add(intro);
        Controls.Add(head);
        Controls.Add(banner);
    }

    private Control Card(string title, string desc, DemoView view)
    {
        var btn = new Button
        {
            Width = 540, Height = 92, Margin = new Padding(0, 6, 0, 6), TextAlign = ContentAlignment.TopLeft,
            FlatStyle = FlatStyle.Flat, BackColor = Color.White, Font = Ui.Big, UseCompatibleTextRendering = false,
            Text = title + "\n\n" + desc, Padding = new Padding(16, 10, 16, 10),
        };
        btn.FlatAppearance.BorderColor = Ui.Accent;
        btn.FlatAppearance.BorderSize = 1;
        btn.Click += (_, _) => Launch(view);
        return btn;
    }

    private void Launch(DemoView view)
    {
        if (_running) return; // guard against double-click while a view is open
        _running = true;
        Hide();
        try
        {
            DemoView? current = view;
            while (current is DemoView v) current = RunView(v);
        }
        finally
        {
            _running = false;
            Show();
            Activate();
        }
    }

    private DemoView? RunView(DemoView view) => view switch
    {
        DemoView.Console => RunConsole(),
        DemoView.Server => RunDialog(new DemoServerStatusForm(_client)),
        DemoView.Client => RunDialog(new DemoClientPcForm(_client)),
        _ => null,
    };

    private static DemoView? RunDialog(DemoViewForm form)
    {
        using (form) { form.ShowDialog(); return form.NextView; }
    }

    /// <summary>Console view: the real Console UI. First visit starts at the sign-up screen; later visits go straight in.</summary>
    private DemoView? RunConsole()
    {
        var setup = _client.GetSetupAsync().GetAwaiter().GetResult();
        if (setup.NeedsOwner)
        {
            using var login = new LoginForm(_client, setup);
            if (login.ShowDialog() != DialogResult.OK) return null; // cancelled → back to the picker
        }

        DemoView? next = null;
        var options = new ConsoleOptions
        {
            AllowFileExport = false,
            Banner = DemoProgram.Banner,
            ShowTour = false,
            ExtraViewMenu =
            [
                ("서버 PC 화면 보기", owner => { next = DemoView.Server; (owner as Form)?.Close(); }),
                ("클라이언트(교실 PC) 화면 보기", owner => { next = DemoView.Client; (owner as Form)?.Close(); }),
                ("체험 처음 화면으로", owner => { next = null; (owner as Form)?.Close(); }),
                ("선생님용 트레이 미리 보기…", owner => { using var f = new TrayPreviewForm(); f.ShowDialog(owner); }),
                ("둘러보기 다시 보기", owner => { if (owner is MainForm m) GuidedTour.Show(m); }),
            ],
        };
        using var main = new MainForm(_client, _client.Owner, options);
        main.ShowDialog();
        return next;
    }
}

/// <summary>Base for the server/client demo views: a persistent banner and a "화면 전환" menu to jump between views.</summary>
public abstract class DemoViewForm : Form
{
    /// <summary>Set when the user picks another view from the switch menu; null means "back to the picker".</summary>
    public DemoView? NextView { get; private set; }

    /// <summary>Call last, after the view's own content is added, so the banner and menu sit on top.</summary>
    protected void InstallChrome(DemoView current)
    {
        var menu = new MenuStrip { Dock = DockStyle.Top };
        var sw = new ToolStripMenuItem("화면 전환");
        void Add(DemoView v, string label)
        {
            if (v == current) return;
            sw.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => { NextView = v; Close(); }));
        }
        Add(DemoView.Console, "관리자 화면 (콘솔)");
        Add(DemoView.Server, "서버 PC 화면");
        Add(DemoView.Client, "클라이언트(교실 PC) 화면");
        sw.DropDownItems.Add(new ToolStripSeparator());
        sw.DropDownItems.Add(new ToolStripMenuItem("체험 처음 화면으로", null, (_, _) => { NextView = null; Close(); }));
        menu.Items.Add(sw);
        MainMenuStrip = menu;

        var banner = new Label { Text = DemoProgram.Banner, Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.FromArgb(255, 236, 153), Font = Ui.Bold };
        Controls.Add(banner);
        Controls.Add(menu);
    }
}
