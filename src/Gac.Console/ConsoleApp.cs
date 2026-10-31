using Gac.Console.Services;
using Gac.Console.UI;
using Gac.Shared.Contracts;
using Gac.Tray.UI;

namespace Gac.Console;

/// <summary>Console start-up flow, shared by Gac.Console.exe and GAC-Demo.exe.</summary>
public static class ConsoleApp
{
    public static void InitializeWinForms()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetDefaultFont(Ui.Font);
    }

    /// <summary>Real product: connect (first time), log in or create the Owner, then show the main window.</summary>
    public static void RunReal()
    {
        while (true)
        {
            var connection = ConsoleConnection.Load();
            if (connection == null)
            {
                using var connect = new ConnectForm();
                if (connect.ShowDialog() != DialogResult.OK) return;
                connection = connect.Result!;
            }

            using var client = new HttpGacServerClient(connection);
            SetupStatus setup;
            try { setup = client.GetSetupAsync().GetAwaiter().GetResult(); }
            catch (GacClientException e)
            {
                var retry = MessageBox.Show(e.Message + "\n\n[다시 시도] 다시 연결 · [취소] 끝내기 · 서버를 바꾸려면 [무시]",
                    "GAC 2.0", MessageBoxButtons.AbortRetryIgnore, MessageBoxIcon.Warning);
                if (retry == DialogResult.Ignore) { ConsoleConnection.Forget(); continue; }
                if (retry == DialogResult.Retry) continue;
                return;
            }

            using var login = new LoginForm(client, setup);
            var lr = login.ShowDialog();
            if (lr == DialogResult.Retry && login.ChangeServerRequested) { ConsoleConnection.Forget(); continue; }
            if (lr != DialogResult.OK) return;

            using var main = new MainForm(client, login.Result!.Account, new ConsoleOptions());
            Application.Run(main);
            if (main.DialogResult != DialogResult.Retry) return; // logout or session expiry → back to login
        }
    }

    /// <summary>Demo and tests: run the main window against any client already logged in.</summary>
    public static void RunWith(IGacServerClient client, AccountInfo me, ConsoleOptions options)
    {
        using var main = new MainForm(client, me, options);
        Application.Run(main);
    }
}
