namespace Gac.Tray.UI;

/// <summary>
/// "서버 PC가 10분 후 종료됩니다." (CHANGE-002 §4.2): the warning popup on the Admin PC and the notice in logged-in
/// Consoles, with [오늘은 끄지 않기] and [30분 연장]. The buttons are enabled only for someone allowed to change the
/// server PC shutdown ("서버 PC 종료 설정", or a Windows administrator on the Admin PC).
/// </summary>
public sealed class ServerShutdownNoticeForm : Form
{
    public const string NoticeText = "서버 PC가 10분 후 종료됩니다.";

    /// <summary>One-paragraph status ("다음 자동 종료: …") shown by the Console and the GAC 서버 관리 app.</summary>
    public static string Describe(Gac.Shared.Contracts.ServerShutdownStatus s) =>
        (s.Warning ? "⚠ " + NoticeText + "  " : "") +
        (s.NextShutdown is { } n ? $"다음 자동 종료: {n.ToLocalTime():yyyy-MM-dd (ddd) HH:mm}" : "예정된 자동 종료 없음") +
        (s.SkippedToday ? " · 오늘은 끄지 않음" : "") + (s.ExtendedMinutes > 0 ? $" · 오늘 {s.ExtendedMinutes}분 연장됨" : "") +
        (s.WaitingFor != null ? "\n" + s.WaitingFor : "") + (s.LastEvent != null ? $"\n최근: {s.LastEvent}" : "");

    public event EventHandler? SkipTodayClicked;
    public event EventHandler? ExtendClicked;

    public ServerShutdownNoticeForm(DateTimeOffset? shutdownAt, bool canChange, string? waitingFor = null, bool onServerPc = false)
    {
        Ui.Apply(this, onServerPc ? "GAC 서버 PC 자동 종료" : "GAC 서버 알림", new Size(460, 230), resizable: false);
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;

        var title = Ui.Label(NoticeText, Ui.Title, Ui.Warn);
        title.Dock = DockStyle.Top;
        title.Padding = new Padding(14, 14, 0, 4);
        var detail = Ui.Label(
            (shutdownAt is { } t ? $"종료 예정 시각: {t.ToLocalTime():HH:mm}\n" : "") +
            (onServerPc ? "이 PC에서 GAC 서버가 꺼지면 교실 PC들은 결과를 모아 두었다가 서버가 켜지면 보냅니다."
                        : "서버가 꺼져도 교실 PC의 자동 정리·자동 종료는 그대로 동작합니다.") +
            (waitingFor != null ? "\n" + waitingFor : "") +
            (canChange ? "" : "\n(이 계정은 서버 PC 종료 설정을 바꿀 권한이 없습니다.)"), color: Ui.Muted);
        detail.Dock = DockStyle.Fill;
        detail.Padding = new Padding(14, 4, 14, 0);
        detail.MaximumSize = new Size(430, 0);

        var skip = Ui.Button("오늘은 끄지 않기", (_, _) => { SkipTodayClicked?.Invoke(this, EventArgs.Empty); Close(); }, 140);
        var extend = Ui.Button("30분 연장", (_, _) => { ExtendClicked?.Invoke(this, EventArgs.Empty); Close(); }, 110);
        var close = Ui.Button("닫기", (_, _) => Close(), 80);
        skip.Enabled = extend.Enabled = canChange;

        Controls.Add(detail);
        Controls.Add(title);
        Controls.Add(Ui.ButtonBar(close, extend, skip));
    }
}
