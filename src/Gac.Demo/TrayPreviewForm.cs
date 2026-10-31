using Gac.Shared.ClassMode;
using Gac.Shared.Contracts;
using Gac.Tray.UI;

namespace Gac.Demo;

/// <summary>Preview of the teacher tray app in a window: preset editor, network self-diagnosis, shutdown popup. All simulated.</summary>
public sealed class TrayPreviewForm : Form
{
    private PresetFile _presets = new()
    {
        Presets =
        [
            new ClassPreset { Name = "1교시 수업 시작", Actions = [new() { Type = PresetActionTypes.Unmute }, new() { Type = PresetActionTypes.SetVolume, Value = 40 },
                new() { Type = PresetActionTypes.DisplayMode, Mode = "clone" }, new() { Type = PresetActionTypes.OpenUrl, Url = "https://www.ebs.co.kr" }] },
            new ClassPreset { Name = "시험 시간", Actions = [new() { Type = PresetActionTypes.Mute }, new() { Type = PresetActionTypes.CloseApp, ProcessName = "chrome" }] },
        ],
    };
    private readonly ListBox _menu = new() { Dock = DockStyle.Fill, IntegralHeight = false, Font = Ui.Big };
    private readonly Label _log = Ui.Label("", color: Ui.Muted);

    public TrayPreviewForm()
    {
        Ui.Apply(this, "선생님용 트레이 미리 보기 (체험)", new Size(520, 430));
        var intro = Ui.Label("실제 제품에서는 화면 오른쪽 아래 알림 영역의 GAC 아이콘을 누르면 이 메뉴가 나옵니다.\n선생님은 관리자 권한 없이 자신의 프리셋만 만들고 실행할 수 있습니다.");
        intro.MaximumSize = new Size(490, 0);
        intro.Dock = DockStyle.Top;
        intro.Padding = new Padding(10);
        _log.Dock = DockStyle.Bottom;
        _log.Padding = new Padding(10);
        _log.MaximumSize = new Size(490, 0);
        FillMenu();
        _menu.DoubleClick += async (_, _) => await OpenAsync();
        var open = Ui.Button("열기", async (_, _) => await OpenAsync(), 80);
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 0) };
        panel.Controls.Add(_menu);
        Controls.Add(panel);
        Controls.Add(intro);
        Controls.Add(_log);
        Controls.Add(Ui.ButtonBar(Ui.Button("닫기", (_, _) => Close(), 80), open));
    }

    private void FillMenu()
    {
        _menu.Items.Clear();
        foreach (var p in DemoSchool.SharedPresets().Presets) _menu.Items.Add("▶ " + p.Name + "  (관리자 배포)");
        foreach (var p in _presets.Presets) _menu.Items.Add("▶ " + p.Name);
        _menu.Items.Add("✎ 수업 모드 프리셋 편집…");
        _menu.Items.Add("🌐 네트워크 자가 진단…");
        _menu.Items.Add("⏻ 자동 종료 경고 창 미리 보기");
    }

    private async Task OpenAsync()
    {
        var text = _menu.SelectedItem as string;
        if (text == null) return;
        if (text.StartsWith('▶'))
        {
            var all = DemoSchool.SharedPresets().Presets.Select(p => { p.Shared = true; return p; }).Concat(_presets.Presets).ToList();
            await RunPresetAsync(all[_menu.SelectedIndex]);
        }
        else if (text.StartsWith('✎'))
        {
            using var editor = new PresetEditorForm(_presets, allowRunCommand: false, f => { _presets = f; FillMenu(); return Task.FromResult<string?>(null); },
                RunPresetAsync, "수업 모드 프리셋 편집 (체험)");
            editor.ShowDialog(this);
        }
        else if (text.StartsWith("🌐", StringComparison.Ordinal))
        {
            using var f = new NetDiagForm(new DemoNetDiagHost());
            f.ShowDialog(this);
        }
        else
        {
            using var w = new ShutdownWarningForm(DateTimeOffset.Now.AddMinutes(5), 2, 30);
            w.ExtendClicked += (_, _) => { Ui.Info(w, "(체험) 종료가 30분 연장되었습니다."); w.Close(); };
            w.ShutdownNowClicked += (_, _) => { Ui.Info(w, "(체험) 실제 제품에서는 PC가 지금 종료됩니다. 체험 모드에서는 아무 일도 일어나지 않습니다."); w.Close(); };
            w.ShowDialog(this);
        }
    }

    /// <summary>Shows what each action would do. Nothing is changed on this PC.</summary>
    private async Task RunPresetAsync(ClassPreset p)
    {
        var lines = new List<string>();
        foreach (var a in p.Actions)
        {
            _log.Text = $"(체험) 실행 중: {a.Describe()}";
            await Task.Delay(a.Type == PresetActionTypes.Wait ? Math.Min(3, a.Seconds ?? 1) * 300 : 350);
            lines.Add("✔ " + a.Describe());
        }
        _log.Text = $"(체험) '{p.Name}' 실행 완료 – 실제 PC 설정은 바뀌지 않았습니다.";
        Ui.Info(this, $"'{p.Name}' 프리셋을 실행한 것처럼 보여 드렸습니다:\n\n{string.Join("\n", lines)}\n\n(체험 모드에서는 볼륨, 화면, 프로그램이 실제로 바뀌지 않습니다.)");
    }
}

/// <summary>Simulates a PC whose DNS setting was broken and gets fixed by the automatic repair.</summary>
public sealed class DemoNetDiagHost : INetDiagHost
{
    public async Task<NetDiagReport> RunAsync(Func<string, bool> confirmReboot, IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("네트워크 상태를 확인하는 중…");
        await Task.Delay(900, ct);
        progress.Report("DNS 캐시를 비우는 중…");
        await Task.Delay(700, ct);
        progress.Report("DNS 설정을 자동으로 되돌리는 중…");
        await Task.Delay(900, ct);
        var checks = new List<NetCheck>
        {
            new("네트워크 어댑터", NetCheckStatus.Ok, "연결됨 (이더넷)"),
            new("IP 주소", NetCheckStatus.Ok, "10.10.2.11 (자동)"),
            new("게이트웨이", NetCheckStatus.Ok, "10.10.0.1 응답함"),
            new("DNS", NetCheckStatus.Repaired, "잘못된 DNS 서버(8.8.4.4)를 자동 설정으로 되돌려 'ADMIN-PC' 이름 확인 성공"),
            new("GAC 서버", NetCheckStatus.Ok, "ADMIN-PC:8443 연결됨"),
            new("프록시 설정", NetCheckStatus.Ok, "프록시 사용 안 함"),
        };
        return new NetDiagReport(checks, ["DNS 캐시 비우기", "DNS 서버 설정을 자동(DHCP)으로 되돌리기"], true, "DNS 문제를 고쳤습니다. (체험 모드 – 실제 설정은 바뀌지 않았습니다)");
    }

    public Task<string> SubmitAsync(NetDiagReport report, CancellationToken ct) => Task.FromResult("(체험) 진단 결과를 관리자에게 보낸 것처럼 표시합니다.");
}
