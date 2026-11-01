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

    [Theory]
    [InlineData("Gac.Console", "Gac.Console.exe", "gac-admin.ico")]
    [InlineData("Gac.Demo", "GAC-Demo.exe", "gac.ico")]
    public void Exe_embeds_the_expected_icon(string project, string exe, string icon)
    {
        var path = Exe(project, exe);
        Assert.True(File.Exists(path), $"build output missing: {path}");
        var actual = IconExtractor.ExtractMainIcon(path);
        var expected = Asset(icon);
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++) Assert.Equal(expected[i].Data, actual[i].Data);
    }
}
