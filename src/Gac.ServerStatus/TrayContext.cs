using System.Net.Http.Json;
using Gac.Server.Admin;
using Gac.Shared;
using Gac.Shared.Contracts;
using Gac.Shared.Security;
using Gac.Tray.UI;

namespace Gac.ServerStatus;

/// <summary>
/// GAC 서버 관리 tray icon (CHANGE-002 §4), started at every logon on the Admin PC without admin rights. It shows the
/// T−10 "서버 PC가 10분 후 종료됩니다." popup with [오늘은 끄지 않기] / [30분 연장] (via the service's loopback-only
/// endpoints) and opens the settings window, which asks for elevation.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon = new() { Text = "GAC 서버 관리", Visible = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15_000 };
    private bool _noticeShown;

    public TrayContext()
    {
        _icon.Icon = GacIcons.GacSized(16);
        var menu = new ContextMenuStrip { Font = Ui.Font };
        menu.Items.Add("GAC 서버 관리 열기…", null, (_, _) => Program.OpenElevatedWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("알림 아이콘 끝내기", null, (_, _) => ExitThread());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => Program.OpenElevatedWindow();
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
        _ = CheckAsync();
    }

    private static HttpClient? Client()
    {
        if (LocalRegistry.Read() is not { } local) return null;
        return new HttpClient(CertPinning.CreatePinnedHandler(local.Thumbprint)) { BaseAddress = new Uri($"https://127.0.0.1:{local.Port}"), Timeout = TimeSpan.FromSeconds(10) };
    }

    private async Task CheckAsync()
    {
        using var http = Client();
        if (http == null) return;
        ServerShutdownStatus? s;
        try { s = await http.GetFromJsonAsync<ServerShutdownStatus>("/api/local/server-shutdown", GacJson.Options); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { _icon.Text = "GAC 서버 관리 (서비스 연결 안 됨)"; return; }
        if (s == null) return;
        _icon.Text = s.NextShutdown is { } n ? $"GAC 서버 관리 · 자동 종료 {n.ToLocalTime():MM-dd HH:mm}" : "GAC 서버 관리";
        if (!s.Warning) { _noticeShown = false; return; }
        if (_noticeShown) return;
        _noticeShown = true;
        var f = new ServerShutdownNoticeForm(s.NextShutdown, canChange: true, s.WaitingFor, onServerPc: true);
        f.SkipTodayClicked += async (_, _) => await PostAsync("/api/local/server-shutdown/skip-today", "오늘은 서버 PC를 끄지 않습니다.");
        f.ExtendClicked += async (_, _) => await PostAsync("/api/local/server-shutdown/extend", "서버 PC 종료를 30분 연장했습니다.");
        f.FormClosed += (_, _) => f.Dispose();
        f.Show();
        f.Activate();
    }

    private async Task PostAsync(string path, string ok)
    {
        using var http = Client();
        if (http == null) return;
        try
        {
            (await http.PostAsync(path, null)).EnsureSuccessStatusCode();
            _icon.ShowBalloonTip(4000, "GAC 서버 관리", ok, ToolTipIcon.Info);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            _icon.ShowBalloonTip(5000, "GAC 서버 관리", "서버 서비스에 연결할 수 없습니다.", ToolTipIcon.Warning);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _icon.Visible = false; _icon.Dispose(); }
        base.Dispose(disposing);
    }
}
