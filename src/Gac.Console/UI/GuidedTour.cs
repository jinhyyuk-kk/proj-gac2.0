using Gac.Tray.UI;

namespace Gac.Console.UI;

/// <summary>Optional first-run tour (6 short, skippable steps) that switches the main window to each screen.</summary>
public static class GuidedTour
{
    internal static readonly (string Title, string Text, int Tab)[] Steps =
    [
        ("GAC 2.0에 오신 것을 환영합니다", "학교의 모든 PC를 한 화면에서 관리하는 무료 프로그램입니다.\n이 체험판은 가상의 학교(PC 약 20대)로 동작하며, 실제 PC에는 아무 변화도 없습니다.", 0),
        ("PC 목록", "왼쪽에서 반이나 교실을 고르면 그 그룹의 PC가 보입니다.\n켜짐/꺼짐, 남은 디스크 공간, 대기 중인 업데이트, 최근 오류를 한눈에 확인하세요.", 0),
        ("작업 실행", "PC를 선택하고 ‘▶ 선택한 PC에 작업 실행’을 누르면\n자동 정리, 종료, 업데이트, 프로그램 설치, 학기말 점검 등을 한 번에 보낼 수 있습니다.", 0),
        ("작업 결과", "보낸 작업이 PC마다 성공했는지 여기서 확인합니다.\n줄을 누르면 자세한 결과(지운 용량, 명령 출력 등)가 오른쪽에 나옵니다.", 1),
        ("보고서", "PC 상태, 오류, 업데이트, 학기말 점검 결과를 모아 봅니다.\n학기말 점검 결과는 표로 내보낼 수 있습니다.", 2),
        ("관리 메뉴와 선생님 기능", "‘관리’ 메뉴에서 계정·그룹·자동 종료 시각을 정합니다.\n‘보기 → 선생님용 트레이 미리 보기’에서 수업 모드 프리셋과 네트워크 자가 진단을 체험해 보세요.", 0),
    ];

    public static void Show(MainForm main)
    {
        var step = 0;
        var form = new Form();
        Ui.Apply(form, "둘러보기", new Size(460, 220), resizable: false);
        form.StartPosition = FormStartPosition.Manual;
        form.TopMost = true;
        form.ShowInTaskbar = false;
        var title = Ui.Label("", Ui.Title);
        var text = Ui.Label("");
        text.MaximumSize = new Size(420, 0);
        var counter = Ui.Label("", color: Ui.Muted);
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(14, 10, 14, 0), WrapContents = false };
        body.Controls.AddRange([title, text, counter]);
        var next = Ui.Button("다음", null, 80);
        var skip = Ui.Button("건너뛰기", (_, _) => form.Close(), 90);
        form.Controls.Add(body);
        form.Controls.Add(Ui.ButtonBar(next, skip));

        void ShowStep()
        {
            var s = Steps[step];
            title.Text = s.Title;
            text.Text = s.Text;
            counter.Text = $"{step + 1} / {Steps.Length}";
            next.Text = step == Steps.Length - 1 ? "마치기" : "다음";
            if (s.Tab < main.Tabs.TabCount) main.Tabs.SelectedIndex = s.Tab;
        }
        next.Click += (_, _) => { if (++step >= Steps.Length) form.Close(); else ShowStep(); };
        var screen = Screen.FromControl(main).WorkingArea;
        form.Location = new Point(screen.Right - form.Width - 40, screen.Bottom - form.Height - 60);
        ShowStep();
        form.Show(main);
    }
}
