using Gac.Console.Services;
using Gac.Shared.Contracts;
using Gac.Tray.UI;

namespace Gac.Console.UI;

/// <summary>
/// 캘린더 (CHANGE-002 §2.1): a month view with active/scheduled period presets as bars and lock exemption days
/// (잠금 해제일) as highlighted days. Clicking a period opens its details (targets, who started it, stop button).
/// Everyone with Console access can view; adding/editing/deleting exemption days needs "캘린더 관리".
/// </summary>
public sealed class CalendarView : UserControl
{
    private readonly IGacServerClient _client;
    private readonly bool _canEdit, _canStopPeriods;
    private readonly MonthGrid _grid = new() { Dock = DockStyle.Fill };
    private readonly Label _month = Ui.Label("", Ui.Title);
    private readonly ListView _list = Ui.List(("잠금 해제일", 150), ("반복", 170), ("다음 날짜", 100));
    private IReadOnlyList<ExemptionDay> _days = [];
    private IReadOnlyList<PeriodPresetInfo> _periods = [];

    public CalendarView(IGacServerClient client, bool canEdit, bool canStopPeriods)
    {
        _client = client;
        _canEdit = canEdit;
        _canStopPeriods = canStopPeriods;
        Dock = DockStyle.Fill;

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = false };
        var add = Ui.Button("잠금 해제일 추가…", async (_, _) => await EditAsync(null), 140);
        add.Enabled = canEdit;
        bar.Controls.AddRange([
            Ui.Button("◀", (_, _) => Move(-1), 40), _month, Ui.Button("▶", (_, _) => Move(1), 40), Ui.Button("오늘", (_, _) => { _grid.Month = FirstOfMonth(DateTime.Today); Render(); }, 60),
            add,
            Ui.Label("■ 잠금 해제일: 그날은 어떤 기간이든 PC 잠금(lock_pc)을 하지 않습니다.   ▬ 기간 프리셋 (빨강 = 잠금 포함)", color: Ui.Muted),
        ]);

        var right = new Panel { Dock = DockStyle.Right, Width = 440, Padding = new Padding(6) };
        var rightBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true };
        var edit = Ui.Button("편집…", async (_, _) => { if (Selected() is { } d) await EditAsync(d); }, 80);
        var del = Ui.Button("삭제", async (_, _) => await DeleteAsync(), 70);
        var skip = Ui.Button("이 회차 건너뛰기…", async (_, _) => { if (Selected() is { } d) await SkipAsync(d, null); }, 140);
        edit.Enabled = del.Enabled = skip.Enabled = canEdit;
        rightBar.Controls.AddRange([edit, del, skip]);
        var rightTitle = Ui.Label("잠금 해제일 목록", Ui.Bold);
        rightTitle.Dock = DockStyle.Top;
        right.Controls.Add(_list);
        right.Controls.Add(rightBar);
        right.Controls.Add(rightTitle);
        _list.DoubleClick += async (_, _) => { if (_canEdit && Selected() is { } d) await EditAsync(d); };

        _grid.Month = FirstOfMonth(DateTime.Today);
        _grid.PeriodClicked += (_, p) => ShowPeriod(p);
        _grid.DayClicked += async (_, d) => await DayClickedAsync(d);

        Controls.Add(_grid);
        Controls.Add(new Splitter { Dock = DockStyle.Right, Width = 4 });
        Controls.Add(right);
        Controls.Add(bar);
    }

    internal MonthGrid Grid => _grid;
    internal IReadOnlyList<ExemptionDay> Days => _days;

    private static DateOnly FirstOfMonth(DateTime d) => new(d.Year, d.Month, 1);

    private void Move(int months) { _grid.Month = _grid.Month.AddMonths(months); Render(); }

    private ExemptionDay? Selected() => _list.SelectedItems.Count == 0 ? null : (ExemptionDay)_list.SelectedItems[0].Tag!;

    public async Task LoadAsync()
    {
        _days = await _client.GetExemptionDaysAsync();
        _periods = await _client.GetPeriodsAsync();
        Render();
    }

    private void Render()
    {
        _month.Text = $"{_grid.Month.Year}년 {_grid.Month.Month}월";
        var from = _grid.Month.AddDays(-7);
        _grid.Exemptions = ExemptionCalendar.ExpandAll(_days, from, _grid.Month.AddMonths(1).AddDays(14));
        _grid.Periods = _periods;
        _grid.Invalidate();

        var today = DateOnly.FromDateTime(DateTime.Today);
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var d in _days.OrderBy(x => x.Name))
        {
            var next = ExemptionCalendar.Expand(d, today, today.AddYears(2)).Cast<DateOnly?>().FirstOrDefault();
            _list.Items.Add(new ListViewItem([d.Name, ExemptionCalendar.Describe(d), next?.ToString("yyyy-MM-dd") ?? "없음"]) { Tag = d });
        }
        _list.EndUpdate();
    }

    private async Task EditAsync(ExemptionDay? existing)
    {
        using var dlg = new ExemptionDayDialog(existing);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { await _client.SaveExemptionDayAsync(existing?.Id, dlg.Request!); await LoadAsync(); }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }

    private async Task DeleteAsync()
    {
        if (Selected() is not { } d || !Ui.Confirm(this, $"'{d.Name}' 잠금 해제일을 삭제할까요?\n삭제하면 그날에도 기간 프리셋의 PC 잠금이 다시 동작합니다.")) return;
        try { await _client.DeleteExemptionDayAsync(d.Id); await LoadAsync(); }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }

    private async Task SkipAsync(ExemptionDay d, DateOnly? date)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (date == null)
        {
            var upcoming = ExemptionCalendar.Expand(d, today, today.AddYears(1)).Take(8).ToList();
            if (upcoming.Count == 0) { Ui.Info(this, "건너뛸 다음 날짜가 없습니다."); return; }
            using var f = new Form();
            Ui.Apply(f, "이 회차 건너뛰기", new Size(360, 140), resizable: false);
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 16, Top = 16, Width = 320 };
            foreach (var x in upcoming) combo.Items.Add($"{x:yyyy-MM-dd} ({ExemptionCalendar.WeekdayKorean(x.DayOfWeek)})");
            combo.SelectedIndex = 0;
            f.Controls.Add(combo);
            f.Controls.Add(Ui.ButtonBar(Ui.Button("취소", (_, _) => f.DialogResult = DialogResult.Cancel, 70), Ui.Button("건너뛰기", (_, _) => f.DialogResult = DialogResult.OK, 90)));
            if (f.ShowDialog(this) != DialogResult.OK) return;
            date = upcoming[combo.SelectedIndex];
        }
        try { await _client.SkipExemptionOccurrenceAsync(d.Id, date.Value); await LoadAsync(); }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }

    private async Task DayClickedAsync(DateOnly day)
    {
        var ex = _grid.Exemptions.Where(e => e.Date == day).ToList();
        var periods = _periods.Where(p => MonthGrid.Covers(p, day)).ToList();
        if (ex.Count == 0 && periods.Count == 0)
        {
            if (_canEdit && Ui.Confirm(this, $"{day:yyyy-MM-dd}을 잠금 해제일로 추가할까요?"))
            {
                using var dlg = new ExemptionDayDialog(null, day);
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    try { await _client.SaveExemptionDayAsync(null, dlg.Request!); await LoadAsync(); } catch (GacClientException e) { Ui.Error(this, e.Message); }
            }
            return;
        }
        if (periods.Count > 0 && ex.Count == 0) { ShowPeriod(periods[0]); return; }
        var text = $"{day:yyyy-MM-dd} ({ExemptionCalendar.WeekdayKorean(day.DayOfWeek)})\n\n" +
                   string.Join("\n", ex.Select(e => $"🔓 잠금 해제일: {e.Name}")) +
                   (periods.Count > 0 ? "\n" + string.Join("\n", periods.Select(p => $"▬ {p.PeriodName}{(p.HasLock ? " (잠금 포함 — 이날은 잠그지 않음)" : "")}")) : "") +
                   "\n\n이날은 어떤 기간이든 PC 잠금이 동작하지 않습니다. 다른 모듈(자동 종료, 자동 정리 등)은 평소처럼 동작합니다.";
        if (_canEdit && ex.Count > 0 && _days.FirstOrDefault(d => d.Id == ex[0].ExemptionId) is { Recurrence: not ExemptionRecurrence.Once } rule)
        {
            if (MessageBox.Show(this, text + "\n\n이 날만 건너뛸까요? (반복 규칙은 그대로 둡니다)", "GAC 2.0", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                await SkipAsync(rule, day);
        }
        else Ui.Info(this, text);
    }

    private async void ShowPeriod(PeriodPresetInfo p)
    {
        using var dlg = new PeriodDetailsDialog(p, _canStopPeriods);
        if (dlg.ShowDialog(this) != DialogResult.Abort) return; // Abort = stop requested
        try { await _client.StopPeriodAsync(p.Id); await LoadAsync(); Ui.Info(this, $"'{p.PeriodName}' 기간을 중지했습니다."); }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }
}

/// <summary>Custom-painted month grid (Sunday first). Exemption days are highlighted; periods are drawn as bars.</summary>
internal sealed class MonthGrid : Control
{
    private static readonly string[] Heads = ["일", "월", "화", "수", "목", "금", "토"];
    private readonly List<(Rectangle Rect, PeriodPresetInfo Period)> _bars = [];
    private readonly List<(Rectangle Rect, DateOnly Day)> _cells = [];

    public DateOnly Month { get; set; }
    public IReadOnlyList<ExemptionDate> Exemptions { get; set; } = [];
    public IReadOnlyList<PeriodPresetInfo> Periods { get; set; } = [];
    public event EventHandler<PeriodPresetInfo>? PeriodClicked;
    public event EventHandler<DateOnly>? DayClicked;

    public MonthGrid()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.White;
        Font = Ui.Font;
        Cursor = Cursors.Hand;
    }

    public DateOnly FirstCell => Month.AddDays(-(int)Month.DayOfWeek);

    public static bool Covers(PeriodPresetInfo p, DateOnly day)
    {
        var s = DateOnly.FromDateTime(p.StartAt.LocalDateTime);
        var e = DateOnly.FromDateTime(p.EndAt.LocalDateTime.AddSeconds(-1));
        return day >= s && day <= e;
    }

    public static string PeriodState(PeriodPresetInfo p)
    {
        var now = DateTimeOffset.Now;
        if (p.EndAt <= now) return "종료";
        if (p.StartAt > now) return "예정";
        return p.Running ? "진행 중" : "중지됨";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        _bars.Clear();
        _cells.Clear();
        const int head = 28;
        var cw = Math.Max(40, (Width - 1) / 7f);
        var ch = Math.Max(40, (Height - head - 1) / 6f);
        using var headBrush = new SolidBrush(Ui.Panel);
        g.FillRectangle(headBrush, 0, 0, Width, head);
        for (var c = 0; c < 7; c++)
            TextRenderer.DrawText(g, Heads[c], Ui.Bold, new Rectangle((int)(c * cw), 0, (int)cw, head), c == 0 ? Ui.Bad : c == 6 ? Ui.Accent : ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var lanes = Periods.OrderBy(p => p.StartAt).Select((p, i) => (p, i)).ToDictionary(x => x.p.Id, x => x.i);
        using var gridPen = new Pen(Color.FromArgb(225, 228, 232));
        using var exBrush = new SolidBrush(Color.FromArgb(222, 245, 228));
        using var small = new Font(Ui.Font.FontFamily, 8.5f);
        for (var i = 0; i < 42; i++)
        {
            var day = FirstCell.AddDays(i);
            var rect = new Rectangle((int)(i % 7 * cw), (int)(head + i / 7 * ch), (int)cw, (int)ch);
            _cells.Add((rect, day));
            var ex = Exemptions.Where(x => x.Date == day).ToList();
            if (ex.Count > 0) g.FillRectangle(exBrush, rect);
            g.DrawRectangle(gridPen, rect);
            var inMonth = day.Month == Month.Month;
            var numColor = !inMonth ? Color.FromArgb(185, 185, 185) : day.DayOfWeek == DayOfWeek.Sunday ? Ui.Bad : day.DayOfWeek == DayOfWeek.Saturday ? Ui.Accent : ForeColor;
            if (day == today)
            {
                using var todayBrush = new SolidBrush(Ui.Accent);
                g.FillEllipse(todayBrush, rect.X + 3, rect.Y + 3, 24, 22);
                numColor = Color.White;
            }
            TextRenderer.DrawText(g, day.Day.ToString(), Ui.Bold, new Rectangle(rect.X + 3, rect.Y + 3, 24, 22), numColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            var y = rect.Y + 27;
            foreach (var x in ex)
            {
                TextRenderer.DrawText(g, "🔓 " + x.Name, small, new Rectangle(rect.X + 3, y, rect.Width - 6, 16), Ui.Ok, TextFormatFlags.EndEllipsis);
                y += 16;
            }
            foreach (var p in Periods.Where(p => Covers(p, day)))
            {
                var lane = lanes[p.Id] % 3;
                var bar = new Rectangle(rect.X + 1, Math.Max(y, rect.Bottom - 20 - lane * 18), rect.Width - 1, 16);
                var state = PeriodState(p);
                var color = state is "종료" or "중지됨" ? Color.FromArgb(170, 170, 170) : p.HasLock ? Color.FromArgb(214, 69, 65) : Ui.Accent;
                if (state == "예정") color = Color.FromArgb(200, color);
                using var b = new SolidBrush(color);
                g.FillRectangle(b, bar);
                var first = day == DateOnly.FromDateTime(p.StartAt.LocalDateTime) || day.DayOfWeek == DayOfWeek.Sunday;
                if (first) TextRenderer.DrawText(g, p.PeriodName + (ex.Count > 0 && p.HasLock ? " (잠금 안 함)" : ""), small, bar, Color.White, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
                _bars.Add((bar, p));
            }
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        foreach (var (rect, p) in _bars) if (rect.Contains(e.Location)) { PeriodClicked?.Invoke(this, p); return; }
        foreach (var (rect, d) in _cells) if (rect.Contains(e.Location)) { DayClicked?.Invoke(this, d); return; }
    }
}

/// <summary>Period details from the calendar: targets, who started it, and the stop button (CHANGE-002 §2.1).</summary>
public sealed class PeriodDetailsDialog : Form
{
    public PeriodDetailsDialog(PeriodPresetInfo p, bool canStop)
    {
        Ui.Apply(this, "기간 프리셋: " + p.PeriodName, new Size(460, 380));
        var state = MonthGrid.PeriodState(p);
        var grid = Ui.Grid();
        Ui.AddRow(grid, "기간 이름", Ui.Label(p.PeriodName, Ui.Bold));
        Ui.AddRow(grid, "상태", Ui.Label(state, color: state == "진행 중" ? Ui.Ok : Ui.Muted));
        Ui.AddRow(grid, "기간", Ui.Label($"{p.StartAt.ToLocalTime():yyyy-MM-dd HH:mm} ~ {p.EndAt.ToLocalTime():yyyy-MM-dd HH:mm}"));
        Ui.AddRow(grid, "시작한 사람", Ui.Label(p.StartedBy));
        Ui.AddRow(grid, "PC 잠금", Ui.Label(p.HasLock ? "포함 (잠금 해제일에는 잠그지 않음)" : "없음"));
        Ui.AddRow(grid, "대상 PC", Ui.Label($"{p.TargetCount}대"));
        var targets = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        foreach (var n in p.TargetNames ?? []) targets.Items.Add(n);
        if (targets.Items.Count == 0) targets.Items.Add($"(PC {p.TargetCount}대)");

        var stop = Ui.Button("기간 중지", (_, _) =>
        {
            if (Ui.Confirm(this, $"'{p.PeriodName}' 기간을 지금 중지할까요? 대상 PC의 잠금도 풀립니다.")) { DialogResult = DialogResult.Abort; Close(); }
        }, 100);
        stop.Enabled = canStop && state is "진행 중" or "예정";
        Controls.Add(targets);
        Controls.Add(grid);
        Controls.Add(Ui.ButtonBar(Ui.Button("닫기", (_, _) => Close(), 80), stop));
    }
}

/// <summary>Add/edit a lock exemption day: preset name dropdown (내PC지키미 점검일) or free text, date, recurrence.</summary>
public sealed class ExemptionDayDialog : Form
{
    private readonly ComboBox _name = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 280 };
    private readonly DateTimePicker _date = new() { Format = DateTimePickerFormat.Long, Width = 200 };
    private readonly RadioButton _once = new() { Text = "한 번만 (위 날짜 하루)", AutoSize = true, Checked = true };
    private readonly RadioButton _monthlyDay = new() { Text = "매월 같은 날짜", AutoSize = true };
    private readonly RadioButton _monthlyNth = new() { Text = "매월 N번째 요일", AutoSize = true };
    private readonly NumericUpDown _dom = new() { Minimum = 1, Maximum = 31, Width = 60 };
    private readonly ComboBox _nth = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    private readonly ComboBox _weekday = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly Label _preview = Ui.Label("", color: Ui.Muted);

    public SaveExemptionDayRequest? Request { get; private set; }

    public ExemptionDayDialog(ExemptionDay? existing, DateOnly? initialDate = null)
    {
        Ui.Apply(this, existing == null ? "잠금 해제일 추가" : "잠금 해제일 편집", new Size(520, 400), resizable: false);
        _name.Items.Add(ExemptionCalendar.PresetName);
        _name.Text = existing?.Name ?? ExemptionCalendar.PresetName;
        foreach (var n in new[] { 1, 2, 3, 4, 5 }) _nth.Items.Add(ExemptionCalendar.NthKorean(n));
        foreach (var d in Enum.GetValues<DayOfWeek>()) _weekday.Items.Add(ExemptionCalendar.WeekdayKorean(d));
        var start = existing?.Date ?? initialDate ?? DateOnly.FromDateTime(DateTime.Today);
        _date.Value = start.ToDateTime(TimeOnly.MinValue);
        _dom.Value = existing?.DayOfMonth ?? start.Day;
        _nth.SelectedIndex = (existing?.Nth ?? 3) - 1;
        _weekday.SelectedIndex = (int)(existing?.Weekday ?? DayOfWeek.Wednesday);
        switch (existing?.Recurrence)
        {
            case ExemptionRecurrence.MonthlyDay: _monthlyDay.Checked = true; break;
            case ExemptionRecurrence.MonthlyNthWeekday: _monthlyNth.Checked = true; break;
        }

        var grid = Ui.Grid();
        Ui.AddRow(grid, "이름", _name);
        Ui.AddRow(grid, "날짜 (반복 시작일)", _date);
        Ui.AddRow(grid, "반복", _once);
        Ui.AddRow(grid, "", Ui.Row(_monthlyDay, _dom, Ui.Label("일")));
        Ui.AddRow(grid, "", Ui.Row(_monthlyNth, _nth, _weekday));
        Ui.AddRow(grid, "", _preview);
        var note = Ui.Label("잠금 해제일에는 00:00부터 23:59:59(한국 시간)까지 모든 PC에서 PC 잠금(lock_pc)이 실행되지 않습니다.\n같은 날 시작한 기간 프리셋도 잠그지 않습니다. 다른 모듈은 평소처럼 동작합니다.", color: Ui.Muted);
        note.Dock = DockStyle.Bottom;
        note.Padding = new Padding(10, 0, 10, 0);
        note.MaximumSize = new Size(500, 0);

        foreach (var c in new Control[] { _name, _date, _once, _monthlyDay, _monthlyNth, _dom, _nth, _weekday })
        {
            c.TextChanged += (_, _) => UpdatePreview();
            if (c is RadioButton r) r.CheckedChanged += (_, _) => UpdatePreview();
            if (c is ComboBox cb) cb.SelectedIndexChanged += (_, _) => UpdatePreview();
            if (c is NumericUpDown nu) nu.ValueChanged += (_, _) => UpdatePreview();
        }
        _date.ValueChanged += (_, _) => UpdatePreview();

        Controls.Add(grid);
        Controls.Add(note);
        Controls.Add(Ui.ButtonBar(Ui.Button("취소", (_, _) => DialogResult = DialogResult.Cancel, 70), Ui.Button("저장", (_, _) => Save(), 80)));
        UpdatePreview();
    }

    private SaveExemptionDayRequest Build() => new(_name.Text.Trim(),
        _once.Checked ? ExemptionRecurrence.Once : _monthlyDay.Checked ? ExemptionRecurrence.MonthlyDay : ExemptionRecurrence.MonthlyNthWeekday,
        DateOnly.FromDateTime(_date.Value), (int)_dom.Value, _nth.SelectedIndex + 1, (DayOfWeek)Math.Max(0, _weekday.SelectedIndex));

    private void UpdatePreview()
    {
        var r = Build();
        _dom.Enabled = _monthlyDay.Checked;
        _nth.Enabled = _weekday.Enabled = _monthlyNth.Checked;
        var probe = new ExemptionDay("", r.Name, r.Recurrence, r.Date, r.DayOfMonth, r.Nth, r.Weekday, [], "", DateTimeOffset.Now);
        var next = ExemptionCalendar.Expand(probe, DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today).AddYears(1)).Take(3).ToList();
        _preview.Text = next.Count == 0 ? "다가오는 날짜 없음" : "다가오는 날짜: " + string.Join(", ", next.Select(d => $"{d:M/d}({ExemptionCalendar.WeekdayKorean(d.DayOfWeek)[..1]})"));
    }

    private void Save()
    {
        var r = Build();
        if (ExemptionCalendar.Validate(r) is { } err) { Ui.Error(this, err); return; }
        Request = r;
        DialogResult = DialogResult.OK;
    }
}
