using Gac.Server.Hosting;
using Gac.Tray.UI;

namespace Gac.ServerStatus;

/// <summary>GAC 서버 상태 — local admin window (CHANGE-001 §6). Talks only to the local service/DB; not a second console.</summary>
public sealed class StatusForm : Form
{
    private readonly ServerInfo _info = new();
    private readonly Label _state = Ui.Label("", Ui.Big);
    private readonly Label _summary = Ui.Label("");
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f), BackColor = System.Drawing.Color.White };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    private ServerInfo.Snapshot? _snap;

    public StatusForm()
    {
        Ui.Apply(this, "GAC 서버 상태", new System.Drawing.Size(640, 560));
        StartPosition = FormStartPosition.CenterScreen;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12, 10, 12, 6), WrapContents = false };
        _summary.MaximumSize = new System.Drawing.Size(600, 0);
        top.Controls.Add(_state);
        top.Controls.Add(_summary);

        var svc = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 0, 0, 0) };
        svc.Controls.AddRange([
            Ui.Button("시작", (_, _) => Svc("start"), 70), Ui.Button("중지", (_, _) => Svc("stop"), 70), Ui.Button("다시 시작", (_, _) => Svc("restart"), 90),
            Ui.Button("대역 다시 감지", (_, _) => ApplyRange(null), 120), Ui.Button("대역 추가…", (_, _) => AddRange(), 100),
            Ui.Button("클라이언트 설치 파일 만들기", (_, _) => ExportRollout(), 190),
        ]);

        var logBox = new GroupBox { Text = "최근 서버 로그", Dock = DockStyle.Fill, Padding = new Padding(8) };
        logBox.Controls.Add(_log);

        Controls.Add(logBox);
        Controls.Add(svc);
        Controls.Add(top);
        Controls.Add(Ui.ButtonBar(Ui.Button("닫기", (_, _) => Close(), 80), Ui.Button("새로 고침", (_, _) => Refresh2(), 90)));

        Load += (_, _) => Refresh2();
        _timer.Tick += (_, _) => Refresh2();
        _timer.Start();
        FormClosed += (_, _) => _timer.Dispose();
    }

    private void Svc(string verb) { if (verb == "restart") { ServerInfo.Service("stop"); Thread.Sleep(1500); ServerInfo.Service("start"); } else ServerInfo.Service(verb); Thread.Sleep(1000); Refresh2(); }

    private void ApplyRange(string? range)
    {
        var result = _info.RunServerVerb("apply-firewall" + (range != null ? $" --range {range}" : ""));
        Ui.Info(this, "방화벽 대역을 적용했습니다: " + (string.IsNullOrWhiteSpace(result) ? "(대역 감지 실패)" : result));
        Refresh2();
    }

    private void AddRange()
    {
        using var f = new Form();
        Ui.Apply(f, "대역 추가", new System.Drawing.Size(360, 130), resizable: false);
        var t = new TextBox { Left = 16, Top = 16, Width = 300, PlaceholderText = "예: 10.20.0.0/22" };
        f.Controls.Add(t);
        f.Controls.Add(Ui.ButtonBar(Ui.Button("취소", (_, _) => f.DialogResult = DialogResult.Cancel, 70), Ui.Button("추가", (_, _) => f.DialogResult = DialogResult.OK, 70)));
        if (f.ShowDialog(this) != DialogResult.OK) return;
        if (!LanDetection.IsValidCidr(t.Text)) { Ui.Error(this, "대역 형식이 올바르지 않습니다 (예: 10.20.0.0/22)."); return; }
        ApplyRange(t.Text.Trim());
    }

    private void ExportRollout()
    {
        if (_snap == null) return;
        using var dlg = new FolderBrowserDialog { Description = "클라이언트 설치 파일을 저장할 폴더 선택" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var target = Path.Combine(dlg.SelectedPath, "GAC-Agent-Rollout");
            RolloutPackage.WriteConfig(target, _info.RolloutConfig(_snap));
            Ui.Info(this, "클라이언트 설치 파일을 만들었습니다:\n" + target + "\n\n이 폴더의 GAC-Agent-Setup.exe 를 교실 PC에서 실행하면 자동으로 등록됩니다.");
        }
        catch (Exception e) { Ui.Error(this, "만들지 못했습니다: " + e.Message); }
    }

    private void Refresh2()
    {
        _snap = _info.Read();
        _state.Text = _snap.ServiceRunning ? "● 서비스 실행 중" : "○ 서비스 중지됨";
        _state.ForeColor = _snap.ServiceRunning ? Ui.Ok : Ui.Bad;
        var ranges = _snap.Ranges.Length == 0 ? "(미설정 — 대역 다시 감지 필요)" : string.Join(", ", _snap.Ranges);
        var setup = string.IsNullOrEmpty(_snap.SetupCode) ? "(회원가입 완료됨)" : _snap.SetupCode;
        _summary.Text =
            $"주소: {_snap.ServerName}:{_snap.Port}   ·   교내망 대역(방화벽): {ranges}\n" +
            $"연결된 PC: {_snap.OnlineCount}/{_snap.PcCount}대 켜짐   ·   차단된 기기: {_snap.BlockedCount}   ·   확인 필요: {_snap.PendingCount}\n" +
            $"마지막 등록: {(_snap.LastEnrollment is { } t ? t.ToString("yyyy-MM-dd HH:mm") : "없음")}   ·   설치 코드: {setup}\n" +
            $"인증서 지문: {_snap.Thumbprint}";
        var newText = string.Join(Environment.NewLine, _snap.RecentLog);
        if (_log.Text != newText) { _log.Text = newText; _log.SelectionStart = _log.TextLength; _log.ScrollToCaret(); }
    }
}
