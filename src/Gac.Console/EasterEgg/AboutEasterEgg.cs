namespace Gac.Console.EasterEgg;

/// <summary>
/// Hidden About item (see EASTER_EGG.md). Clicking the top-left GAC logo 5 times within 3 seconds reveals the last
/// menu item for the rest of the session. Pure logic with an injectable clock so it is testable without UI.
/// No network access, no file writes, no telemetry.
/// </summary>
public sealed class AboutEasterEgg(TimeProvider clock)
{
    public const int RevealClickCount = 5;
    public static readonly TimeSpan RevealWindow = TimeSpan.FromSeconds(3);
    public const string DialogText = "MIT Licence. Develop by JH";
    public const string DialogTitle = "About";

    private DateTimeOffset? _firstClick;
    private int _count;

    public bool Revealed { get; private set; }

    /// <summary>Records one logo click. Returns true exactly when this click reveals the item.</summary>
    public bool RegisterClick()
    {
        if (Revealed) return false;
        var now = clock.GetUtcNow();
        if (_firstClick == null || now - _firstClick.Value > RevealWindow)
        {
            _firstClick = now;
            _count = 0;
        }
        _count++;
        if (_count >= RevealClickCount)
        {
            Revealed = true;
            return true;
        }
        return false;
    }
}

/// <summary>Small fixed dialog: title "About", one centered line, one OK button (Enter/Esc close).</summary>
public sealed class AboutDialog : Form
{
    public AboutDialog(Font font)
    {
        Text = AboutEasterEgg.DialogTitle;
        Font = font;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(320, 120);

        var label = new Label { Text = AboutEasterEgg.DialogText, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(80, 30), Anchor = AnchorStyles.None };
        var bar = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 48, ColumnCount = 1 };
        bar.Controls.Add(ok);
        Controls.Add(label);
        Controls.Add(bar);
        AcceptButton = ok;
        CancelButton = ok;
    }
}
