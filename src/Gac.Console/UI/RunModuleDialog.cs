using Gac.Console.Services;
using Gac.Shared;
using Gac.Shared.Auth;
using Gac.Shared.Contracts;
using Gac.Tray.UI;

namespace Gac.Console.UI;

/// <summary>Sends a module action to the selected PCs. Options are built per module; destructive actions ask first.</summary>
public sealed class RunModuleDialog : Form
{
    private readonly IGacServerClient _client;
    private readonly IReadOnlyList<PcSummary> _targets;
    private readonly AccountInfo _me;
    private readonly ListBox _modules = new() { Dock = DockStyle.Left, Width = 170, IntegralHeight = false };
    private readonly Panel _options = new() { Dock = DockStyle.Fill, Padding = new Padding(12), AutoScroll = true };
    private readonly string[] _moduleOrder = [ModuleIds.Clean, ModuleIds.Shutdown, ModuleIds.Errors, ModuleIds.Update, ModuleIds.Deploy, ModuleIds.Health, ModuleIds.Inspect, ModuleIds.NetDiag, ModuleIds.ClassMode];
    private Func<(string Action, string Payload, string? Confirm)?>? _build;
    public SendCommandResponse? Result { get; private set; }

    public RunModuleDialog(IGacServerClient client, IReadOnlyList<PcSummary> targets, AccountInfo me, string? module = null)
    {
        _client = client;
        _targets = targets;
        _me = me;
        Ui.Apply(this, $"작업 실행 – 선택한 PC {targets.Count}대", new Size(700, 480));
        foreach (var m in _moduleOrder) _modules.Items.Add(ModuleIds.KoreanName(m));
        var send = Ui.Button("보내기", async (_, _) => await SendAsync(), 100);
        var cancel = Ui.Button("취소", (_, _) => DialogResult = DialogResult.Cancel, 80);
        var head = Ui.Label("대상: " + string.Join(", ", targets.Take(8).Select(t => t.Name)) + (targets.Count > 8 ? $" 외 {targets.Count - 8}대" : ""), color: Ui.Muted);
        head.Dock = DockStyle.Top;
        head.Padding = new Padding(8);
        Controls.Add(_options);
        Controls.Add(_modules);
        Controls.Add(head);
        Controls.Add(Ui.ButtonBar(cancel, send));
        CancelButton = cancel;
        _modules.SelectedIndexChanged += async (_, _) => await BuildOptionsAsync();
        _modules.SelectedIndex = Math.Max(0, module == null ? 0 : Array.IndexOf(_moduleOrder, module));
    }

    private bool Can(Permission p) => RolePermissions.Has(_me.Role, p);
    private string Module => _moduleOrder[_modules.SelectedIndex];

    private static RadioButton Radio(string text, bool check = false) => new() { Text = text, AutoSize = true, Checked = check, Margin = new Padding(3, 6, 3, 3) };

    private FlowLayoutPanel Stack(params Control[] c)
    {
        var p = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        p.Controls.AddRange(c);
        return p;
    }

    private async Task BuildOptionsAsync()
    {
        _options.Controls.Clear();
        _build = null;
        var n = _targets.Count;
        switch (Module)
        {
            case ModuleIds.Clean:
            {
                var policy = Radio("그룹 정책 설정을 따름 (처음에는 미리 보기)", true);
                var dry = Radio("미리 보기 (삭제하지 않고 목록만 확인)");
                var real = Radio("실제로 정리 (임시 파일, 캐시, 휴지통, 오래된 다운로드 삭제)");
                _options.Controls.Add(Stack(Ui.Label("자동 정리", Ui.Bold), policy, dry, real,
                    Ui.Label("정해진 폴더만 정리합니다. 사용 중인 파일과 실행 중인 브라우저의 캐시는 건너뜁니다.", color: Ui.Muted)));
                _build = () => (ModuleActions.CleanRun, GacJson.Serialize(new CleanRequest(policy.Checked ? null : dry.Checked)),
                    real.Checked ? $"PC {n}대에서 파일을 실제로 삭제합니다. 계속할까요?" : null);
                break;
            }
            case ModuleIds.Shutdown:
            {
                var now = Radio("경고 후 종료", true);
                var delay = new NumericUpDown { Minimum = 0, Maximum = 120, Value = 5, Width = 70 };
                var cancel = Radio("예약된 종료 취소");
                _options.Controls.Add(Stack(Ui.Label("자동 종료", Ui.Bold), Ui.Row(now, delay, Ui.Label("분 뒤 (로그인한 사용자에게 경고와 연장 버튼 표시)")), cancel,
                    Ui.Label("정기 종료 시각은 [관리 → 그룹별 정책]에서 설정합니다.", color: Ui.Muted)));
                _build = () => now.Checked
                    ? (ModuleActions.ShutdownNow, GacJson.Serialize(new ShutdownNowRequest((int)delay.Value)), $"PC {n}대를 {(int)delay.Value}분 뒤 종료합니다. 계속할까요?")
                    : (ModuleActions.ShutdownCancel, "{}", null);
                break;
            }
            case ModuleIds.Errors:
                _options.Controls.Add(Stack(Ui.Label("오류 점검", Ui.Bold), Ui.Label("시스템·응용 프로그램 로그의 오류/위험 이벤트와 멈춘 자동 시작 서비스를 지금 수집합니다.")));
                _build = () => (ModuleActions.ErrorsCollect, "{}", null);
                break;
            case ModuleIds.Update:
            {
                var scan = Radio("업데이트 확인만", true);
                var install = Radio("검색 후 설치");
                _options.Controls.Add(Stack(Ui.Label("일괄 업데이트", Ui.Bold), scan, install,
                    Ui.Label("각 PC에 이미 설정된 업데이트 원본(WSUS 또는 Microsoft Update)을 사용합니다.\nGAC는 자동으로 재부팅하지 않습니다.", color: Ui.Muted)));
                _build = () => scan.Checked ? (ModuleActions.UpdateScan, "{}", null) : (ModuleActions.UpdateInstall, "{}", $"PC {n}대에 업데이트를 설치합니다. 수업 중이라면 PC가 느려질 수 있습니다. 계속할까요?");
                break;
            }
            case ModuleIds.Deploy:
            {
                IReadOnlyList<DeployPackage> pkgs = [];
                try { pkgs = await _client.GetPackagesAsync(); } catch (GacClientException) { }
                var install = Radio("설치 파일 배포", true);
                var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
                foreach (var p in pkgs) combo.Items.Add($"{p.Name} ({p.FileName})");
                if (pkgs.Count > 0) combo.SelectedIndex = 0;
                var timeout = new NumericUpDown { Minimum = 1, Maximum = 240, Value = 30, Width = 70 };
                var script = Radio("명령 실행 (관리자 권한)");
                var shell = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
                shell.Items.AddRange(["명령 프롬프트", "PowerShell"]);
                shell.SelectedIndex = 0;
                var text = new TextBox { Multiline = true, Width = 440, Height = 110, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 10f) };
                script.Enabled = shell.Enabled = text.Enabled = Can(Permission.RunDeployScripts);
                var children = new List<Control> { Ui.Label("일괄 배포·실행", Ui.Bold), install, combo, Ui.Row(Ui.Label("제한 시간"), timeout, Ui.Label("분")), script, shell, text };
                if (!Can(Permission.RunDeployScripts)) children.Add(Ui.Label("운영자는 명령을 실행할 수 없습니다.", color: Ui.Muted));
                if (pkgs.Count == 0) children.Insert(2, Ui.Label("등록된 설치 파일이 없습니다. [배포 패키지] 탭에서 먼저 추가하세요.", color: Ui.Warn));
                _options.Controls.Add(Stack([.. children]));
                _build = () =>
                {
                    if (install.Checked)
                    {
                        if (combo.SelectedIndex < 0) { Ui.Error(this, "설치할 파일을 선택하세요."); return null; }
                        var p = pkgs[combo.SelectedIndex];
                        return (ModuleActions.DeployInstall, GacJson.Serialize(new DeployPackageRequest(p.Id, p.FileName, p.Sha256, p.SilentArgs, (int)timeout.Value * 60)),
                            $"PC {n}대에 '{p.Name}'을(를) 설치합니다. 계속할까요?");
                    }
                    if (string.IsNullOrWhiteSpace(text.Text)) { Ui.Error(this, "실행할 명령을 입력하세요."); return null; }
                    return (ModuleActions.DeployRunScript, GacJson.Serialize(new RunScriptRequest(shell.SelectedIndex == 1 ? ScriptShell.PowerShell : ScriptShell.Cmd, text.Text, (int)timeout.Value * 60)),
                        $"PC {n}대에서 관리자 권한으로 명령을 실행합니다. 계속할까요?");
                };
                break;
            }
            case ModuleIds.Health:
                _options.Controls.Add(Stack(Ui.Label("PC 상태 보고", Ui.Bold), Ui.Label("디스크 공간·상태, 메모리, 가동 시간, 부팅 시간을 지금 보고받습니다.")));
                _build = () => (ModuleActions.HealthReport, "{}", null);
                break;
            case ModuleIds.Inspect:
            {
                var run = Radio("점검 실행", true);
                var wall = new CheckBox { Text = "바탕화면을 학교 기본값으로 초기화", Checked = true, AutoSize = true };
                var led = new CheckBox { Text = "모니터/전자칠판 LED 점검 화면 띄우기 (PC 앞에서 진행)", Checked = true, AutoSize = true };
                var upd = new CheckBox { Text = "Windows 업데이트·에디션 확인", Checked = true, AutoSize = true };
                var prog = new CheckBox { Text = "허용 목록에 없는 프로그램 찾기", Checked = true, AutoSize = true };
                var uninstall = Radio("프로그램 제거 (최근 점검 결과에서 선택)");
                var progs = new CheckedListBox { Width = 440, Height = 120, CheckOnClick = true };
                var keys = new List<string>();
                try
                {
                    var latest = await _client.GetLatestAsync(ModuleIds.Inspect);
                    var ids = _targets.Select(t => t.Id).ToHashSet();
                    foreach (var r in latest.Where(r => ids.Contains(r.PcId) && r.DataJson != null && r.DataJson.TrimStart().StartsWith('{')))
                        foreach (var p in GacJson.Deserialize<InspectReport>(r.DataJson!)!.UnwantedPrograms.Where(p => p.CanUninstall))
                            if (!keys.Contains(p.Key)) { keys.Add(p.Key); progs.Items.Add($"{p.Name} {p.Version}"); }
                }
                catch (Exception e) when (e is GacClientException or System.Text.Json.JsonException) { }
                uninstall.Enabled = progs.Enabled = Can(Permission.UninstallPrograms) && keys.Count > 0;
                var hint = !Can(Permission.UninstallPrograms) ? "운영자는 프로그램을 제거할 수 없습니다." : keys.Count == 0 ? "먼저 점검을 실행하면 제거할 수 있는 프로그램 목록이 나타납니다." : "";
                _options.Controls.Add(Stack(Ui.Label("학기말 점검", Ui.Bold), run, wall, led, upd, prog, uninstall, progs, Ui.Label(hint, color: Ui.Muted)));
                _build = () =>
                {
                    if (run.Checked) return (ModuleActions.InspectRun, GacJson.Serialize(new InspectRequest(wall.Checked, led.Checked, upd.Checked, prog.Checked)), null);
                    var chosen = progs.CheckedIndices.Cast<int>().Select(i => keys[i]).ToList();
                    if (chosen.Count == 0) { Ui.Error(this, "제거할 프로그램을 선택하세요."); return null; }
                    return (ModuleActions.InspectUninstall, GacJson.Serialize(new UninstallRequest(chosen)), $"PC {n}대에서 프로그램 {chosen.Count}개를 제거합니다. 계속할까요?");
                };
                break;
            }
            case ModuleIds.NetDiag:
                _options.Controls.Add(Stack(Ui.Label("네트워크 진단", Ui.Bold), Ui.Label("PC의 어댑터·IP·게이트웨이·DNS·GAC 서버 연결을 원격으로 확인합니다.\n(자동 수리는 선생님이 트레이 메뉴에서 실행할 때만 합니다.)")));
                _build = () => (ModuleActions.NetDiagRun, "{}", null);
                break;
            case ModuleIds.ClassMode:
                _options.Controls.Add(Stack(Ui.Label("원클릭 수업 모드", Ui.Bold), Ui.Label("공용 프리셋은 [공용 프리셋] 탭에서 편집하면 PC가 다음 연결 때 자동으로 받습니다.\n이 작업은 각 PC가 최신 공용 프리셋을 가지고 있는지 확인합니다.")));
                _build = () => (ModuleActions.ClassModeSync, "{}", null);
                break;
        }
    }

    private async Task SendAsync()
    {
        var built = _build?.Invoke();
        if (built is not { } b) return;
        var offline = _targets.Count(t => !t.Online);
        var confirm = b.Confirm;
        if (offline > 0) confirm = (confirm == null ? "" : confirm + "\n\n") + $"꺼져 있는 PC {offline}대는 다음에 켜질 때 실행합니다 (3일 지나면 만료).";
        if (confirm != null && !Ui.Confirm(this, confirm)) return;
        UseWaitCursor = true;
        try
        {
            Result = await _client.SendCommandAsync(new SendCommandRequest(Module, b.Action, b.Payload, _targets.Select(t => t.Id).ToList()));
            DialogResult = DialogResult.OK;
        }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
        finally { UseWaitCursor = false; }
    }
}
