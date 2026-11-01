using System.Text.RegularExpressions;
using Gac.IconTool;

namespace Gac.Icons.Tests;

/// <summary>CHANGE-002 §6: each exe embeds the right icon — admin icon for the Console, GAC icon for everything else.</summary>
public class EmbeddedIconTests
{
    private static string Exe(string project, string exeName)
    {
        // Same configuration as this test assembly (Debug/Release), next to the project's own build output.
        var cfg = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        return Path.Combine(IconToolTests.RepoRoot(), "src", project, "bin", cfg, "net10.0-windows", exeName);
    }

    private static IReadOnlyList<IcoFrame> Asset(string name) =>
        IcoFile.Read(File.ReadAllBytes(Path.Combine(IconToolTests.RepoRoot(), "assets", "icons", name)));

    private static void AssertSameIcon(string path, string icon)
    {
        var actual = IconExtractor.ExtractMainIcon(path);
        var expected = Asset(icon);
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++) Assert.Equal(expected[i].Data, actual[i].Data);
    }

    [Theory]
    [InlineData("Gac.Console", "Gac.Console.exe", "gac-admin.ico")]
    [InlineData("Gac.Demo", "GAC-Demo.exe", "gac.ico")]
    [InlineData("Gac.Tray", "Gac.Tray.exe", "gac.ico")]
    [InlineData("Gac.Agent", "Gac.Agent.exe", "gac.ico")]
    [InlineData("Gac.Server", "Gac.Server.exe", "gac.ico")]
    [InlineData("Gac.ServerStatus", "Gac.ServerStatus.exe", "gac.ico")]
    public void Exe_embeds_the_expected_icon(string project, string exe, string icon)
    {
        var path = Exe(project, exe);
        Assert.True(File.Exists(path), $"build output missing: {path}");
        AssertSameIcon(path, icon);
    }

    [Theory]
    [InlineData("GAC-Console.iss", "GAC-Console-Setup.exe", "gac-admin.ico")]
    [InlineData("GAC-Agent.iss", "GAC-Agent-Setup.exe", "gac.ico")]
    [InlineData("GAC-Server.iss", "GAC-Server-Setup.exe", "gac.ico")]
    public void Installer_uses_the_expected_icon(string script, string setupExe, string icon)
    {
        var iss = Path.Combine(IconToolTests.RepoRoot(), "installer", script);
        var m = Regex.Match(File.ReadAllText(iss), @"^SetupIconFile=(.+)$", RegexOptions.Multiline);
        Assert.True(m.Success, $"{script} has no SetupIconFile");
        Assert.EndsWith(@"assets\icons\" + icon, m.Groups[1].Value.Trim());
        // When the installer has been built after the script last changed, check the compiled exe too.
        var built = Path.Combine(IconToolTests.RepoRoot(), "dist", setupExe);
        if (File.Exists(built) && File.GetLastWriteTimeUtc(built) > File.GetLastWriteTimeUtc(iss))
        {
            var frames = IconExtractor.ExtractMainIcon(built);
            var expected = Asset(icon);
            Assert.Contains(frames, f => expected.Any(e => e.Data.AsSpan().SequenceEqual(f.Data)));
        }
    }
}
