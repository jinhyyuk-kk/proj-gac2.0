namespace Gac.Tray.UI;

/// <summary>Shared look and small control factories so all GAC windows look the same at 100–200% scaling.</summary>
public static class Ui
{
    public static readonly Font Font = new("맑은 고딕", 9.75f);
    public static readonly Font Bold = new("맑은 고딕", 9.75f, FontStyle.Bold);
    public static readonly Font Title = new("맑은 고딕", 14f, FontStyle.Bold);
    public static readonly Font Big = new("맑은 고딕", 12f);

    public static readonly Color Accent = Color.FromArgb(0, 102, 204);
    public static readonly Color Ok = Color.FromArgb(22, 128, 60);
    public static readonly Color Warn = Color.FromArgb(191, 117, 0);
    public static readonly Color Bad = Color.FromArgb(196, 43, 28);
    public static readonly Color Muted = Color.FromArgb(110, 110, 110);
    public static readonly Color Panel = Color.FromArgb(244, 246, 249);

    public static void Apply(Form f, string title, Size size, bool resizable = true)
    {
        f.Text = title;
        f.Font = Font;
        f.AutoScaleMode = AutoScaleMode.Dpi;
        f.AutoScaleDimensions = new SizeF(96f, 96f);
        f.ClientSize = size;
        f.StartPosition = FormStartPosition.CenterParent;
        f.ShowIcon = false;
        if (!resizable)
        {
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = false;
            f.MinimizeBox = false;
        }
        else f.MinimumSize = new Size(Math.Min(size.Width, 480), Math.Min(size.Height, 320));
    }

    public static Button Button(string text, EventHandler? click = null, int width = 110)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(width, 32), Padding = new Padding(6, 2, 6, 2), UseVisualStyleBackColor = true };
        if (click != null) b.Click += click;
        return b;
    }

    public static Label Label(string text, Font? font = null, Color? color = null) =>
        new() { Text = text, AutoSize = true, Font = font ?? Font, ForeColor = color ?? SystemColors.ControlText, Margin = new Padding(3, 6, 3, 3) };

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
        p.Controls.AddRange(controls);
        return p;
    }

    public static FlowLayoutPanel ButtonBar(params Control[] buttons)
    {
        var p = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8), WrapContents = false };
        p.Controls.AddRange(buttons);
        return p;
    }

    public static ListView List(params (string Header, int Width)[] columns)
    {
        var lv = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, Dock = DockStyle.Fill, GridLines = false, MultiSelect = false };
        foreach (var (h, w) in columns) lv.Columns.Add(h, w);
        return lv;
    }

    /// <summary>Two-column label/field grid.</summary>
    public static TableLayoutPanel Grid()
    {
        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(8) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    public static void AddRow(TableLayoutPanel t, string label, Control field)
    {
        var r = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 3) }, 0, r);
        field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        field.Margin = new Padding(3, 4, 3, 4);
        t.Controls.Add(field, 1, r);
    }

    public static void Error(IWin32Window? owner, string message) =>
        MessageBox.Show(owner, message, "GAC 2.0", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public static void Info(IWin32Window? owner, string message) =>
        MessageBox.Show(owner, message, "GAC 2.0", MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static bool Confirm(IWin32Window? owner, string message) =>
        MessageBox.Show(owner, message, "GAC 2.0", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
}
