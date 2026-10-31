using Gac.Console.EasterEgg;

namespace Gac.Console.Tests;

internal sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
}

public class EasterEggTests
{
    [Fact]
    public void Five_clicks_within_three_seconds_reveals()
    {
        var clock = new ManualClock();
        var egg = new AboutEasterEgg(clock);
        for (var i = 0; i < 4; i++) { Assert.False(egg.RegisterClick()); clock.Advance(0.5); }
        Assert.True(egg.RegisterClick());
        Assert.True(egg.Revealed);
    }

    [Fact]
    public void Four_clicks_do_not_reveal()
    {
        var clock = new ManualClock();
        var egg = new AboutEasterEgg(clock);
        for (var i = 0; i < 4; i++) { egg.RegisterClick(); clock.Advance(0.2); }
        Assert.False(egg.Revealed);
    }

    [Fact]
    public void Five_clicks_spread_over_more_than_three_seconds_do_not_reveal()
    {
        var clock = new ManualClock();
        var egg = new AboutEasterEgg(clock);
        for (var i = 0; i < 5; i++) { egg.RegisterClick(); clock.Advance(0.8); } // 5th click at 3.2 s
        Assert.False(egg.Revealed);
    }

    [Fact]
    public void Counter_resets_after_window_passes()
    {
        var clock = new ManualClock();
        var egg = new AboutEasterEgg(clock);
        egg.RegisterClick(); egg.RegisterClick();
        clock.Advance(3.5);
        for (var i = 0; i < 4; i++) { egg.RegisterClick(); clock.Advance(0.1); }
        Assert.False(egg.Revealed);
        Assert.True(egg.RegisterClick());
    }

    [Fact]
    public void Dialog_text_and_constants_are_exact()
    {
        Assert.Equal("MIT Licence. Develop by JH", AboutEasterEgg.DialogText);
        Assert.Equal("About", AboutEasterEgg.DialogTitle);
        Assert.Equal(5, AboutEasterEgg.RevealClickCount);
        Assert.Equal(TimeSpan.FromSeconds(3), AboutEasterEgg.RevealWindow);
    }

    [Fact]
    public void About_item_is_hidden_last_menu_item_and_dialog_shows_exact_text() => Sta.Run(() =>
    {
        using var client = new Gac.Demo.DemoServerClient(delayScale: 0);
        using var form = new Gac.Console.UI.MainForm(client, client.Owner, Gac.Demo.DemoProgram.CreateOptions(false));
        var items = form.Menu.Items;
        Assert.Same(form.AboutMenuItem, items[items.Count - 1]);
        Assert.False(form.AboutMenuItem.Available);
        Assert.Empty(form.AboutMenuItem.ToolTipText ?? "");
        Assert.Equal(Keys.None, form.AboutMenuItem.ShortcutKeys);

        using var dlg = new AboutDialog(form.Font);
        Assert.Equal("About", dlg.Text);
        Assert.False(dlg.MaximizeBox);
        Assert.False(dlg.MinimizeBox);
        Assert.Equal(FormBorderStyle.FixedDialog, dlg.FormBorderStyle);
        var labels = dlg.Controls.OfType<Label>().ToList();
        Assert.Equal("MIT Licence. Develop by JH", Assert.Single(labels).Text);
    });
}

/// <summary>Runs a test body on an STA thread (WinForms controls) and rethrows failures.</summary>
internal static class Sta
{
    public static void Run(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception e) { error = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
