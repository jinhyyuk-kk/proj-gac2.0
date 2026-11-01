using Gac.Shared.Contracts;

namespace Gac.Tray.UI;

/// <summary>Weekday on/off + time grid and exception dates. Shared by the Console and the GAC 서버 관리 app.</summary>
public sealed class ShutdownScheduleEditor : UserControl
{
    private readonly CheckBox _enabled = new() { Text = "서버 PC 자동 종료 사용", AutoSize = true, Font = Ui.Bold };
    private readonly (DayOfWeek Day, CheckBox On, DateTimePicker Time)[] _days;
    private readonly ListBox _exceptions = new() { Width = 200, Height = 140, IntegralHeight = false };
    private readonly DateTimePicker _newException = new() { Format = DateTimePickerFormat.Short, Width = 120 };
    private ServerShutdownSettings? _value;

    public ShutdownScheduleEditor()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(8);
        DayOfWeek[] order = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
        _days = order.Select(d => (d, new CheckBox { Text = ExemptionCalendar.WeekdayKorean(d), AutoSize = true, Width = 80 },
            new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 80 })).ToArray();
        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Left, Padding = new Padding(4) };
        table.Controls.Add(_enabled, 0, 0);
        table.SetColumnSpan(_enabled, 2);
        for (var i = 0; i < _days.Length; i++) { table.Controls.Add(_days[i].On, 0, i + 1); table.Controls.Add(_days[i].Time, 1, i + 1); }

        var exBox = new GroupBox { Text = "예외 날짜 (그날은 끄지 않음)", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var exPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        exPanel.Controls.Add(_exceptions);
        exPanel.Controls.Add(Ui.Row(_newException, Ui.Button("추가", (_, _) => AddException(), 60), Ui.Button("삭제", (_, _) => { if (_exceptions.SelectedIndex >= 0) _exceptions.Items.RemoveAt(_exceptions.SelectedIndex); }, 60)));
        exPanel.Controls.Add(Ui.Label("예: 학교 행사로 서버가 밤새 켜져 있어야 하는 날", color: Ui.Muted));
        exBox.Controls.Add(exPanel);
        Controls.Add(exBox);
        Controls.Add(table);
    }

    private void AddException()
    {
        var d = DateOnly.FromDateTime(_newException.Value).ToString("yyyy-MM-dd");
        if (!_exceptions.Items.Contains(d)) _exceptions.Items.Add(d);
    }

    public ServerShutdownSettings? Value
    {
        get
        {
            if (_value == null) return null;
            return new ServerShutdownSettings(_enabled.Checked,
                [.. _days.Select(d => new ServerShutdownDay(d.Day, d.On.Checked, d.Time.Value.ToString("HH:mm")))],
                [.. _exceptions.Items.Cast<string>().Select(s => DateOnly.Parse(s)).Order()]);
        }
        set
        {
            _value = value;
            if (value == null) return;
            _enabled.Checked = value.Enabled;
            foreach (var (day, on, time) in _days)
            {
                var d = value.Days.FirstOrDefault(x => x.Day == day);
                on.Checked = d?.Enabled ?? false;
                time.Value = DateTime.Today + (TimeOnly.TryParseExact(d?.Time ?? "18:00", "HH:mm", out var t) ? t.ToTimeSpan() : TimeSpan.FromHours(18));
            }
            _exceptions.Items.Clear();
            foreach (var x in value.ExceptionDates) _exceptions.Items.Add(x.ToString("yyyy-MM-dd"));
        }
    }
}

/// <summary>Overload limit fields (defaults in brackets per CHANGE-002 §4.3).</summary>
public sealed class OverloadEditor : UserControl
{
    private readonly NumericUpDown _req = Num(5, 10000), _auth = Num(1, 1000), _enroll = Num(1, 1000), _block = Num(1, 1440), _max = Num(1, 720);

    public OverloadEditor()
    {
        Dock = DockStyle.Top;
        AutoSize = true;
        var g = Ui.Grid();
        Ui.AddRow(g, "PC 한 대의 분당 최대 요청 수 [60]", _req);
        Ui.AddRow(g, "주소 하나의 10분당 인증 실패 한도 [10]", _auth);
        Ui.AddRow(g, "주소 하나의 시간당 등록 시도 한도 [5]", _enroll);
        Ui.AddRow(g, "차단 시간(분) [10] — 24시간 안에 반복하면 두 배", _block);
        Ui.AddRow(g, "최대 차단 시간(시간) [24]", _max);
        Controls.Add(g);
    }

    private static NumericUpDown Num(int min, int max) => new() { Minimum = min, Maximum = max, Width = 90 };

    public OverloadSettings Value
    {
        get => new((int)_req.Value, (int)_auth.Value, (int)_enroll.Value, (int)_block.Value, (int)_max.Value);
        set { _req.Value = value.MaxRequestsPerAgentPerMinute; _auth.Value = value.MaxFailedAuthPerSourcePer10Min; _enroll.Value = value.MaxEnrollPerSourcePerHour; _block.Value = value.BlockMinutes; _max.Value = value.MaxBlockHours; }
    }
}
