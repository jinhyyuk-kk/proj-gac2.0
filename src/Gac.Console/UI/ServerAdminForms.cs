using Gac.Console.Services;
using Gac.Shared.Contracts;
using Gac.Tray.UI;

namespace Gac.Console.UI;

/// <summary>알림 (CHANGE-002 §4.5): overload alerts, newest first, with a read state.</summary>
public sealed class AlertsView : UserControl
{
    private readonly IGacServerClient _client;
    private readonly ListView _list = Ui.List(("", 26), ("PC 별명", 120), ("Windows 이름", 120), ("IP", 110), ("제한", 140), ("요청 수", 70), ("시작", 120), ("차단 해제", 120));

    public event EventHandler? UnreadChanged;

    public AlertsView(IGacServerClient client)
    {
        _client = client;
        Dock = DockStyle.Fill;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        bar.Controls.AddRange([
            Ui.Button("읽음으로 표시", async (_, _) => await MarkAsync(selectedOnly: true), 120),
            Ui.Button("모두 읽음", async (_, _) => await MarkAsync(selectedOnly: false), 100),
            Ui.Button("새로 고침", async (_, _) => await LoadAsync(), 90),
            Ui.Label("PC가 서버에 요청을 너무 많이 보내면 서버가 잠시 차단하고 여기에 알립니다. 차단 해제·영구 차단은 [관리 → 서버 관리]에서 합니다.", color: Ui.Muted),
        ]);
        _list.MultiSelect = true;
        Controls.Add(_list);
        Controls.Add(bar);
    }

    public IReadOnlyList<OverloadAlert> Alerts { get; private set; } = [];
    public int Unread => Alerts.Count(a => !a.Read);

    public async Task LoadAsync()
    {
        Alerts = await _client.GetAlertsAsync();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var a in Alerts.OrderByDescending(a => a.StartedAt))
        {
            var item = new ListViewItem([a.Read ? "" : "●", a.PcAlias ?? "-", a.WindowsName ?? "-", a.Ip, OverloadText.Korean(a.Limit), a.RequestCount.ToString(),
                a.StartedAt.ToLocalTime().ToString("MM-dd HH:mm:ss"), a.BlockedUntil.ToLocalTime().ToString("MM-dd HH:mm")]) { Tag = a };
            if (!a.Read) { item.Font = Ui.Bold; item.ForeColor = Ui.Bad; }
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        UnreadChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task MarkAsync(bool selectedOnly)
    {
        var ids = (selectedOnly ? _list.SelectedItems.Cast<ListViewItem>() : _list.Items.Cast<ListViewItem>()).Select(i => ((OverloadAlert)i.Tag!).Id).ToList();
        if (ids.Count == 0) return;
        try { await _client.MarkAlertsReadAsync(ids); await LoadAsync(); }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }
}

/// <summary>
/// 서버 관리 (CHANGE-002 §4.5): mirrors the GAC 서버 관리 settings on the Admin PC — they are the same setting.
/// Server PC auto shutdown: view + change with "서버 PC 종료 설정" (Owner + Admin by default).
/// Overload limits: view with Owner/Admin, change with "서버 설정" (Owner only). Every check is repeated on the server.
/// </summary>
public sealed class ServerManagementForm : Form
{
    private readonly IGacServerClient _client;
    private readonly bool _canShutdown, _canOverload;
    private readonly ShutdownScheduleEditor _schedule = new();
    private readonly Label _shutdownStatus = Ui.Label("", color: Ui.Muted);
    private readonly OverloadEditor _overload = new();
    private readonly ListView _blocks = Ui.List(("PC 별명", 110), ("Windows 이름", 120), ("IP", 110), ("제한", 140), ("차단 해제 시각", 120), ("횟수", 50));

    public ServerManagementForm(IGacServerClient client, bool canShutdown, bool canOverload)
    {
        _client = client;
        _canShutdown = canShutdown;
        _canOverload = canOverload;
        Ui.Apply(this, "서버 관리", new Size(760, 560));

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var t1 = new TabPage("서버 PC 자동 종료");
        var save1 = Ui.Button("저장", async (_, _) => await SaveShutdownAsync(), 80);
        var skip = Ui.Button("오늘은 끄지 않기", async (_, _) => await Do(_client.SkipServerShutdownTodayAsync), 140);
        var ext = Ui.Button("30분 연장", async (_, _) => await Do(_client.ExtendServerShutdownAsync), 100);
        save1.Enabled = skip.Enabled = ext.Enabled = canShutdown;
        _schedule.Enabled = canShutdown;
        var intro1 = Ui.Label("관리 PC(서버 PC) 자체가 자동으로 꺼지는 시간입니다. 관리 PC의 ‘GAC 서버 관리’에서 바꾼 설정과 같은 설정입니다." +
                              (canShutdown ? "" : "\n이 계정은 보기만 할 수 있습니다 (‘서버 PC 종료 설정’ 권한 필요)."), color: Ui.Muted);
        intro1.Dock = DockStyle.Top; intro1.Padding = new Padding(8); intro1.MaximumSize = new Size(720, 0);
        _shutdownStatus.Dock = DockStyle.Top; _shutdownStatus.Padding = new Padding(8, 0, 8, 6);
        t1.Controls.Add(_schedule);
        t1.Controls.Add(_shutdownStatus);
        t1.Controls.Add(intro1);
        t1.Controls.Add(Ui.ButtonBar(save1, ext, skip));

        var t2 = new TabPage("과부하 차단");
        var save2 = Ui.Button("저장", async (_, _) => await SaveOverloadAsync(), 80);
        save2.Enabled = canOverload;
        _overload.Enabled = canOverload;
        var unblock = Ui.Button("차단 해제", async (_, _) => await BlockActionAsync(permanent: false), 100);
        var perm = Ui.Button("영구 차단", async (_, _) => await BlockActionAsync(permanent: true), 100);
        unblock.Enabled = perm.Enabled = canOverload;
        var intro2 = Ui.Label("한 PC가 요청을 너무 많이 보내면 그 PC(하드웨어 지문)와 주소를 잠시 차단하고 운영자 PC로 알립니다. 같은 PC가 24시간 안에 다시 넘으면 차단 시간이 두 배가 됩니다." +
                              (canOverload ? "" : "\n한도 변경은 ‘서버 설정’ 권한(최고 관리자)이 필요합니다."), color: Ui.Muted);
        intro2.Dock = DockStyle.Top; intro2.Padding = new Padding(8); intro2.MaximumSize = new Size(720, 0);
        var blocksBox = new GroupBox { Text = "지금 차단된 PC", Dock = DockStyle.Fill, Padding = new Padding(6) };
        blocksBox.Controls.Add(_blocks);
        blocksBox.Controls.Add(Ui.ButtonBar(perm, unblock));
        t2.Controls.Add(blocksBox);
        t2.Controls.Add(_overload);
        t2.Controls.Add(intro2);
        t2.Controls.Add(Ui.ButtonBar(save2));

        tabs.TabPages.AddRange([t1, t2]);
        Controls.Add(tabs);
        Controls.Add(Ui.ButtonBar(Ui.Button("닫기", (_, _) => Close(), 80)));
        Load += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _schedule.Value = await _client.GetServerShutdownAsync();
            _shutdownStatus.Text = StatusText(await _client.GetServerShutdownStatusAsync());
            _overload.Value = await _client.GetOverloadSettingsAsync();
            var blocks = await _client.GetOverloadBlocksAsync();
            _blocks.Items.Clear();
            foreach (var b in blocks)
                _blocks.Items.Add(new ListViewItem([b.PcAlias ?? "-", b.WindowsName ?? "-", b.Ip, OverloadText.Korean(b.Limit), b.BlockedUntil.ToLocalTime().ToString("MM-dd HH:mm"), b.Offences.ToString()]) { Tag = b });
        }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }

    private static string StatusText(ServerShutdownStatus s) => ServerShutdownNoticeForm.Describe(s);

    private async Task SaveShutdownAsync()
    {
        if (_schedule.Value is not { } v) return;
        try { await _client.SaveServerShutdownAsync(v); Ui.Info(this, "서버 PC 자동 종료 설정을 저장했습니다."); await LoadAsync(); }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }

    private async Task SaveOverloadAsync()
    {
        try { await _client.SaveOverloadSettingsAsync(_overload.Value); Ui.Info(this, "과부하 차단 설정을 저장했습니다."); }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }

    private async Task BlockActionAsync(bool permanent)
    {
        if (_blocks.SelectedItems.Count == 0) { Ui.Error(this, "차단된 PC를 선택하세요."); return; }
        var b = (OverloadBlock)_blocks.SelectedItems[0].Tag!;
        if (permanent && !Ui.Confirm(this, $"{b.WindowsName ?? b.Ip}을(를) 영구 차단할까요? ‘차단된 기기’ 목록으로 옮겨집니다.")) return;
        try { if (permanent) await _client.BlockPermanentlyAsync(b.Key); else await _client.UnblockAsync(b.Key); await LoadAsync(); }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }

    private async Task Do(Func<Task> action)
    {
        try { await action(); await LoadAsync(); }
        catch (GacClientException e) { Ui.Error(this, e.Message); }
    }
}
