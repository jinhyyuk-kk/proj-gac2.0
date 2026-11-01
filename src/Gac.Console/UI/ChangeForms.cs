using Gac.Console.Services;
using Gac.Shared;
using Gac.Shared.Auth;
using Gac.Shared.ClassMode;
using Gac.Shared.Contracts;
using Gac.Tray.UI;

namespace Gac.Console.UI;

/// <summary>차단된 기기 / 확인 필요 목록 (CHANGE-001 1.2/1.4). Approve battery-review devices, clear console-first, see blocks.</summary>
public sealed class DevicesForm : Form
{
    private readonly IGacServerClient _client;
    private readonly ListView _blocked = Ui.List(("시각", 130), ("이름", 120), ("모델", 140), ("사유", 300), ("영구", 60));
    private readonly ListView _pending = Ui.List(("시각", 130), ("이름", 120), ("모델", 140), ("사유", 320));

    public DevicesForm(IGacServerClient client)
    {
        _client = client;
        Ui.Apply(this, "기기 관리", new Size(780, 520));
        var tabs = new TabControl { Dock = DockStyle.Fill };

        var pendPage = new TabPage("확인 필요");
        var pendBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        pendBar.Controls.AddRange([Ui.Button("승인", async (_, _) => await ApproveAsync(), 80),
            Ui.Label("배터리 신호가 감지된 데스크톱(예: UPS)입니다. 최고 관리자가 승인하면 등록됩니다.", color: Ui.Muted)]);
        pendPage.Controls.Add(_pending);
        pendPage.Controls.Add(pendBar);

        var blockPage = new TabPage("차단된 기기");
        var blockBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        blockBar.Controls.AddRange([Ui.Button("설치 순서 초기화(허용)", async (_, _) => await ClearAsync(), 150),
            Ui.Label("노트북·휴대형으로 차단된 기기는 초기화할 수 없습니다. ‘관리자 설치 먼저’로 차단된 기기만 허용됩니다.", color: Ui.Muted)]);
        blockPage.Controls.Add(_blocked);
        blockPage.Controls.Add(blockBar);

        tabs.TabPages.Add(pendPage);
        tabs.TabPages.Add(blockPage);
        Controls.Add(tabs);
        Controls.Add(Ui.ButtonBar(Ui.Button("닫기", (_, _) => Close(), 80), Ui.Button("새로 고침", async (_, _) => await ReloadAsync(), 90)));
        Load += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            _pending.Items.Clear();
            foreach (var d in await _client.GetPendingDevicesAsync())
                _pending.Items.Add(new ListViewItem([d.At.ToLocalTime().ToString("MM-dd HH:mm"), d.ReportedName, d.Model ?? "", d.Reason]) { Tag = d });
            _blocked.Items.Clear();
            foreach (var d in await _client.GetBlockedDevicesAsync())
                _blocked.Items.Add(new ListViewItem([d.At.ToLocalTime().ToString("MM-dd HH:mm"), d.ReportedName, d.Model ?? "", d.Reason, d.PermanentlyBlocked ? "예" : ""]) { Tag = d, ForeColor = d.PermanentlyBlocked ? Ui.Bad : SystemColors.ControlText });
        }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }

    private async Task ApproveAsync()
    {
        if (_pending.SelectedItems.Count == 0) return;
        var d = (PendingDevice)_pending.SelectedItems[0].Tag!;
        try { await _client.ApproveDeviceAsync(d.Fingerprint); await ReloadAsync(); }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }

    private async Task ClearAsync()
    {
        if (_blocked.SelectedItems.Count == 0) return;
        var d = (BlockedDevice)_blocked.SelectedItems[0].Tag!;
        if (d.PermanentlyBlocked) { Ui.Error(this, "노트북·휴대형으로 차단된 기기는 허용할 수 없습니다."); return; }
        if (!Ui.Confirm(this, $"'{d.ReportedName}'의 설치 순서 차단을 해제할까요? 이후 이 PC는 클라이언트로 등록될 수 있습니다.")) return;
        try { await _client.ClearConsoleFirstAsync(d.Fingerprint); await ReloadAsync(); }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }
}

/// <summary>기간제 프리셋 (CHANGE-001 2.3): create a class-mode preset that runs for N days; stop early.</summary>
public sealed class PeriodsForm : Form
{
    private readonly IGacServerClient _client;
    private readonly IReadOnlyList<PcSummary> _pcs;
    private readonly IReadOnlyList<PcGroup> _groups;
    private readonly ListView _list = Ui.List(("기간 이름", 160), ("시작", 120), ("종료", 120), ("대상", 60), ("잠금", 50), ("상태", 70), ("만든 사람", 100));

    public PeriodsForm(IGacServerClient client, IReadOnlyList<PcSummary> pcs, IReadOnlyList<PcGroup> groups)
    {
        _client = client;
        _pcs = pcs;
        _groups = groups;
        Ui.Apply(this, "기간제 프리셋", new Size(820, 460));
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        bar.Controls.AddRange([Ui.Button("새 기간 시작…", async (_, _) => await CreateAsync(), 120), Ui.Button("선택 기간 중지", async (_, _) => await StopAsync(), 120),
            Ui.Label("기간 동안 대상 PC가 로그인할 때마다 프리셋이 실행됩니다. ‘PC 잠금’을 넣으면 그 기간에 PC를 쓸 수 없습니다.", color: Ui.Muted)]);
        Controls.Add(_list);
        Controls.Add(bar);
        Controls.Add(Ui.ButtonBar(Ui.Button("닫기", (_, _) => Close(), 80), Ui.Button("새로 고침", async (_, _) => await ReloadAsync(), 90)));
        Load += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            _list.Items.Clear();
            foreach (var p in await _client.GetPeriodsAsync())
                _list.Items.Add(new ListViewItem([p.PeriodName, p.StartAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), p.EndAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    p.TargetCount.ToString(), p.HasLock ? "예" : "", p.Running ? "진행 중" : "종료", p.StartedBy]) { Tag = p, ForeColor = p.Running ? Ui.Accent : Ui.Muted });
        }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }

    private async Task CreateAsync()
    {
        using var dlg = new CreatePeriodDialog(_pcs, _groups);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Request == null) return;
        try { await _client.CreatePeriodAsync(dlg.Request); await ReloadAsync(); }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }

    private async Task StopAsync()
    {
        if (_list.SelectedItems.Count == 0) return;
        var p = (PeriodPresetInfo)_list.SelectedItems[0].Tag!;
        if (!Ui.Confirm(this, $"'{p.PeriodName}' 기간을 지금 중지할까요?")) return;
        try { await _client.StopPeriodAsync(p.Id); await ReloadAsync(); }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }
}

/// <summary>Create a period: name, start, days, targets, and a one-preset editor that may include lock_pc.</summary>
public sealed class CreatePeriodDialog : Form
{
    private readonly TextBox _name = new() { Width = 240 };
    private readonly DateTimePicker _start = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", Width = 160 };
    private readonly NumericUpDown _days = new() { Minimum = 1, Maximum = 400, Value = 7, Width = 70 };
    private readonly CheckedListBox _groups = new() { Width = 260, Height = 90, CheckOnClick = true };
    private readonly CheckedListBox _pcList = new() { Width = 260, Height = 110, CheckOnClick = true };
    private ClassPreset _preset = new() { Name = "기간제", Trigger = PresetTrigger.Login, Actions = [new() { Type = PresetActionTypes.LockPc }] };
    private readonly Label _presetSummary = Ui.Label("");
    private readonly IReadOnlyList<PcSummary> _pcs;
    private readonly IReadOnlyList<PcGroup> _groupList;
    public CreatePeriodRequest? Request { get; private set; }

    public CreatePeriodDialog(IReadOnlyList<PcSummary> pcs, IReadOnlyList<PcGroup> groups)
    {
        _pcs = pcs;
        _groupList = groups;
        Ui.Apply(this, "새 기간 시작", new Size(560, 560), resizable: false);
        _start.Value = DateTime.Now;
        foreach (var g in groups) _groups.Items.Add(g.Name);
        foreach (var p in pcs) _pcList.Items.Add($"{p.Name} ({p.WindowsName})");

        var grid = Ui.Grid();
        Ui.AddRow(grid, "기간 이름", _name);
        Ui.AddRow(grid, "시작", _start);
        Ui.AddRow(grid, "기간(일)", _days);
        Ui.AddRow(grid, "대상 그룹", _groups);
        Ui.AddRow(grid, "대상 PC(개별)", _pcList);
        Ui.AddRow(grid, "프리셋", Ui.Row(Ui.Button("프리셋 편집…", (_, _) => EditPreset(), 110), _presetSummary));
        Controls.Add(grid);
        Controls.Add(Ui.ButtonBar(Ui.Button("취소", (_, _) => DialogResult = DialogResult.Cancel, 80), Ui.Button("시작하기", (_, _) => Accept(), 90)));
        UpdateSummary();
    }

    private void EditPreset()
    {
        var file = new PresetFile { Presets = [GacJson.Deserialize<ClassPreset>(GacJson.Serialize(_preset))!] };
        using var editor = new PresetEditorForm(file, allowRunCommand: true, f =>
        {
            if (f.Presets.Count != 1) return Task.FromResult<string?>("기간제 프리셋은 하나만 담을 수 있습니다.");
            _preset = f.Presets[0];
            UpdateSummary();
            return Task.FromResult<string?>(null);
        }, run: null, "기간제 프리셋 편집 (PC 잠금 사용 가능)");
        editor.ShowDialog(this);
    }

    private void UpdateSummary() => _presetSummary.Text = $"동작 {_preset.Actions.Count}개" + (_preset.Actions.Any(a => a.Type == PresetActionTypes.LockPc) ? " · PC 잠금 포함" : "");

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(_name.Text)) { Ui.Error(this, "기간 이름을 입력하세요."); return; }
        var groupIds = _groups.CheckedIndices.Cast<int>().Select(i => _groupList[i].Id).ToList();
        var pcIds = _pcList.CheckedIndices.Cast<int>().Select(i => _pcs[i].Id).ToList();
        if (groupIds.Count == 0 && pcIds.Count == 0) { Ui.Error(this, "대상 그룹이나 PC를 선택하세요."); return; }
        _preset.Name = _name.Text.Trim();
        Request = new CreatePeriodRequest(_name.Text.Trim(), GacJson.Serialize(_preset), _start.Value, (int)_days.Value, pcIds, groupIds);
        DialogResult = DialogResult.OK;
    }
}

/// <summary>권한 매트릭스 편집 (CHANGE-001 §5): toggle each editable permission per non-Owner role.</summary>
public sealed class PermissionMatrixForm : Form
{
    private readonly IGacServerClient _client;
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, RowHeadersVisible = false, AllowUserToResizeRows = false };
    private PermissionMatrix _matrix = PermissionMatrix.Default();
    private static readonly Role[] Roles = [Role.Admin, Role.Operator, Role.Viewer];

    public PermissionMatrixForm(IGacServerClient client)
    {
        _client = client;
        Ui.Apply(this, "역할별 권한 설정", new Size(640, 540));
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "권한", ReadOnly = true, FillWeight = 200, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        foreach (var r in Roles) _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = RolePermissions.KoreanLabel(r), FillWeight = 80 });
        Controls.Add(_grid);
        Controls.Add(Ui.ButtonBar(Ui.Button("취소", (_, _) => Close(), 80), Ui.Button("저장", async (_, _) => await SaveAsync(), 80)));
        Controls.Add(new Label { Dock = DockStyle.Top, Height = 40, Text = "최고 관리자는 항상 모든 권한을 가집니다. 계정·서버 설정 권한은 다른 역할에 줄 수 없습니다.", ForeColor = Ui.Muted, Padding = new Padding(8, 10, 8, 0) });
        Load += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try { _matrix = await _client.GetMatrixAsync(); } catch (GacClientException e) { Ui.Error(this, e.Message); Close(); return; }
        _grid.Rows.Clear();
        foreach (var p in RolePermissions.Editable)
        {
            var i = _grid.Rows.Add();
            var row = _grid.Rows[i];
            row.Cells[0].Value = PermissionLabel(p);
            row.Tag = p;
            for (var c = 0; c < Roles.Length; c++) row.Cells[c + 1].Value = _matrix.Effective(Roles[c]).Contains(p);
        }
    }

    private async Task SaveAsync()
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var p = (Permission)row.Tag!;
            for (var c = 0; c < Roles.Length; c++) _matrix.Set(Roles[c], p, row.Cells[c + 1].Value is true);
        }
        try { await _client.SaveMatrixAsync(_matrix); Ui.Info(this, "저장했습니다. 각 계정의 다음 요청부터 적용됩니다."); Close(); }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }

    private static string PermissionLabel(Permission p) => p switch
    {
        Permission.ViewPcs => "PC 보기",
        Permission.ViewReports => "보고서 보기",
        Permission.ViewAudit => "감사 로그 보기",
        Permission.RunModules => "모듈 실행",
        Permission.RunDeployScripts => "명령 실행(배포)",
        Permission.UninstallPrograms => "프로그램 제거",
        Permission.ManageGroups => "그룹 관리",
        Permission.ManagePolicies => "정책 관리",
        Permission.ManagePresets => "프리셋·기간 관리",
        Permission.ManagePackages => "배포 패키지 관리",
        Permission.UnlockPc => "PC 잠금 해제",
        Permission.ManageDevices => "기기 승인·차단",
        Permission.ManageCalendar => "캘린더 관리",
        Permission.ManageServerShutdown => "서버 PC 종료 설정",
        _ => p.ToString(),
    };
}
