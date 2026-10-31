using Gac.Shared;
using Gac.Shared.ClassMode;

namespace Gac.Tray.UI;

/// <summary>
/// Class-mode preset editor (CLAUDE.md 4.3). Used by the tray (teacher presets), the console (shared presets, which may
/// contain run_command) and the demo. Teachers can only pick actions from the fixed list.
/// </summary>
public sealed class PresetEditorForm : Form
{
    private readonly PresetFile _file;
    private readonly bool _allowRunCommand;
    private readonly Func<PresetFile, Task<string?>> _save;
    private readonly Func<ClassPreset, Task>? _run;
    private readonly ListBox _presets = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _name = new() { Width = 260 };
    private readonly ComboBox _trigger = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly ListView _actions = Ui.List(("순서", 50), ("동작", 380));
    private readonly Panel _detail = new() { Dock = DockStyle.Fill };
    private bool _loading;
    private bool _dirty;

    public PresetEditorForm(PresetFile file, bool allowRunCommand, Func<PresetFile, Task<string?>> save, Func<ClassPreset, Task>? run, string title)
    {
        _file = GacJson.Deserialize<PresetFile>(GacJson.Serialize(file)) ?? new PresetFile(); // edit a copy
        _allowRunCommand = allowRunCommand;
        _save = save;
        _run = run;
        Ui.Apply(this, title, new Size(820, 520));

        _trigger.Items.AddRange(["수동 실행 (트레이 메뉴)", "로그인할 때 자동 실행"]);

        var left = new Panel { Dock = DockStyle.Left, Width = 230, Padding = new Padding(8) };
        left.Controls.Add(_presets);
        left.Controls.Add(Ui.Label("프리셋 목록", Ui.Bold));
        left.Controls[1].Dock = DockStyle.Top;
        var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        leftButtons.Controls.AddRange([Ui.Button("새 프리셋", (_, _) => AddPreset(), 96), Ui.Button("복제", (_, _) => Duplicate(), 60), Ui.Button("삭제", (_, _) => DeletePreset(), 60)]);
        left.Controls.Add(leftButtons);

        var grid = Ui.Grid();
        Ui.AddRow(grid, "이름", _name);
        Ui.AddRow(grid, "실행 시점", _trigger);

        var actionButtons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 130, FlowDirection = FlowDirection.TopDown, Padding = new Padding(6, 0, 0, 0) };
        actionButtons.Controls.AddRange([
            Ui.Button("동작 추가", (_, _) => AddAction()), Ui.Button("수정", (_, _) => EditAction()), Ui.Button("삭제", (_, _) => RemoveAction()),
            Ui.Button("위로", (_, _) => MoveAction(-1)), Ui.Button("아래로", (_, _) => MoveAction(1)),
        ]);
        var actionsBox = new GroupBox { Text = "동작 (위에서부터 차례로 실행)", Dock = DockStyle.Fill, Padding = new Padding(8) };
        actionsBox.Controls.Add(_actions);
        actionsBox.Controls.Add(actionButtons);
        _detail.Controls.Add(actionsBox);
        _detail.Controls.Add(grid);
        _detail.Padding = new Padding(4, 8, 8, 0);

        var close = Ui.Button("닫기", (_, _) => Close(), 80);
        var saveButton = Ui.Button("저장", async (_, _) => await SaveAsync(), 80);
        var bar = Ui.ButtonBar(close, saveButton);
        if (_run != null) bar.Controls.Add(Ui.Button("지금 실행해 보기", async (_, _) => await RunSelectedAsync(), 130));

        Controls.Add(_detail);
        Controls.Add(left);
        Controls.Add(bar);
        AcceptButton = null;
        CancelButton = close;

        _presets.SelectedIndexChanged += (_, _) => ShowSelected();
        _name.TextChanged += (_, _) => { if (!_loading && Current is { } p) { p.Name = _name.Text; _dirty = true; RefreshList(keepSelection: true); } };
        _trigger.SelectedIndexChanged += (_, _) => { if (!_loading && Current is { } p) { p.Trigger = (PresetTrigger)_trigger.SelectedIndex; _dirty = true; } };
        _actions.DoubleClick += (_, _) => EditAction();
        FormClosing += (_, e) =>
        {
            if (_dirty && !Ui.Confirm(this, "저장하지 않은 변경 내용이 있습니다. 저장하지 않고 닫을까요?")) e.Cancel = true;
        };

        RefreshList(keepSelection: false);
        if (_file.Presets.Count > 0) _presets.SelectedIndex = 0; else ShowSelected();
    }

    private ClassPreset? Current => _presets.SelectedIndex >= 0 && _presets.SelectedIndex < _file.Presets.Count ? _file.Presets[_presets.SelectedIndex] : null;

    private void RefreshList(bool keepSelection)
    {
        var idx = _presets.SelectedIndex;
        _loading = true;
        _presets.BeginUpdate();
        _presets.Items.Clear();
        foreach (var p in _file.Presets) _presets.Items.Add((string.IsNullOrWhiteSpace(p.Name) ? "(이름 없음)" : p.Name) + (p.Trigger == PresetTrigger.Login ? "  [로그인 시]" : ""));
        _presets.EndUpdate();
        if (keepSelection && idx < _presets.Items.Count) _presets.SelectedIndex = idx;
        _loading = false;
    }

    private void ShowSelected()
    {
        if (_loading) return;
        var p = Current;
        _detail.Enabled = p != null;
        _loading = true;
        _name.Text = p?.Name ?? "";
        _trigger.SelectedIndex = p == null ? -1 : (int)p.Trigger;
        _loading = false;
        RefreshActions();
    }

    private void RefreshActions(int select = -1)
    {
        _actions.BeginUpdate();
        _actions.Items.Clear();
        var p = Current;
        if (p != null)
            for (var i = 0; i < p.Actions.Count; i++)
                _actions.Items.Add(new ListViewItem([(i + 1).ToString(), p.Actions[i].Describe()]));
        if (select >= 0 && select < _actions.Items.Count) { _actions.Items[select].Selected = true; _actions.Items[select].EnsureVisible(); }
        _actions.EndUpdate();
    }

    private void AddPreset()
    {
        if (_file.Presets.Count >= PresetValidator.MaxPresets) { Ui.Error(this, $"프리셋은 최대 {PresetValidator.MaxPresets}개까지 만들 수 있습니다."); return; }
        _file.Presets.Add(new ClassPreset { Name = "새 프리셋" });
        _dirty = true;
        RefreshList(false);
        _presets.SelectedIndex = _file.Presets.Count - 1;
        _name.Focus();
        _name.SelectAll();
    }

    private void Duplicate()
    {
        if (Current is not { } p) return;
        var copy = GacJson.Deserialize<ClassPreset>(GacJson.Serialize(p))!;
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = (p.Name + " 복사본").Length > PresetValidator.MaxNameLength ? p.Name : p.Name + " 복사본";
        _file.Presets.Add(copy);
        _dirty = true;
        RefreshList(false);
        _presets.SelectedIndex = _file.Presets.Count - 1;
    }

    private void DeletePreset()
    {
        if (Current is not { } p || !Ui.Confirm(this, $"'{p.Name}' 프리셋을 삭제할까요?")) return;
        _file.Presets.Remove(p);
        _dirty = true;
        RefreshList(false);
        if (_file.Presets.Count > 0) _presets.SelectedIndex = 0; else ShowSelected();
    }

    private int SelectedAction => _actions.SelectedIndices.Count > 0 ? _actions.SelectedIndices[0] : -1;

    private void AddAction()
    {
        if (Current is not { } p) return;
        using var dlg = new PresetActionDialog(null, _allowRunCommand);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        p.Actions.Add(dlg.Result!);
        _dirty = true;
        RefreshActions(p.Actions.Count - 1);
    }

    private void EditAction()
    {
        if (Current is not { } p || SelectedAction < 0) return;
        var i = SelectedAction;
        using var dlg = new PresetActionDialog(p.Actions[i], _allowRunCommand);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        p.Actions[i] = dlg.Result!;
        _dirty = true;
        RefreshActions(i);
    }

    private void RemoveAction()
    {
        if (Current is not { } p || SelectedAction < 0) return;
        var i = SelectedAction;
        p.Actions.RemoveAt(i);
        _dirty = true;
        RefreshActions(Math.Min(i, p.Actions.Count - 1));
    }

    private void MoveAction(int delta)
    {
        if (Current is not { } p || SelectedAction < 0) return;
        var i = SelectedAction;
        var j = i + delta;
        if (j < 0 || j >= p.Actions.Count) return;
        (p.Actions[i], p.Actions[j]) = (p.Actions[j], p.Actions[i]);
        _dirty = true;
        RefreshActions(j);
    }

    private async Task SaveAsync()
    {
        var result = PresetValidator.ValidateFile(_file, _allowRunCommand);
        if (!result.IsValid) { Ui.Error(this, "저장할 수 없습니다.\n\n" + result.ErrorText); return; }
        UseWaitCursor = true;
        try
        {
            var error = await _save(_file);
            if (error != null) { Ui.Error(this, error); return; }
            _dirty = false;
            Ui.Info(this, "저장했습니다.");
        }
        finally { UseWaitCursor = false; }
    }

    private async Task RunSelectedAsync()
    {
        if (Current is not { } p || _run == null) return;
        var v = PresetValidator.Validate(p, _allowRunCommand);
        if (!v.IsValid) { Ui.Error(this, v.ErrorText); return; }
        await _run(p);
    }
}

/// <summary>Add/edit one preset action. Only the fixed action list is offered.</summary>
public sealed class PresetActionDialog : Form
{
    private readonly ComboBox _type = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    private readonly TableLayoutPanel _fields = Ui.Grid();
    private readonly NumericUpDown _value = new() { Minimum = 0, Maximum = 100, Value = 50, Width = 100 };
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly NumericUpDown _width = new() { Minimum = 640, Maximum = 7680, Value = 1920, Width = 100 };
    private readonly NumericUpDown _height = new() { Minimum = 480, Maximum = 4320, Value = 1080, Width = 100 };
    private readonly TextBox _url = new() { Width = 360, PlaceholderText = "https://" };
    private readonly TextBox _path = new() { Width = 300, PlaceholderText = @"C:\Program Files\...\program.exe" };
    private readonly TextBox _args = new() { Width = 360 };
    private readonly TextBox _process = new() { Width = 200, PlaceholderText = "예: chrome" };
    private readonly NumericUpDown _seconds = new() { Minimum = 1, Maximum = 30, Value = 3, Width = 100 };
    private readonly TextBox _command = new() { Width = 360 };
    private readonly List<string> _types;

    public PresetAction? Result { get; private set; }

    public PresetActionDialog(PresetAction? existing, bool allowRunCommand)
    {
        Ui.Apply(this, existing == null ? "동작 추가" : "동작 수정", new Size(520, 300), resizable: false);
        _types = [.. PresetActionTypes.TeacherAllowed];
        if (allowRunCommand) _types.Add(PresetActionTypes.RunCommand);
        foreach (var t in _types) _type.Items.Add(PresetActionTypes.KoreanName(t));
        foreach (var m in PresetActionTypes.DisplayModes) _mode.Items.Add(PresetActionTypes.KoreanDisplayMode(m));
        _mode.SelectedIndex = 0;

        var top = Ui.Grid();
        Ui.AddRow(top, "동작 종류", _type);
        var ok = Ui.Button("확인", (_, _) => Accept(), 80);
        var cancel = Ui.Button("취소", (_, _) => { DialogResult = DialogResult.Cancel; }, 80);
        Controls.Add(_fields);
        Controls.Add(top);
        Controls.Add(Ui.ButtonBar(cancel, ok));
        AcceptButton = ok;
        CancelButton = cancel;

        _type.SelectedIndexChanged += (_, _) => BuildFields();
        if (existing != null)
        {
            var idx = _types.IndexOf(existing.Type);
            _type.SelectedIndex = Math.Max(0, idx);
            if (existing.Value is { } v) _value.Value = Math.Clamp(v, 0, 100);
            if (existing.Mode != null) _mode.SelectedIndex = Math.Max(0, PresetActionTypes.DisplayModes.ToList().IndexOf(existing.Mode));
            if (existing.Width is { } w) _width.Value = Math.Clamp(w, 640, 7680);
            if (existing.Height is { } h) _height.Value = Math.Clamp(h, 480, 4320);
            _url.Text = existing.Url ?? "";
            _path.Text = existing.Path ?? "";
            _args.Text = existing.Arguments ?? "";
            _process.Text = existing.ProcessName ?? "";
            if (existing.Seconds is { } s) _seconds.Value = Math.Clamp(s, 1, 30);
            _command.Text = existing.Command ?? "";
        }
        else _type.SelectedIndex = 0;
    }

    private string SelectedType => _types[Math.Max(0, _type.SelectedIndex)];

    private void BuildFields()
    {
        _fields.SuspendLayout();
        _fields.Controls.Clear();
        _fields.RowStyles.Clear();
        _fields.RowCount = 0;
        switch (SelectedType)
        {
            case PresetActionTypes.SetVolume: Ui.AddRow(_fields, "볼륨 (0~100)", _value); break;
            case PresetActionTypes.DisplayMode: Ui.AddRow(_fields, "화면 모드", _mode); break;
            case PresetActionTypes.SetResolution: Ui.AddRow(_fields, "가로", _width); Ui.AddRow(_fields, "세로", _height); break;
            case PresetActionTypes.OpenUrl: Ui.AddRow(_fields, "웹 주소", _url); break;
            case PresetActionTypes.LaunchApp:
                var browse = Ui.Button("찾아보기…", (_, _) => Browse(), 90);
                Ui.AddRow(_fields, "프로그램", Ui.Row(_path, browse));
                Ui.AddRow(_fields, "실행 인수 (선택)", _args);
                break;
            case PresetActionTypes.CloseApp: Ui.AddRow(_fields, "프로그램 이름", _process); break;
            case PresetActionTypes.Wait: Ui.AddRow(_fields, "대기 시간 (초, 최대 30)", _seconds); break;
            case PresetActionTypes.RunCommand: Ui.AddRow(_fields, "명령 (사용자 권한)", _command); break;
            default: _fields.Controls.Add(Ui.Label("추가 설정이 필요 없습니다.", color: Ui.Muted)); break;
        }
        _fields.ResumeLayout();
    }

    private void Browse()
    {
        using var ofd = new OpenFileDialog { Filter = "프로그램 (*.exe)|*.exe", Title = "실행할 프로그램 선택" };
        if (ofd.ShowDialog(this) == DialogResult.OK) _path.Text = ofd.FileName;
    }

    private void Accept()
    {
        var a = new PresetAction { Type = SelectedType };
        switch (a.Type)
        {
            case PresetActionTypes.SetVolume: a.Value = (int)_value.Value; break;
            case PresetActionTypes.DisplayMode: a.Mode = PresetActionTypes.DisplayModes[_mode.SelectedIndex]; break;
            case PresetActionTypes.SetResolution: a.Width = (int)_width.Value; a.Height = (int)_height.Value; break;
            case PresetActionTypes.OpenUrl: a.Url = _url.Text.Trim(); break;
            case PresetActionTypes.LaunchApp: a.Path = _path.Text.Trim(); a.Arguments = string.IsNullOrWhiteSpace(_args.Text) ? null : _args.Text.Trim(); break;
            case PresetActionTypes.CloseApp: a.ProcessName = _process.Text.Trim(); break;
            case PresetActionTypes.Wait: a.Seconds = (int)_seconds.Value; break;
            case PresetActionTypes.RunCommand: a.Command = _command.Text.Trim(); break;
        }
        var probe = new ClassPreset { Name = "확인", Actions = [a] };
        var v = PresetValidator.Validate(probe, allowRunCommand: a.Type == PresetActionTypes.RunCommand);
        if (!v.IsValid) { Ui.Error(this, string.Join("\n", v.Errors.Select(e => e.Replace("'확인' 1번 동작: ", "")))); return; }
        Result = a;
        DialogResult = DialogResult.OK;
    }
}
