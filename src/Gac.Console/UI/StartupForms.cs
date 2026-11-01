using Gac.Console.Services;
using Gac.Shared.Contracts;
using Gac.Tray.UI;

namespace Gac.Console.UI;

/// <summary>First connection to a GAC Server: address + enrollment code shown on the Admin PC.</summary>
public sealed class ConnectForm : Form
{
    private readonly TextBox _address = new() { Width = 260, PlaceholderText = "예: ADMIN-PC 또는 192.168.0.10:8443" };
    private readonly TextBox _code = new() { Width = 260, PlaceholderText = "예: K7QM-2XPA-9RTD", CharacterCasing = CharacterCasing.Upper };
    public ConsoleConnection? Result { get; private set; }

    public ConnectForm()
    {
        Ui.Apply(this, "GAC 2.0 – 서버 연결", new Size(480, 250), resizable: false);
        StartPosition = FormStartPosition.CenterScreen;
        var intro = Ui.Label("관리 PC(GAC 서버)의 주소와 설치 마지막 화면에 표시된 등록 코드를 입력하세요.\n등록 코드는 관리 PC의 C:\\ProgramData\\GAC\\Server\\enrollment.txt 에도 있습니다.");
        intro.MaximumSize = new Size(440, 0);
        intro.Dock = DockStyle.Top;
        intro.Padding = new Padding(10, 10, 10, 0);
        var grid = Ui.Grid();
        Ui.AddRow(grid, "서버 주소", _address);
        Ui.AddRow(grid, "등록 코드", _code);
        var ok = Ui.Button("연결", async (_, _) => await ConnectAsync(), 90);
        var cancel = Ui.Button("취소", (_, _) => DialogResult = DialogResult.Cancel, 80);
        Controls.Add(grid);
        Controls.Add(intro);
        Controls.Add(Ui.ButtonBar(cancel, ok));
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(_address.Text) || string.IsNullOrWhiteSpace(_code.Text)) { Ui.Error(this, "서버 주소와 등록 코드를 모두 입력하세요."); return; }
        UseWaitCursor = true;
        Enabled = false;
        try
        {
            Result = await ConsoleConnection.EstablishAsync(_address.Text, _code.Text, CancellationToken.None);
            Result.Save();
            DialogResult = DialogResult.OK;
        }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
        finally { UseWaitCursor = false; Enabled = true; }
    }
}

/// <summary>Login, or the "create Owner account" screen on first run (no default password ever ships).</summary>
public sealed class LoginForm : Form
{
    private readonly IGacServerClient _client;
    private readonly bool _createOwner;
    private readonly TextBox _user = new() { Width = 240 };
    private readonly TextBox _display = new() { Width = 240 };
    private readonly TextBox _pw = new() { Width = 240, UseSystemPasswordChar = true };
    private readonly TextBox _pw2 = new() { Width = 240, UseSystemPasswordChar = true };
    private readonly TextBox _code = new() { Width = 240, CharacterCasing = CharacterCasing.Upper };
    public LoginResponse? Result { get; private set; }
    public bool ChangeServerRequested { get; private set; }

    public LoginForm(IGacServerClient client, SetupStatus setup)
    {
        _client = client;
        _createOwner = setup.NeedsOwner;
        Ui.Apply(this, _createOwner ? "GAC 2.0 – 회원가입 (최고 관리자 등록)" : "GAC 2.0 – 로그인", new Size(440, _createOwner ? 330 : 220), resizable: false);
        StartPosition = FormStartPosition.CenterScreen;
        var head = Ui.Label(_createOwner
            ? "처음 실행입니다. 회원가입으로 최고 관리자 계정을 만드세요. 서버 설치 화면에 표시된 ‘설치 코드’가 필요합니다.\n첫 회원가입만 최고 관리자가 되고, 이후 계정은 계정 관리에서 추가합니다. (비밀번호: 8자 이상, 문자와 숫자 포함)"
            : $"서버: {client.ServerDisplayName}");
        head.Dock = DockStyle.Top;
        head.Padding = new Padding(10, 10, 10, 0);
        head.MaximumSize = new Size(420, 0);
        var grid = Ui.Grid();
        Ui.AddRow(grid, "아이디", _user);
        if (_createOwner) Ui.AddRow(grid, "이름", _display);
        Ui.AddRow(grid, "비밀번호", _pw);
        if (_createOwner) { Ui.AddRow(grid, "비밀번호 확인", _pw2); Ui.AddRow(grid, "설치 코드", _code); }
        var ok = Ui.Button(_createOwner ? "회원가입" : "로그인", async (_, _) => await SubmitAsync(), 100);
        var cancel = Ui.Button("끝내기", (_, _) => DialogResult = DialogResult.Cancel, 80);
        var bar = Ui.ButtonBar(cancel, ok);
        if (!client.IsDemo) bar.Controls.Add(Ui.Button("다른 서버…", (_, _) => { ChangeServerRequested = true; DialogResult = DialogResult.Retry; }, 90));
        Controls.Add(grid);
        Controls.Add(head);
        Controls.Add(bar);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private async Task SubmitAsync()
    {
        if (_createOwner && _pw.Text != _pw2.Text) { Ui.Error(this, "비밀번호 확인이 일치하지 않습니다."); return; }
        UseWaitCursor = true;
        Enabled = false;
        try
        {
            if (_createOwner)
                await _client.CreateOwnerAsync(new CreateOwnerRequest(_user.Text.Trim(), _display.Text.Trim(), _pw.Text, _code.Text.Trim()));
            Result = await _client.LoginAsync(_user.Text.Trim(), _pw.Text);
            DialogResult = DialogResult.OK;
        }
        catch (GacClientException e) { Ui.Error(this, e.Message); _pw.SelectAll(); _pw.Focus(); }
        finally { UseWaitCursor = false; Enabled = true; }
    }
}
