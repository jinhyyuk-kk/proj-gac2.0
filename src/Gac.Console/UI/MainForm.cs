using Gac.Console.EasterEgg;
using Gac.Console.Services;
using Gac.Shared;
using Gac.Shared.Auth;
using Gac.Shared.ClassMode;
using Gac.Shared.Contracts;
using Gac.Tray.UI;

namespace Gac.Console.UI;

public sealed class ConsoleOptions
{
    /// <summary>False in the demo: nothing is written to disk.</summary>
    public bool AllowFileExport { get; init; } = true;
    public string? Banner { get; init; }
    public bool ShowTour { get; init; }
    public IReadOnlyList<(string Text, Action<IWin32Window> Open)> ExtraViewMenu { get; init; } = [];
}

/// <summary>Console main window. The same code runs against a real server and inside GAC-Demo.exe.</summary>
public sealed class MainForm : Form
{
    internal const string AllPcsNode = "__all";
    internal const string NoGroupNode = "__none";

    private readonly IGacServerClient _client;
    private readonly ConsoleOptions _options;
    private AccountInfo _me;
    private readonly AboutEasterEgg _egg = new(TimeProvider.System);
    private readonly MenuStrip _menu = new() { Dock = DockStyle.Top };
    private readonly ToolStripMenuItem _aboutItem = new(AboutEasterEgg.DialogTitle) { Visible = false };
    private readonly Label _logo = new() { Text = "GAC 2.0", AutoSize = true, Font = new Font("맑은 고딕", 16f, FontStyle.Bold), ForeColor = Ui.Accent, Margin = new Padding(10, 6, 4, 4), Cursor = Cursors.Default };
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, Font = Ui.Font, FullRowSelect = true, ShowLines = false };
    internal readonly TabControl Tabs = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _grid = new();
    private readonly ListView _batches = Ui.List(("시각", 120), ("작업", 200), ("실행자", 90), ("진행", 80), ("실패", 60));
    private readonly ListView _rows = Ui.List(("PC", 120), ("상태", 80), ("결과", 420), ("완료", 120));
    private readonly TextBox _rowDetail = Detail();
    private readonly ComboBox _reportModule = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly ListView _reportList = Ui.List(("PC", 130), ("결과", 60), ("요약", 460), ("시각", 120));
    private readonly TextBox _reportDetail = Detail();
    private readonly ListView _packages = Ui.List(("이름", 150), ("파일", 150), ("크기", 80), ("설치 인수", 140), ("SHA-256", 160), ("등록", 140));
    private readonly ListView _presetList = Ui.List(("공용 프리셋", 220), ("실행 시점", 140), ("동작 수", 80));
    private readonly Label _presetInfo = Ui.Label("", color: Ui.Muted);
    private readonly ListView _audit = Ui.List(("시각", 140), ("누가", 110), ("무엇을", 200), ("대상", 180), ("내용", 300));
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 30_000 };
    private IReadOnlyList<PcSummary> _pcs = [];
    private IReadOnlyList<PcGroup> _groups = [];
    private IReadOnlyList<ReportRow> _reports = [];
    private SharedPresetInfo? _sharedPresets;
    private bool _busy;

    private static readonly (string Module, string Label)[] ReportModules =
    [
        (ModuleIds.Health, "PC 상태"), (ModuleIds.Errors, "오류 점검"), (ModuleIds.Update, "업데이트"), (ModuleIds.Inspect, "학기말 점검"),
        (ModuleIds.Clean, "자동 정리"), (ModuleIds.NetDiag, "네트워크 진단"), (ModuleIds.Shutdown, "자동 종료 기록"), (ModuleIds.Deploy, "배포·실행"),
    ];

    public MainForm(IGacServerClient client, AccountInfo me, ConsoleOptions options)
    {
        _client = client;
        _me = me;
        _options = options;
        Ui.Apply(this, "GAC 2.0 콘솔", new Size(1180, 720));
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        MainMenuStrip = _menu;

        BuildMenu();
        var header = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Ui.Panel };
        var who = Ui.Label($"{me.DisplayName} ({RolePermissions.KoreanLabel(me.Role)}) · 서버 {client.ServerDisplayName}", color: Ui.Muted);
        who.Dock = DockStyle.Right;
        who.Padding = new Padding(0, 12, 12, 0);
        var sub = Ui.Label("학교 PC 관리", color: Ui.Muted);
        sub.Margin = new Padding(2, 16, 0, 0);
        header.Controls.Add(Ui.Row(_logo, sub));
        header.Controls.Add(who);
        // Easter egg: the logo click only counts; it never navigates or refreshes (EASTER_EGG.md).
        _logo.Click += (_, _) => { if (_egg.RegisterClick()) _aboutItem.Visible = true; };

        var left = new Panel { Dock = DockStyle.Left, Width = 210, Padding = new Padding(6) };
        left.Controls.Add(_tree);
        var leftTitle = Ui.Label("PC 그룹", Ui.Bold);
        leftTitle.Dock = DockStyle.Top;
        left.Controls.Add(leftTitle);
        _tree.AfterSelect += (_, _) => FillGrid();

        BuildPcTab();
        BuildResultsTab();
        BuildReportsTab();
        BuildPresetsTab();
        BuildPackagesTab();
        if (Can(Permission.ViewAudit)) BuildAuditTab();
        Tabs.SelectedIndexChanged += async (_, _) => await RefreshCurrentTabAsync();

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);

        Controls.Add(Tabs);
        Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
        Controls.Add(left);
        if (options.Banner != null)
            Controls.Add(new Label { Text = options.Banner, Dock = DockStyle.Top, Height = 30, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.FromArgb(255, 236, 153), Font = Ui.Bold });
        Controls.Add(header);
        Controls.Add(_menu);
        Controls.Add(statusStrip);

        _refresh.Tick += async (_, _) => await RefreshAllAsync(quiet: true);
        Load += async (_, _) =>
        {
            await RefreshAllAsync(quiet: false);
            _refresh.Start();
            if (_options.ShowTour) BeginInvoke(() => GuidedTour.Show(this));
        };
        FormClosed += (_, _) => _refresh.Dispose();
    }

    internal bool Can(Permission p) => RolePermissions.Has(_me.Role, p);
    internal ToolStripMenuItem AboutMenuItem => _aboutItem;
    internal MenuStrip MenuBar => _menu;

    private static TextBox Detail() => new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("맑은 고딕", 9.5f), BackColor = SystemColors.Window };

    // ---------- menu ----------
    private void BuildMenu()
    {
        var file = new ToolStripMenuItem("파일");
        file.DropDownItems.Add(new ToolStripMenuItem("새로 고침", null, async (_, _) => await RefreshAllAsync(false), Keys.F5));
        if (!_client.IsDemo) file.DropDownItems.Add(new ToolStripMenuItem("로그아웃", null, async (_, _) => { await _client.LogoutAsync(); DialogResult = DialogResult.Retry; Close(); }));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("끝내기", null, (_, _) => Close()));

        var run = new ToolStripMenuItem("작업");
        foreach (var m in new[] { ModuleIds.Clean, ModuleIds.Shutdown, ModuleIds.Errors, ModuleIds.Update, ModuleIds.Deploy, ModuleIds.Health, ModuleIds.Inspect, ModuleIds.NetDiag, ModuleIds.ClassMode })
        {
            var id = m;
            run.DropDownItems.Add(new ToolStripMenuItem(ModuleIds.KoreanName(m) + "…", null, (_, _) => RunModule(id)) { Enabled = Can(Permission.RunModules) });
        }

        var admin = new ToolStripMenuItem("관리");
        admin.DropDownItems.Add(new ToolStripMenuItem("계정 관리…", null, (_, _) => { using var f = new AccountsForm(_client); f.ShowDialog(this); }) { Enabled = Can(Permission.ManageAccounts) });
        admin.DropDownItems.Add(new ToolStripMenuItem("PC 그룹 관리…", null, async (_, _) => await ManageGroupsAsync()) { Enabled = Can(Permission.ManageGroups) });
        admin.DropDownItems.Add(new ToolStripMenuItem("그룹별 정책 (자동 종료·정리)…", null, (_, _) => OpenGroupPolicy()));
        admin.DropDownItems.Add(new ToolStripMenuItem("서버 설정…", null, (_, _) => { using var f = new ServerSettingsForm(_client); f.ShowDialog(this); }) { Enabled = Can(Permission.ManageServerSettings) });
        admin.DropDownItems.Add(new ToolStripSeparator());
        admin.DropDownItems.Add(new ToolStripMenuItem("내 비밀번호 변경…", null, async (_, _) => await ChangeOwnPasswordAsync()));

        var view = new ToolStripMenuItem("보기");
        view.DropDownItems.Add(new ToolStripMenuItem("이 PC에서 LED 점검 화면 열기", null, (_, _) => { using var f = new LedTestForm(); f.ShowDialog(this); }));
        foreach (var (text, open) in _options.ExtraViewMenu)
        {
            var o = open;
            view.DropDownItems.Add(new ToolStripMenuItem(text, null, (_, _) => o(this)));
        }

        _aboutItem.Click += (_, _) => { using var d = new AboutDialog(Font); d.ShowDialog(this); };
        _menu.Items.AddRange([file, run, admin, view, _aboutItem]); // About is always the last top-level item
    }

    // ---------- tabs ----------
    private void BuildPcTab()
    {
        var page = new TabPage("PC 목록");
        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4), ImageScalingSize = new Size(16, 16) };
        var runBtn = new ToolStripButton("▶ 선택한 PC에 작업 실행") { Enabled = Can(Permission.RunModules) };
        runBtn.Click += (_, _) => RunModule(null);
        var move = new ToolStripButton("그룹 이동") { Enabled = Can(Permission.ManageGroups) };
        move.Click += async (_, _) => await MovePcsAsync();
        var del = new ToolStripButton("PC 삭제") { Enabled = Can(Permission.ManageGroups) };
        del.Click += async (_, _) => await DeletePcsAsync();
        var all = new ToolStripButton("전체 선택");
        all.Click += (_, _) => _grid.SelectAll();
        bar.Items.AddRange([runBtn, new ToolStripSeparator(), all, move, del]);

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = true;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.BorderStyle = BorderStyle.None;
        _grid.Font = Ui.Font;
        foreach (var (name, header, weight) in new[] {
            ("state", "상태", 60), ("name", "PC 이름", 120), ("group", "그룹", 100), ("ip", "IP", 100), ("user", "사용자", 100),
            ("free", "남은 공간", 70), ("updates", "업데이트", 70), ("errors", "오류", 50), ("reboot", "재부팅", 60), ("seen", "마지막 연결", 110), ("ver", "버전", 50) })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = weight, SortMode = DataGridViewColumnSortMode.Automatic });
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) RunModule(null); };

        page.Controls.Add(_grid);
        page.Controls.Add(bar);
        Tabs.TabPages.Add(page);
    }

    private void BuildResultsTab()
    {
        var page = new TabPage("작업 결과");
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 220 };
        split.Panel1.Controls.Add(_batches);
        var lower = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 640 };
        lower.Panel1.Controls.Add(_rows);
        lower.Panel2.Controls.Add(_rowDetail);
        split.Panel2.Controls.Add(lower);
        page.Controls.Add(split);
        Tabs.TabPages.Add(page);
        _batches.SelectedIndexChanged += async (_, _) => await LoadBatchAsync();
        _rows.SelectedIndexChanged += (_, _) =>
        {
            if (_rows.SelectedItems.Count == 0) return;
            var r = (CommandRow)_rows.SelectedItems[0].Tag!;
            _rowDetail.Text = $"{r.PcName} · {ModuleIds.KoreanName(r.Module)} · {StatusText(r.Status)}\r\n{r.Summary}\r\n\r\n" +
                              ReportFormatter.Format(r.Module, r.DataJson).ReplaceLineEndings("\r\n");
        };
    }

    private void BuildReportsTab()
    {
        var page = new TabPage("보고서");
        foreach (var (_, label) in ReportModules) _reportModule.Items.Add(label);
        _reportModule.SelectedIndex = 0;
        _reportModule.SelectedIndexChanged += async (_, _) => await LoadReportsAsync();
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        bar.Controls.AddRange([Ui.Label("항목"), _reportModule, Ui.Button("새로 고침", async (_, _) => await LoadReportsAsync(), 90),
            Ui.Button("표로 내보내기 (CSV)", (_, _) => ExportReports(), 150)]);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 700 };
        split.Panel1.Controls.Add(_reportList);
        split.Panel2.Controls.Add(_reportDetail);
        page.Controls.Add(split);
        page.Controls.Add(bar);
        Tabs.TabPages.Add(page);
        _reportList.SelectedIndexChanged += (_, _) =>
        {
            if (_reportList.SelectedItems.Count == 0) return;
            var r = (ReportRow)_reportList.SelectedItems[0].Tag!;
            _reportDetail.Text = $"{r.PcName} · {r.FinishedAt.ToLocalTime():yyyy-MM-dd HH:mm}\r\n{r.Summary}\r\n\r\n" + ReportFormatter.Format(r.Module, r.DataJson).ReplaceLineEndings("\r\n");
        };
    }

    private void BuildPresetsTab()
    {
        var page = new TabPage("공용 프리셋");
        var intro = Ui.Label("관리자가 만든 공용 프리셋은 모든 PC의 트레이 메뉴에 나타납니다. ‘명령 실행’ 동작은 공용 프리셋에만 넣을 수 있으며 서버 키로 서명되어 배포됩니다.");
        intro.Dock = DockStyle.Top;
        intro.Padding = new Padding(8);
        intro.MaximumSize = new Size(900, 0);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        var edit = Ui.Button("공용 프리셋 편집…", async (_, _) => await EditSharedPresetsAsync(), 150);
        edit.Enabled = Can(Permission.ManagePresets);
        bar.Controls.AddRange([edit, _presetInfo]);
        page.Controls.Add(_presetList);
        page.Controls.Add(bar);
        page.Controls.Add(intro);
        Tabs.TabPages.Add(page);
    }

    private void BuildPackagesTab()
    {
        var page = new TabPage("배포 패키지");
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        var add = Ui.Button("설치 파일 추가…", async (_, _) => await AddPackageAsync(), 130);
        var del = Ui.Button("삭제", async (_, _) => await DeletePackageAsync(), 70);
        add.Enabled = del.Enabled = Can(Permission.ManagePackages);
        bar.Controls.AddRange([add, del, Ui.Label("설치 파일(.exe/.msi)은 관리 PC에 저장되고, 각 PC는 학교 네트워크로만 내려받습니다. 실행 전 SHA-256을 확인합니다.", color: Ui.Muted)]);
        page.Controls.Add(_packages);
        page.Controls.Add(bar);
        Tabs.TabPages.Add(page);
    }

    private void BuildAuditTab()
    {
        var page = new TabPage("감사 로그");
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        bar.Controls.AddRange([Ui.Button("새로 고침", async (_, _) => await LoadAuditAsync(), 90), Ui.Button("내보내기 (CSV)", (_, _) => ExportAudit(), 120)]);
        page.Controls.Add(_audit);
        page.Controls.Add(bar);
        Tabs.TabPages.Add(page);
    }

    // ---------- data ----------
    internal async Task RefreshAllAsync(bool quiet)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            _groups = await _client.GetGroupsAsync();
            _pcs = await _client.GetPcsAsync();
            FillTree();
            FillGrid();
            await RefreshCurrentTabAsync();
            _status.Text = $"PC {_pcs.Count}대 · 켜짐 {_pcs.Count(p => p.Online)}대 · 마지막 새로 고침 {DateTime.Now:HH:mm:ss}";
        }
        catch (GacClientException e)
        {
            _status.Text = "서버 연결 실패: " + e.Message;
            if (e.SessionExpired) { _refresh.Stop(); Ui.Error(this, e.Message); DialogResult = DialogResult.Retry; Close(); }
            else if (!quiet) Ui.Error(this, e.Message);
        }
        finally { _busy = false; }
    }

    private async Task RefreshCurrentTabAsync()
    {
        try
        {
            switch (Tabs.SelectedTab?.Text)
            {
                case "작업 결과": await LoadBatchesAsync(); break;
                case "보고서": await LoadReportsAsync(); break;
                case "공용 프리셋": await LoadSharedPresetsAsync(); break;
                case "배포 패키지": await LoadPackagesAsync(); break;
                case "감사 로그": await LoadAuditAsync(); break;
            }
        }
        catch (GacClientException e) { _status.Text = e.Message; }
    }

    private void FillTree()
    {
        var selected = _tree.SelectedNode?.Name ?? AllPcsNode;
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        _tree.Nodes.Add(AllPcsNode, $"전체 PC ({_pcs.Count})");
        foreach (var g in _groups) _tree.Nodes.Add(g.Id.ToString(), $"{g.Name} ({_pcs.Count(p => p.GroupId == g.Id)})").Tag = g;
        var none = _pcs.Count(p => p.GroupId == null);
        if (none > 0) _tree.Nodes.Add(NoGroupNode, $"그룹 없음 ({none})");
        _tree.SelectedNode = _tree.Nodes[selected] ?? _tree.Nodes[0];
        _tree.EndUpdate();
    }

    private IEnumerable<PcSummary> VisiblePcs() => _tree.SelectedNode?.Name switch
    {
        null or AllPcsNode => _pcs,
        NoGroupNode => _pcs.Where(p => p.GroupId == null),
        var id => _pcs.Where(p => p.GroupId?.ToString() == id),
    };

    private void FillGrid()
    {
        var selected = SelectedPcs().Select(p => p.Id).ToHashSet();
        _grid.Rows.Clear();
        foreach (var p in VisiblePcs())
        {
            var i = _grid.Rows.Add(p.Online ? "● 켜짐" : "○ 꺼짐", p.Name, p.GroupName ?? "-", p.IpAddress ?? "-", p.LoggedOnUser ?? "-",
                p.MinFreePercent is { } f ? $"{f:0}%" : "-", p.PendingUpdates?.ToString() ?? "-", p.RecentErrors?.ToString() ?? "-",
                p.RebootRequired ? "필요" : "", p.LastSeen?.ToLocalTime().ToString("MM-dd HH:mm") ?? "-", p.AgentVersion ?? "");
            var row = _grid.Rows[i];
            row.Tag = p;
            row.Cells["state"].Style.ForeColor = p.Online ? Ui.Ok : Ui.Muted;
            if (p.MinFreePercent is < 10) row.Cells["free"].Style.ForeColor = Ui.Bad;
            if (p.PendingUpdates is > 0) row.Cells["updates"].Style.ForeColor = Ui.Warn;
            if (p.RecentErrors is > 0) row.Cells["errors"].Style.ForeColor = Ui.Bad;
            if (p.RebootRequired) row.Cells["reboot"].Style.ForeColor = Ui.Warn;
            row.Selected = selected.Contains(p.Id);
        }
    }

    internal List<PcSummary> SelectedPcs() => _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => (PcSummary)r.Tag!).ToList();

    private async Task LoadBatchesAsync()
    {
        var keep = _batches.SelectedItems.Count > 0 ? ((CommandBatchSummary)_batches.SelectedItems[0].Tag!).BatchId : (Guid?)null;
        var list = await _client.GetBatchesAsync(200);
        _batches.BeginUpdate();
        _batches.Items.Clear();
        foreach (var b in list)
        {
            var item = new ListViewItem([b.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss"), $"{ModuleIds.KoreanName(b.Module)} · {ActionText(b.Module, b.Action)}", b.CreatedBy,
                $"{b.Done}/{b.Total}", b.Failed > 0 ? b.Failed.ToString() : ""]) { Tag = b };
            if (b.Failed > 0) item.ForeColor = Ui.Bad;
            else if (b.Done < b.Total) item.ForeColor = Ui.Accent;
            _batches.Items.Add(item);
            if (b.BatchId == keep) item.Selected = true;
        }
        _batches.EndUpdate();
        if (keep != null) await LoadBatchAsync();
    }

    private async Task LoadBatchAsync()
    {
        if (_batches.SelectedItems.Count == 0) return;
        var b = (CommandBatchSummary)_batches.SelectedItems[0].Tag!;
        IReadOnlyList<CommandRow> rows;
        try { rows = await _client.GetBatchAsync(b.BatchId); } catch (GacClientException e) { _status.Text = e.Message; return; }
        var keep = _rows.SelectedItems.Count > 0 ? ((CommandRow)_rows.SelectedItems[0].Tag!).Id : (Guid?)null;
        _rows.BeginUpdate();
        _rows.Items.Clear();
        foreach (var r in rows)
        {
            var item = new ListViewItem([r.PcName, StatusText(r.Status), r.Summary ?? "", r.FinishedAt?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? ""]) { Tag = r };
            item.ForeColor = r.Status switch { CommandStatus.Succeeded => Ui.Ok, CommandStatus.Failed or CommandStatus.Rejected or CommandStatus.Expired => Ui.Bad, _ => Ui.Muted };
            _rows.Items.Add(item);
            if (r.Id == keep) item.Selected = true;
        }
        _rows.EndUpdate();
    }

    private async Task LoadReportsAsync()
    {
        var module = ReportModules[_reportModule.SelectedIndex].Module;
        _reports = module is ModuleIds.Shutdown or ModuleIds.Deploy or ModuleIds.NetDiag or ModuleIds.Clean
            ? await _client.GetReportsAsync(module, null, 500)
            : await _client.GetLatestAsync(module);
        _reportList.BeginUpdate();
        _reportList.Items.Clear();
        foreach (var r in _reports)
        {
            var warn = module == ModuleIds.Health && r.Summary.StartsWith('⚠');
            var item = new ListViewItem([r.PcName, r.Success ? (warn ? "주의" : "정상") : "실패", r.Summary, r.FinishedAt.ToLocalTime().ToString("MM-dd HH:mm")]) { Tag = r };
            item.ForeColor = !r.Success ? Ui.Bad : warn ? Ui.Warn : SystemColors.ControlText;
            _reportList.Items.Add(item);
        }
        _reportList.EndUpdate();
        _reportDetail.Clear();
    }

    private async Task LoadSharedPresetsAsync()
    {
        _sharedPresets = await _client.GetSharedPresetsAsync();
        var file = GacJson.Deserialize<PresetFile>(_sharedPresets.PresetsJson) ?? new PresetFile();
        _presetList.Items.Clear();
        foreach (var p in file.Presets)
            _presetList.Items.Add(new ListViewItem([p.Name, p.Trigger == PresetTrigger.Login ? "로그인 시 자동" : "수동", p.Actions.Count.ToString()]));
        _presetInfo.Text = _sharedPresets.UpdatedAt is { } t ? $"마지막 수정: {t.ToLocalTime():yyyy-MM-dd HH:mm} ({_sharedPresets.UpdatedBy})" : "아직 공용 프리셋이 없습니다.";
    }

    private async Task LoadPackagesAsync()
    {
        var list = await _client.GetPackagesAsync();
        _packages.BeginUpdate();
        _packages.Items.Clear();
        foreach (var p in list)
            _packages.Items.Add(new ListViewItem([p.Name, p.FileName, ReportFormatter.Bytes(p.SizeBytes), p.SilentArgs, p.Sha256[..Math.Min(16, p.Sha256.Length)] + "…",
                $"{p.UploadedAt.ToLocalTime():yyyy-MM-dd} {p.UploadedBy}"]) { Tag = p });
        _packages.EndUpdate();
    }

    private async Task LoadAuditAsync()
    {
        var list = await _client.GetAuditAsync(3000);
        _audit.BeginUpdate();
        _audit.Items.Clear();
        foreach (var a in list)
            _audit.Items.Add(new ListViewItem([a.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), a.Actor, a.Action, a.Target ?? "", a.Detail ?? ""]) { Tag = a });
        _audit.EndUpdate();
    }

    // ---------- actions ----------
    internal void RunModule(string? module)
    {
        if (!Can(Permission.RunModules)) { Ui.Error(this, "이 계정은 작업을 실행할 수 없습니다 (조회 전용)."); return; }
        var targets = SelectedPcs();
        if (targets.Count == 0) { Ui.Error(this, "먼저 PC 목록에서 PC를 선택하세요. (Ctrl/Shift로 여러 대 선택)"); return; }
        using var dlg = new RunModuleDialog(_client, targets, _me, module);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _status.Text = $"PC {dlg.Result!.Count}대에 작업을 보냈습니다. 결과는 [작업 결과] 탭에서 확인하세요.";
        Tabs.SelectedIndex = 1;
    }

    private async Task MovePcsAsync()
    {
        var pcs = SelectedPcs();
        if (pcs.Count == 0) { Ui.Error(this, "이동할 PC를 선택하세요."); return; }
        using var dlg = new Form();
        Ui.Apply(dlg, "그룹 이동", new Size(320, 130), resizable: false);
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Left = 20, Top = 20 };
        combo.Items.Add("(그룹 없음)");
        foreach (var g in _groups) combo.Items.Add(g.Name);
        combo.SelectedIndex = 0;
        dlg.Controls.Add(combo);
        dlg.Controls.Add(Ui.ButtonBar(Ui.Button("취소", (_, _) => dlg.DialogResult = DialogResult.Cancel, 70), Ui.Button("이동", (_, _) => dlg.DialogResult = DialogResult.OK, 70)));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        int? gid = combo.SelectedIndex == 0 ? null : _groups[combo.SelectedIndex - 1].Id;
        try { await _client.MovePcsAsync(new MovePcRequest(pcs.Select(p => p.Id).ToList(), gid)); await RefreshAllAsync(false); }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }

    private async Task DeletePcsAsync()
    {
        var pcs = SelectedPcs();
        if (pcs.Count == 0 || !Ui.Confirm(this, $"PC {pcs.Count}대를 목록에서 삭제할까요?\n(PC의 에이전트를 지우지는 않습니다. 다시 등록하면 새 PC로 나타납니다.)")) return;
        try { foreach (var p in pcs) await _client.DeletePcAsync(p.Id); await RefreshAllAsync(false); }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }

    private async Task ManageGroupsAsync()
    {
        using var dlg = new Form();
        Ui.Apply(dlg, "PC 그룹 관리", new Size(380, 360));
        var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        void Fill() { list.Items.Clear(); foreach (var g in _groups) list.Items.Add($"{g.Name} ({g.PcCount}대)"); }
        Fill();
        async Task Reload() { _groups = await _client.GetGroupsAsync(); Fill(); }
        string? Ask(string title, string initial)
        {
            using var f = new Form();
            Ui.Apply(f, title, new Size(320, 120), resizable: false);
            var t = new TextBox { Text = initial, Left = 16, Top = 16, Width = 280 };
            f.Controls.Add(t);
            f.Controls.Add(Ui.ButtonBar(Ui.Button("취소", (_, _) => f.DialogResult = DialogResult.Cancel, 70), Ui.Button("확인", (_, _) => f.DialogResult = DialogResult.OK, 70)));
            return f.ShowDialog(dlg) == DialogResult.OK ? t.Text : null;
        }
        var bar = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 120, FlowDirection = FlowDirection.TopDown, Padding = new Padding(6) };
        bar.Controls.AddRange([
            Ui.Button("추가", async (_, _) => { if (Ask("그룹 추가", "") is { } n) try { await _client.CreateGroupAsync(n); await Reload(); } catch (GacClientException e) { Ui.Error(dlg, e.Message); } }, 100),
            Ui.Button("이름 바꾸기", async (_, _) => { if (list.SelectedIndex >= 0 && Ask("이름 바꾸기", _groups[list.SelectedIndex].Name) is { } n) try { await _client.RenameGroupAsync(_groups[list.SelectedIndex].Id, n); await Reload(); } catch (GacClientException e) { Ui.Error(dlg, e.Message); } }, 100),
            Ui.Button("삭제", async (_, _) => { if (list.SelectedIndex >= 0 && Ui.Confirm(dlg, "그룹을 삭제할까요? 그룹의 PC는 ‘그룹 없음’으로 옮겨집니다.")) try { await _client.DeleteGroupAsync(_groups[list.SelectedIndex].Id); await Reload(); } catch (GacClientException e) { Ui.Error(dlg, e.Message); } }, 100),
        ]);
        dlg.Controls.Add(list);
        dlg.Controls.Add(bar);
        dlg.Controls.Add(Ui.ButtonBar(Ui.Button("닫기", (_, _) => dlg.Close(), 80)));
        dlg.ShowDialog(this);
        await RefreshAllAsync(false);
    }

    private void OpenGroupPolicy()
    {
        var group = _tree.SelectedNode?.Tag as PcGroup ?? (_groups.Count == 1 ? _groups[0] : null);
        if (group == null) { Ui.Error(this, "왼쪽에서 그룹을 먼저 선택하세요. (정책은 그룹마다 따로 정합니다)"); return; }
        using var f = new GroupPolicyForm(_client, group, Can(Permission.ManagePolicies));
        f.ShowDialog(this);
    }

    private async Task ChangeOwnPasswordAsync()
    {
        using var dlg = new PasswordDialog("내 비밀번호 변경", askCurrent: true);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { await _client.ChangePasswordAsync(new ChangePasswordRequest(dlg.CurrentPassword, dlg.Password)); Ui.Info(this, "비밀번호를 바꿨습니다."); }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }

    private async Task EditSharedPresetsAsync()
    {
        var info = _sharedPresets ?? await _client.GetSharedPresetsAsync();
        var file = GacJson.Deserialize<PresetFile>(info.PresetsJson) ?? new PresetFile();
        using var editor = new PresetEditorForm(file, allowRunCommand: true, async f =>
        {
            try { await _client.SaveSharedPresetsAsync(GacJson.Serialize(f)); return null; }
            catch (GacClientException e) { return e.Message; }
        }, run: null, "공용 프리셋 편집 (모든 PC에 배포)");
        editor.ShowDialog(this);
        await LoadSharedPresetsAsync();
    }

    private async Task AddPackageAsync()
    {
        using var ofd = new OpenFileDialog { Filter = "설치 파일 (*.exe;*.msi)|*.exe;*.msi", Title = "배포할 설치 파일 선택" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        using var dlg = new Form();
        Ui.Apply(dlg, "설치 파일 추가", new Size(460, 190), resizable: false);
        var name = new TextBox { Text = Path.GetFileNameWithoutExtension(ofd.FileName), Width = 300 };
        var args = new TextBox { Width = 300, PlaceholderText = ofd.FileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? "비워 두면 /qn /norestart" : "예: /S 또는 /quiet /norestart" };
        var grid = Ui.Grid();
        Ui.AddRow(grid, "파일", Ui.Label(Path.GetFileName(ofd.FileName)));
        Ui.AddRow(grid, "이름", name);
        Ui.AddRow(grid, "무인 설치 인수", args);
        dlg.Controls.Add(grid);
        dlg.Controls.Add(Ui.ButtonBar(Ui.Button("취소", (_, _) => dlg.DialogResult = DialogResult.Cancel, 70), Ui.Button("등록", (_, _) => dlg.DialogResult = DialogResult.OK, 70)));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        UseWaitCursor = true;
        try
        {
            var progress = new Progress<double>(p => _status.Text = $"업로드 중… {p:P0}");
            await _client.UploadPackageAsync(name.Text.Trim(), ofd.FileName, args.Text.Trim(), progress, CancellationToken.None);
            _status.Text = "설치 파일을 등록했습니다.";
            await LoadPackagesAsync();
        }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
        catch (IOException e) { Ui.Error(this, "파일을 읽을 수 없습니다: " + e.Message); }
        finally { UseWaitCursor = false; }
    }

    private async Task DeletePackageAsync()
    {
        if (_packages.SelectedItems.Count == 0) return;
        var p = (DeployPackage)_packages.SelectedItems[0].Tag!;
        if (!Ui.Confirm(this, $"'{p.Name}' 설치 파일을 삭제할까요?")) return;
        try { await _client.DeletePackageAsync(p.Id); await LoadPackagesAsync(); }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }

    private void ExportReports()
    {
        var module = ReportModules[_reportModule.SelectedIndex];
        var rows = new List<string[]>();
        if (module.Module == ModuleIds.Inspect)
        {
            // Inspection results as a checklist table (one column per item).
            var parsed = _reports.Select(r => (r, rep: r.DataJson != null && r.DataJson.TrimStart().StartsWith('{') ? GacJson.Deserialize<InspectReport>(r.DataJson) : null)).ToList();
            var items = parsed.Where(x => x.rep != null).SelectMany(x => x.rep!.Items.Select(i => i.Name)).Distinct().ToList();
            rows.Add(["PC", "시각", "Windows", .. items, "허용 목록 밖 프로그램"]);
            foreach (var (r, rep) in parsed)
                rows.Add([r.PcName, r.FinishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), $"{rep?.WindowsEdition} {rep?.WindowsVersion}",
                    .. items.Select(n => rep?.Items.FirstOrDefault(i => i.Name == n) is { } i ? (i.Passed switch { true => "합격", false => "불합격", null => "미실시" }) + " " + i.Detail : ""),
                    string.Join("; ", rep?.UnwantedPrograms.Select(p => p.Name) ?? [])]);
        }
        else
        {
            rows.Add(["PC", "결과", "요약", "시각"]);
            rows.AddRange(_reports.Select(r => new[] { r.PcName, r.Success ? "성공" : "실패", r.Summary, r.FinishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") }));
        }
        SaveCsv($"GAC_{module.Label}_{DateTime.Now:yyyyMMdd_HHmm}.csv", rows);
    }

    private void ExportAudit()
    {
        var rows = new List<string[]> { new[] { "시각", "누가", "무엇을", "대상", "내용" } };
        rows.AddRange(_audit.Items.Cast<ListViewItem>().Select(i => i.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text).ToArray()));
        SaveCsv($"GAC_감사로그_{DateTime.Now:yyyyMMdd_HHmm}.csv", rows);
    }

    private void SaveCsv(string fileName, List<string[]> rows)
    {
        if (!_options.AllowFileExport) { Ui.Info(this, $"체험 모드에서는 파일로 저장하지 않습니다.\n(실제 제품에서는 {rows.Count - 1}행이 CSV 파일로 저장됩니다.)"); return; }
        using var sfd = new SaveFileDialog { FileName = fileName, Filter = "CSV 파일 (*.csv)|*.csv" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(sfd.FileName, ReportFormatter.ToCsv(rows), new System.Text.UTF8Encoding(true));
        _status.Text = "저장했습니다: " + sfd.FileName;
    }

    internal static string StatusText(CommandStatus s) => s switch
    {
        CommandStatus.Pending => "대기 (PC 꺼짐)",
        CommandStatus.Delivered => "실행 중",
        CommandStatus.Succeeded => "성공",
        CommandStatus.Failed => "실패",
        CommandStatus.Expired => "만료",
        CommandStatus.Rejected => "거부됨",
        _ => s.ToString(),
    };

    internal static string ActionText(string module, string action) => (module, action) switch
    {
        (ModuleIds.Clean, _) => "정리",
        (ModuleIds.Shutdown, ModuleActions.ShutdownNow) => "종료",
        (ModuleIds.Shutdown, ModuleActions.ShutdownCancel) => "종료 취소",
        (ModuleIds.Errors, _) => "수집",
        (ModuleIds.Update, ModuleActions.UpdateScan) => "확인",
        (ModuleIds.Update, ModuleActions.UpdateInstall) => "설치",
        (ModuleIds.Deploy, ModuleActions.DeployInstall) => "패키지 설치",
        (ModuleIds.Deploy, ModuleActions.DeployRunScript) => "명령 실행",
        (ModuleIds.Health, _) => "보고",
        (ModuleIds.Inspect, ModuleActions.InspectRun) => "점검",
        (ModuleIds.Inspect, ModuleActions.InspectUninstall) => "프로그램 제거",
        (ModuleIds.NetDiag, _) => "진단",
        (ModuleIds.ClassMode, _) => "동기화",
        _ => action,
    };
}
