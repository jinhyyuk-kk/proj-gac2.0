using Gac.Console.Services;
using Gac.Shared.Auth;
using Gac.Shared.Contracts;
using Gac.Tray.UI;

namespace Gac.Console.UI;

/// <summary>계정 관리 (Owner only): add, edit, disable, delete accounts, reset passwords, assign roles and PC groups.</summary>
public sealed class AccountsForm : Form
{
    private readonly IGacServerClient _client;
    private readonly ListView _list = Ui.List(("아이디", 110), ("이름", 110), ("역할", 100), ("상태", 80), ("담당 그룹", 160), ("마지막 로그인", 130));
    private IReadOnlyList<AccountInfo> _accounts = [];
    private IReadOnlyList<PcGroup> _groups = [];

    public AccountsForm(IGacServerClient client)
    {
        _client = client;
        Ui.Apply(this, "계정 관리", new Size(780, 420));
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        bar.Controls.AddRange([
            Ui.Button("계정 추가", async (_, _) => await EditAsync(null)), Ui.Button("수정", async (_, _) => await EditAsync(Selected)),
            Ui.Button("비밀번호 초기화", async (_, _) => await ResetAsync(), 130), Ui.Button("삭제", async (_, _) => await DeleteAsync()),
        ]);
        Controls.Add(_list);
        Controls.Add(bar);
        Controls.Add(Ui.ButtonBar(Ui.Button("닫기", (_, _) => Close(), 80)));
        _list.DoubleClick += async (_, _) => await EditAsync(Selected);
        Load += async (_, _) => await ReloadAsync();
    }

    private AccountInfo? Selected => _list.SelectedItems.Count == 0 ? null : (AccountInfo)_list.SelectedItems[0].Tag!;

    private async Task ReloadAsync()
    {
        try
        {
            _accounts = await _client.GetAccountsAsync();
            _groups = await _client.GetGroupsAsync();
        }
        catch (GacClientException e) { Ui.Error(this, e.Message); return; }
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var a in _accounts)
        {
            var groups = a.Role == Role.Operator ? string.Join(", ", _groups.Where(g => a.GroupIds.Contains(g.Id)).Select(g => g.Name)) : "전체";
            var state = a.Disabled ? "사용 중지" : a.LockedUntil != null ? "잠김" : "사용";
            _list.Items.Add(new ListViewItem([a.UserName, a.DisplayName, RolePermissions.KoreanLabel(a.Role), state, groups,
                a.LastLogin?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-"]) { Tag = a, ForeColor = a.Disabled ? Ui.Muted : SystemColors.ControlText });
        }
        _list.EndUpdate();
    }

    private async Task EditAsync(AccountInfo? a)
    {
        using var dlg = new AccountEditDialog(a, _groups);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            if (a == null) await _client.CreateAccountAsync(dlg.Result!);
            else await _client.UpdateAccountAsync(a.Id, dlg.Result!);
            await ReloadAsync();
        }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }

    private async Task ResetAsync()
    {
        if (Selected is not { } a) return;
        using var dlg = new PasswordDialog($"'{a.DisplayName}' 비밀번호 초기화");
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { await _client.ResetPasswordAsync(a.Id, dlg.Password); Ui.Info(this, "비밀번호를 초기화했습니다. 잠금도 해제되었습니다."); await ReloadAsync(); }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }

    private async Task DeleteAsync()
    {
        if (Selected is not { } a || !Ui.Confirm(this, $"'{a.DisplayName}' ({a.UserName}) 계정을 삭제할까요?")) return;
        try { await _client.DeleteAccountAsync(a.Id); await ReloadAsync(); }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }
}

public sealed class AccountEditDialog : Form
{
    private readonly TextBox _user = new() { Width = 220 };
    private readonly TextBox _display = new() { Width = 220 };
    private readonly ComboBox _role = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly CheckBox _disabled = new() { Text = "사용 중지", AutoSize = true };
    private readonly TextBox _pw = new() { Width = 220, UseSystemPasswordChar = true };
    private readonly CheckedListBox _groups = new() { Width = 220, Height = 110, CheckOnClick = true };
    private readonly IReadOnlyList<PcGroup> _groupList;
    private readonly bool _isNew;
    public SaveAccountRequest? Result { get; private set; }

    public AccountEditDialog(AccountInfo? a, IReadOnlyList<PcGroup> groups)
    {
        _isNew = a == null;
        _groupList = groups;
        Ui.Apply(this, _isNew ? "계정 추가" : "계정 수정", new Size(420, 400), resizable: false);
        var roles = a?.Role == Role.Owner ? new[] { Role.Owner } : new[] { Role.Admin, Role.Operator, Role.Viewer };
        foreach (var r in roles) _role.Items.Add(RolePermissions.KoreanLabel(r));
        _role.Tag = roles;
        foreach (var g in groups) _groups.Items.Add(g.Name, a?.GroupIds.Contains(g.Id) ?? false);
        _user.Text = a?.UserName ?? "";
        _display.Text = a?.DisplayName ?? "";
        _role.SelectedIndex = a == null ? Array.IndexOf(roles, Role.Operator) : Math.Max(0, Array.IndexOf(roles, a.Role));
        _disabled.Checked = a?.Disabled ?? false;
        _disabled.Enabled = a?.Role != Role.Owner;
        _pw.PlaceholderText = _isNew ? "8자 이상, 문자+숫자" : "바꿀 때만 입력";

        var grid = Ui.Grid();
        Ui.AddRow(grid, "아이디", _user);
        Ui.AddRow(grid, "이름", _display);
        Ui.AddRow(grid, "역할", _role);
        Ui.AddRow(grid, "비밀번호", _pw);
        Ui.AddRow(grid, "담당 그룹 (운영자)", _groups);
        Ui.AddRow(grid, "", _disabled);
        var ok = Ui.Button("확인", (_, _) => Accept(), 80);
        var cancel = Ui.Button("취소", (_, _) => DialogResult = DialogResult.Cancel, 80);
        Controls.Add(grid);
        Controls.Add(Ui.ButtonBar(cancel, ok));
        AcceptButton = ok;
        CancelButton = cancel;
        _role.SelectedIndexChanged += (_, _) => _groups.Enabled = SelectedRole == Role.Operator;
        _groups.Enabled = SelectedRole == Role.Operator;
    }

    private Role SelectedRole => ((Role[])_role.Tag!)[_role.SelectedIndex];

    private void Accept()
    {
        if (_isNew && string.IsNullOrEmpty(_pw.Text)) { Ui.Error(this, "비밀번호를 입력하세요."); return; }
        var groupIds = _groups.CheckedIndices.Cast<int>().Select(i => _groupList[i].Id).ToList();
        if (SelectedRole == Role.Operator && groupIds.Count == 0 && !Ui.Confirm(this, "담당 그룹이 없는 운영자는 어떤 PC도 관리할 수 없습니다. 계속할까요?")) return;
        Result = new SaveAccountRequest(_user.Text.Trim(), _display.Text.Trim(), SelectedRole, _disabled.Checked, groupIds, string.IsNullOrEmpty(_pw.Text) ? null : _pw.Text);
        DialogResult = DialogResult.OK;
    }
}

public sealed class PasswordDialog : Form
{
    private readonly TextBox _current = new() { Width = 220, UseSystemPasswordChar = true };
    private readonly TextBox _pw = new() { Width = 220, UseSystemPasswordChar = true };
    private readonly TextBox _pw2 = new() { Width = 220, UseSystemPasswordChar = true };
    private readonly bool _askCurrent;
    public string Password => _pw.Text;
    public string CurrentPassword => _current.Text;

    public PasswordDialog(string title, bool askCurrent = false)
    {
        _askCurrent = askCurrent;
        Ui.Apply(this, title, new Size(400, askCurrent ? 220 : 180), resizable: false);
        var grid = Ui.Grid();
        if (askCurrent) Ui.AddRow(grid, "현재 비밀번호", _current);
        Ui.AddRow(grid, "새 비밀번호", _pw);
        Ui.AddRow(grid, "새 비밀번호 확인", _pw2);
        var ok = Ui.Button("확인", (_, _) =>
        {
            if (_pw.Text != _pw2.Text) { Ui.Error(this, "새 비밀번호 확인이 일치하지 않습니다."); return; }
            if (_askCurrent && _current.Text.Length == 0) { Ui.Error(this, "현재 비밀번호를 입력하세요."); return; }
            DialogResult = DialogResult.OK;
        }, 80);
        var cancel = Ui.Button("취소", (_, _) => DialogResult = DialogResult.Cancel, 80);
        Controls.Add(grid);
        Controls.Add(Ui.ButtonBar(cancel, ok));
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

/// <summary>Per-group schedules: auto shutdown, auto clean, health and error-check intervals.</summary>
public sealed class GroupPolicyForm : Form
{
    private readonly IGacServerClient _client;
    private readonly int _groupId;
    private readonly CheckBox _sdEnabled = new() { Text = "자동 종료 사용", AutoSize = true };
    private readonly DateTimePicker _sdTime = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 90 };
    private readonly CheckedListBox _days = new() { Width = 300, Height = 26, MultiColumn = true, ColumnWidth = 42, CheckOnClick = true };
    private readonly NumericUpDown _warn = new() { Minimum = 1, Maximum = 30, Width = 70 };
    private readonly NumericUpDown _extMin = new() { Minimum = 5, Maximum = 120, Width = 70 };
    private readonly NumericUpDown _extMax = new() { Minimum = 0, Maximum = 10, Width = 70 };
    private readonly NumericUpDown _grace = new() { Minimum = 1, Maximum = 60, Width = 70 };
    private readonly CheckBox _clEnabled = new() { Text = "자동 정리 사용", AutoSize = true };
    private readonly CheckBox _clDry = new() { Text = "미리 보기만 (실제로 삭제하지 않음)", AutoSize = true };
    private readonly CheckBox _clShutdown = new() { Text = "로그오프·종료할 때 정리", AutoSize = true };
    private readonly CheckBox _clScheduled = new() { Text = "매일 정해진 시각에 정리", AutoSize = true };
    private readonly DateTimePicker _clTime = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 90 };
    private readonly NumericUpDown _retention = new() { Minimum = 1, Maximum = 365, Width = 70 };
    private readonly CheckedListBox _targets = new() { Width = 300, Height = 96, CheckOnClick = true };
    private readonly NumericUpDown _healthHours = new() { Minimum = 1, Maximum = 168, Width = 70 };
    private readonly NumericUpDown _freeWarn = new() { Minimum = 1, Maximum = 50, Width = 70 };
    private readonly NumericUpDown _errorHours = new() { Minimum = 1, Maximum = 168, Width = 70 };
    private GroupPolicy? _policy;
    private static readonly DayOfWeek[] DayOrder = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public GroupPolicyForm(IGacServerClient client, PcGroup group, bool canEdit)
    {
        _client = client;
        _groupId = group.Id;
        Ui.Apply(this, $"그룹 정책 – {group.Name}", new Size(560, 640));
        _days.Items.AddRange(["월", "화", "수", "목", "금", "토", "일"]);
        _targets.Items.AddRange(["Windows 임시 파일", "사용자 임시 파일", "브라우저 캐시 (Chrome/Edge, 실행 중이 아닐 때)", "휴지통", "다운로드 폴더의 오래된 파일"]);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var sd = Ui.Grid();
        Ui.AddRow(sd, "", _sdEnabled);
        Ui.AddRow(sd, "종료 시각", _sdTime);
        Ui.AddRow(sd, "요일", _days);
        Ui.AddRow(sd, "경고 표시 (분 전)", _warn);
        Ui.AddRow(sd, "연장 시간 (분)", _extMin);
        Ui.AddRow(sd, "최대 연장 횟수", _extMax);
        Ui.AddRow(sd, "강제 종료까지 유예 (분)", _grace);
        tabs.TabPages.Add(Page("자동 종료", sd));
        var cl = Ui.Grid();
        Ui.AddRow(cl, "", _clEnabled);
        Ui.AddRow(cl, "", _clDry);
        Ui.AddRow(cl, "", _clShutdown);
        Ui.AddRow(cl, "", Ui.Row(_clScheduled, _clTime));
        Ui.AddRow(cl, "다운로드 보관 (일)", _retention);
        Ui.AddRow(cl, "정리 대상", _targets);
        tabs.TabPages.Add(Page("자동 정리", cl));
        var hp = Ui.Grid();
        Ui.AddRow(hp, "상태 보고 주기 (시간)", _healthHours);
        Ui.AddRow(hp, "남은 공간 경고 (%)", _freeWarn);
        Ui.AddRow(hp, "오류 점검 주기 (시간)", _errorHours);
        tabs.TabPages.Add(Page("상태·오류 보고", hp));

        var save = Ui.Button("저장", async (_, _) => await SaveAsync(), 80);
        save.Enabled = canEdit;
        Controls.Add(tabs);
        Controls.Add(Ui.ButtonBar(Ui.Button("닫기", (_, _) => Close(), 80), save));
        Load += async (_, _) => await LoadAsync();
    }

    private static TabPage Page(string title, Control content)
    {
        var p = new TabPage(title) { Padding = new Padding(8), AutoScroll = true };
        p.Controls.Add(content);
        return p;
    }

    private async Task LoadAsync()
    {
        try { _policy = await _client.GetGroupPolicyAsync(_groupId); }
        catch (GacClientException e) { Ui.Error(this, e.Message); Close(); return; }
        var s = _policy.Shutdown;
        _sdEnabled.Checked = s.Enabled;
        _sdTime.Value = DateTime.Today + (TimeOnly.TryParse(s.Time, out var t) ? t.ToTimeSpan() : new TimeSpan(17, 30, 0));
        for (var i = 0; i < 7; i++) _days.SetItemChecked(i, s.Days.Contains(DayOrder[i]));
        _warn.Value = s.WarningMinutes; _extMin.Value = Math.Clamp(s.ExtensionMinutes, 5, 120); _extMax.Value = s.MaxExtensions; _grace.Value = s.ForceGraceMinutes;
        var c = _policy.Clean;
        _clEnabled.Checked = c.Enabled; _clDry.Checked = c.DryRun; _clShutdown.Checked = c.RunOnShutdown;
        _clScheduled.Checked = c.ScheduleTime != null;
        _clTime.Value = DateTime.Today + (TimeOnly.TryParse(c.ScheduleTime ?? "12:40", out var ct) ? ct.ToTimeSpan() : new TimeSpan(12, 40, 0));
        _retention.Value = c.DownloadsRetentionDays;
        bool[] flags = [c.WindowsTemp, c.UserTemp, c.BrowserCache, c.RecycleBin, c.Downloads];
        for (var i = 0; i < flags.Length; i++) _targets.SetItemChecked(i, flags[i]);
        _healthHours.Value = _policy.Health.IntervalHours; _freeWarn.Value = _policy.Health.FreeSpaceWarnPercent; _errorHours.Value = _policy.Errors.IntervalHours;
    }

    private async Task SaveAsync()
    {
        if (_policy == null) return;
        bool T(int i) => _targets.GetItemChecked(i);
        var updated = _policy with
        {
            Shutdown = new ShutdownPolicy
            {
                Enabled = _sdEnabled.Checked, Time = _sdTime.Value.ToString("HH:mm"),
                Days = Enumerable.Range(0, 7).Where(_days.GetItemChecked).Select(i => DayOrder[i]).ToArray(),
                WarningMinutes = (int)_warn.Value, ExtensionMinutes = (int)_extMin.Value, MaxExtensions = (int)_extMax.Value, ForceGraceMinutes = (int)_grace.Value,
            },
            Clean = new CleanPolicy
            {
                Enabled = _clEnabled.Checked, DryRun = _clDry.Checked, RunOnShutdown = _clShutdown.Checked,
                ScheduleTime = _clScheduled.Checked ? _clTime.Value.ToString("HH:mm") : null, DownloadsRetentionDays = (int)_retention.Value,
                WindowsTemp = T(0), UserTemp = T(1), BrowserCache = T(2), RecycleBin = T(3), Downloads = T(4),
            },
            Health = new HealthPolicy { IntervalHours = (int)_healthHours.Value, FreeSpaceWarnPercent = (int)_freeWarn.Value },
            Errors = _policy.Errors with { IntervalHours = (int)_errorHours.Value },
        };
        if (!updated.Clean.DryRun && _policy.Clean.DryRun &&
            !Ui.Confirm(this, "‘미리 보기만’을 끄면 이 그룹 PC에서 실제로 파일이 삭제됩니다.\n먼저 미리 보기 결과를 확인하셨나요? 계속할까요?")) return;
        try { await _client.SaveGroupPolicyAsync(updated); _policy = updated; Ui.Info(this, "저장했습니다. PC가 다음에 서버에 연결할 때 적용됩니다."); }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }
}

/// <summary>Server settings (Owner): net-diag targets, inspection wallpaper and program allowlist, poll interval, enrollment code.</summary>
public sealed class ServerSettingsForm : Form
{
    private readonly IGacServerClient _client;
    private readonly TextBox _external = new() { Width = 260, PlaceholderText = "비워 두면 확인하지 않음 (기본)" };
    private readonly TextBox _dnsName = new() { Width = 260, PlaceholderText = "비워 두면 GAC 서버 이름으로 확인" };
    private readonly TextBox _wallpaper = new() { Width = 320 };
    private readonly TextBox _allow = new() { Width = 320, Height = 140, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly NumericUpDown _poll = new() { Minimum = 10, Maximum = 600, Width = 80 };
    private readonly Label _code = Ui.Label("", Ui.Bold);

    public ServerSettingsForm(IGacServerClient client)
    {
        _client = client;
        Ui.Apply(this, "서버 설정", new Size(560, 520));
        var grid = Ui.Grid();
        Ui.AddRow(grid, "외부 연결 확인 주소", _external);
        Ui.AddRow(grid, "DNS 확인용 이름", _dnsName);
        Ui.AddRow(grid, "학교 기본 바탕화면", _wallpaper);
        Ui.AddRow(grid, "허용 프로그램 목록\n(한 줄에 하나, 이름·제조사 일부)", _allow);
        Ui.AddRow(grid, "PC 확인 주기 (초)", _poll);
        Ui.AddRow(grid, "등록 코드", Ui.Row(_code, Ui.Button("새 코드 만들기", async (_, _) => await RegenerateAsync(), 120)));
        var save = Ui.Button("저장", async (_, _) => await SaveAsync(), 80);
        Controls.Add(grid);
        Controls.Add(Ui.ButtonBar(Ui.Button("닫기", (_, _) => Close(), 80), save));
        Load += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var s = await _client.GetSettingsAsync();
            _external.Text = s.NetDiagExternalTarget ?? "";
            _dnsName.Text = s.NetDiagDnsTestName ?? "";
            _wallpaper.Text = s.WallpaperPath;
            _allow.Text = string.Join(Environment.NewLine, s.ProgramAllowlist);
            _poll.Value = Math.Clamp(s.PollSeconds, 10, 600);
            var e = await _client.GetEnrollmentAsync();
            _code.Text = e.Code;
        }
        catch (GacClientException ex) { Ui.Error(this, ex.Message); }
    }

    private async Task SaveAsync()
    {
        var s = new ServerSettings(Blank(_external.Text), Blank(_dnsName.Text), _wallpaper.Text.Trim(),
            _allow.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToArray(), (int)_poll.Value);
        try { await _client.SaveSettingsAsync(s); Ui.Info(this, "저장했습니다."); }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }

    private async Task RegenerateAsync()
    {
        if (!Ui.Confirm(this, "새 등록 코드를 만들면 이전 코드로는 새 PC를 등록할 수 없습니다. 이미 등록된 PC는 영향이 없습니다. 계속할까요?")) return;
        try { _code.Text = (await _client.RegenerateEnrollmentAsync()).Code; }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
