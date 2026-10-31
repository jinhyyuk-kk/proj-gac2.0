using Gac.Shared.Contracts;

namespace Gac.Tray.UI;

/// <summary>Runs network self-diagnosis. Real implementation in the tray; simulated in the demo.</summary>
public interface INetDiagHost
{
    Task<NetDiagReport> RunAsync(Func<string, bool> confirmReboot, IProgress<string> progress, CancellationToken ct);
    /// <summary>Sends a failing report to the console. Returns a Korean status message.</summary>
    Task<string> SubmitAsync(NetDiagReport report, CancellationToken ct);
}

/// <summary>Network self-diagnosis window (CLAUDE.md 4.4). Plain Korean results.</summary>
public sealed class NetDiagForm : Form
{
    private readonly INetDiagHost _host;
    private readonly Label _status = Ui.Label("‘진단 시작’을 누르면 네트워크를 차례로 확인합니다.", Ui.Big);
    private readonly ListView _checks = Ui.List(("항목", 140), ("결과", 70), ("설명", 420));
    private readonly Button _start;
    private CancellationTokenSource? _cts;

    public NetDiagForm(INetDiagHost host)
    {
        _host = host;
        Ui.Apply(this, "네트워크 자가 진단", new Size(680, 420));
        StartPosition = FormStartPosition.CenterScreen;
        _start = Ui.Button("진단 시작", async (_, _) => await RunAsync(), 120);
        var close = Ui.Button("닫기", (_, _) => Close(), 80);
        var top = new Panel { Dock = DockStyle.Top, Height = 56, Padding = new Padding(12, 12, 12, 0) };
        _status.MaximumSize = new Size(640, 0);
        top.Controls.Add(_status);
        Controls.Add(_checks);
        Controls.Add(top);
        Controls.Add(Ui.ButtonBar(close, _start));
        CancelButton = close;
        FormClosed += (_, _) => _cts?.Cancel();
    }

    private async Task RunAsync()
    {
        _start.Enabled = false;
        _checks.Items.Clear();
        _status.ForeColor = SystemColors.ControlText;
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<string>(s => _status.Text = s);
            var report = await _host.RunAsync(msg => Ui.Confirm(this, msg), progress, _cts.Token);
            foreach (var c in report.Checks)
            {
                var (text, color) = c.Status switch
                {
                    NetCheckStatus.Ok => ("✔ 정상", Ui.Ok),
                    NetCheckStatus.Repaired => ("✔ 고침", Ui.Ok),
                    NetCheckStatus.Warning => ("⚠ 주의", Ui.Warn),
                    NetCheckStatus.Failed => ("✖ 문제", Ui.Bad),
                    _ => ("– 건너뜀", Ui.Muted),
                };
                _checks.Items.Add(new ListViewItem([c.Name, text, c.Message]) { ForeColor = color, UseItemStyleForSubItems = true });
            }
            _status.Text = report.Summary;
            _status.ForeColor = report.Healthy ? Ui.Ok : Ui.Bad;
            if (!report.Healthy)
            {
                var sent = await _host.SubmitAsync(report, _cts.Token);
                _status.Text = report.Summary.Contains("관리자에게") ? report.Summary : report.Summary + " " + sent;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _status.Text = "진단 중 오류가 발생했습니다: " + e.Message; _status.ForeColor = Ui.Bad; }
        finally { if (!IsDisposed) _start.Enabled = true; }
    }
}

/// <summary>T−5 minute auto-shutdown warning with "30분 연장" and "지금 종료" (CLAUDE.md 4.2).</summary>
public sealed class ShutdownWarningForm : Form
{
    private readonly DateTimeOffset _deadline;
    private readonly Label _countdown = Ui.Label("", new Font("맑은 고딕", 28f, FontStyle.Bold), Ui.Bad);
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };

    public event EventHandler? ExtendClicked;
    public event EventHandler? ShutdownNowClicked;

    public ShutdownWarningForm(DateTimeOffset deadline, int extensionsLeft, int extensionMinutes)
    {
        _deadline = deadline;
        Ui.Apply(this, "GAC 자동 종료 안내", new Size(440, 250), resizable: false);
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ControlBox = false;
        ShowInTaskbar = true;
        var msg = Ui.Label($"이 PC는 {deadline.ToLocalTime():HH:mm}에 자동으로 종료됩니다.\n작업 중인 파일을 저장하세요.", Ui.Big);
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(20, 16, 20, 0), WrapContents = false };
        panel.Controls.Add(msg);
        panel.Controls.Add(_countdown);
        var extend = Ui.Button($"{extensionMinutes}분 연장", (_, _) => ExtendClicked?.Invoke(this, EventArgs.Empty), 120);
        extend.Enabled = extensionsLeft > 0;
        if (extensionsLeft <= 0) panel.Controls.Add(Ui.Label("더 이상 연장할 수 없습니다.", color: Ui.Muted));
        else panel.Controls.Add(Ui.Label($"남은 연장 횟수: {extensionsLeft}회", color: Ui.Muted));
        var now = Ui.Button("지금 종료", (_, _) => ShutdownNowClicked?.Invoke(this, EventArgs.Empty), 120);
        Controls.Add(panel);
        Controls.Add(Ui.ButtonBar(now, extend));
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
        FormClosed += (_, _) => _timer.Dispose();
    }

    private void Tick()
    {
        var left = _deadline - DateTimeOffset.Now;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        _countdown.Text = $"{(int)left.TotalMinutes:00}:{left.Seconds:00}";
    }
}

/// <summary>
/// Monitor / interactive whiteboard LED test (old GAC LITE): full-screen solid colors advanced by click or key,
/// then the owner records pass/fail.
/// </summary>
public sealed class LedTestForm : Form
{
    private static readonly (Color Color, string Name)[] Colors =
        [(Color.Red, "빨강"), (Color.Lime, "초록"), (Color.Blue, "파랑"), (Color.White, "흰색"), (Color.Black, "검정")];
    private int _index = -1;
    private readonly Label _hint = new() { AutoSize = true, Font = new Font("맑은 고딕", 12f), BackColor = Color.Transparent };

    public bool? Passed { get; private set; }
    public string? Note { get; private set; }

    public LedTestForm()
    {
        Text = "LED 점검";
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        TopMost = true;
        KeyPreview = true;
        Cursor = Cursors.Hand;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen?.Bounds ?? Bounds;
        _hint.Location = new Point(24, 24);
        Controls.Add(_hint);
        MouseClick += (_, _) => Next();
        _hint.MouseClick += (_, _) => Next();
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { Passed = null; Close(); }
            else Next();
        };
        Load += (_, _) => Next();
    }

    private void Next()
    {
        _index++;
        if (_index >= Colors.Length)
        {
            Hide();
            using var ask = new LedResultDialog();
            ask.ShowDialog();
            Passed = ask.Passed;
            Note = ask.Note;
            Close();
            return;
        }
        var (c, name) = Colors[_index];
        BackColor = c;
        _hint.ForeColor = c.GetBrightness() > 0.5f ? Color.Black : Color.White;
        _hint.Text = $"{name} ({_index + 1}/{Colors.Length}) · 화면을 클릭하거나 아무 키나 누르면 다음 색 · Esc 취소";
    }

    private sealed class LedResultDialog : Form
    {
        private readonly TextBox _note = new() { Width = 320, PlaceholderText = "이상이 있으면 내용을 적으세요 (선택)" };
        public bool? Passed { get; private set; }
        public string? Note => string.IsNullOrWhiteSpace(_note.Text) ? null : _note.Text.Trim();

        public LedResultDialog()
        {
            Ui.Apply(this, "LED 점검 결과", new Size(420, 170), resizable: false);
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            var grid = Ui.Grid();
            grid.Controls.Add(Ui.Label("모든 색이 고르게 잘 보였나요? (얼룩, 죽은 화소, 줄 없음)"), 0, 0);
            grid.SetColumnSpan(grid.Controls[0], 2);
            grid.RowCount = 1;
            Ui.AddRow(grid, "메모", _note);
            Controls.Add(grid);
            Controls.Add(Ui.ButtonBar(
                Ui.Button("이상 있음", (_, _) => { Passed = false; Close(); }, 100),
                Ui.Button("이상 없음", (_, _) => { Passed = true; Close(); }, 100)));
        }
    }
}
