namespace Gac.Tray.UI;

/// <summary>
/// Full-screen PC lock window shown during a period (CHANGE-001 2.4). Plain, centered, always on top, no close/minimize,
/// Alt+F4 ignored. The second line counts down live; at zero the PC shuts down. "[관리자입니다.]" opens a password box
/// whose credentials are checked by the server (through the tray → service). The text is fixed, exactly as specified.
/// </summary>
public sealed class LockForm : Form
{
    private readonly string _periodName;
    private readonly Label _countdown = new() { AutoSize = true, Font = new Font("맑은 고딕", 16f, FontStyle.Bold), ForeColor = Color.White, BackColor = Color.Transparent };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private DateTimeOffset _deadline;

    /// <summary>Asks the server (via the tray → service) to unlock. Returns (ok, message).</summary>
    public Func<string, string, Task<(bool Ok, string Message)>>? Unlock { get; set; }
    public event EventHandler? ExitRequested;
    public event EventHandler? Expired;

    public LockForm(string periodName, int countdownSeconds)
    {
        _periodName = periodName;
        _deadline = DateTimeOffset.Now.AddSeconds(countdownSeconds);
        Text = "GAC";
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        TopMost = true;
        ControlBox = false;
        ShowInTaskbar = false;
        KeyPreview = true;
        BackColor = Color.FromArgb(20, 24, 33);
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen?.Bounds ?? Bounds;

        var line1 = new Label
        {
            Text = $"지금은 \"{_periodName}\" 이기에 사용이 불가합니다.",
            AutoSize = true, Font = new Font("맑은 고딕", 20f, FontStyle.Bold), ForeColor = Color.White, BackColor = Color.Transparent,
        };
        var admin = Ui.Button("관리자입니다.", async (_, _) => await AdminAsync(), 140);
        var exit = Ui.Button("종료하기", (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty), 120);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, BackColor = Color.Transparent, Anchor = AnchorStyles.None };
        buttons.Controls.AddRange([admin, exit]);

        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Anchor = AnchorStyles.None, BackColor = Color.Transparent, WrapContents = false };
        stack.Controls.AddRange([line1, _countdown, new Label { Height = 24, AutoSize = false, Width = 1 }, buttons]);
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, BackColor = Color.Transparent };
        center.Controls.Add(stack, 0, 0);
        stack.Anchor = AnchorStyles.None;
        Controls.Add(center);

        KeyDown += (_, e) => { if (e.Alt && e.KeyCode == Keys.F4) e.Handled = true; };
        FormClosing += (_, e) => { if (!_forceClosing && e.CloseReason == CloseReason.UserClosing) e.Cancel = true; }; // ignore Alt+F4 / close box
        _timer.Tick += (_, _) => Tick();
        Load += (_, _) => Tick();
        _timer.Start();
        Shown += (_, _) => { Activate(); BringToFront(); };
    }

    private void Tick()
    {
        var left = _deadline - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero)
        {
            _countdown.Text = "0초 후 자동 종료됩니다.";
            _timer.Stop();
            Expired?.Invoke(this, EventArgs.Empty);
            return;
        }
        var m = (int)left.TotalMinutes;
        var s = left.Seconds;
        _countdown.Text = m > 0 ? $"{m}분 {s}초 후 자동 종료됩니다." : $"{s}초 후 자동 종료됩니다.";
    }

    private async Task AdminAsync()
    {
        using var dlg = new UnlockDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (Unlock == null) return;
        try
        {
            var (ok, message) = await Unlock(dlg.UserName, dlg.Password);
            if (ok) { _timer.Stop(); ForceClose(); }
            else Ui.Error(this, message);
        }
        catch (Exception e) { Ui.Error(this, "잠금 해제 중 오류가 발생했습니다: " + e.Message); }
    }

    /// <summary>Closes the lock window programmatically (unlock succeeded or the period ended).</summary>
    public void ForceClose()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(ForceClose); return; }
        _timer.Stop();
        _forceClosing = true; // Form.Close() reports CloseReason.UserClosing, so the Alt+F4 guard must be bypassed explicitly
        Hide();
        Close();
        Dispose();
    }

    private bool _forceClosing;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    private sealed class UnlockDialog : Form
    {
        private readonly TextBox _user = new() { Width = 220 };
        private readonly TextBox _pw = new() { Width = 220, UseSystemPasswordChar = true };
        public string UserName => _user.Text.Trim();
        public string Password => _pw.Text;

        public UnlockDialog()
        {
            Ui.Apply(this, "관리자 확인", new Size(380, 190), resizable: false);
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            var grid = Ui.Grid();
            Ui.AddRow(grid, "관리자 아이디", _user);
            Ui.AddRow(grid, "비밀번호", _pw);
            var ok = Ui.Button("확인", (_, _) =>
            {
                if (UserName.Length == 0 || Password.Length == 0) { Ui.Error(this, "아이디와 비밀번호를 입력하세요."); return; }
                DialogResult = DialogResult.OK;
            }, 80);
            var cancel = Ui.Button("취소", (_, _) => DialogResult = DialogResult.Cancel, 80);
            Controls.Add(grid);
            Controls.Add(Ui.ButtonBar(cancel, ok));
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
