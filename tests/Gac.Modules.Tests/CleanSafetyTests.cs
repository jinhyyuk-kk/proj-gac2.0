using System.Diagnostics;
using Gac.Modules.Clean;
using Gac.Shared.Contracts;

namespace Gac.Modules.Tests;

/// <summary>CLAUDE.md section 8 rule 6: code that deletes files must prove paths outside the whitelist are never touched.</summary>
public sealed class CleanSafetyTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "gac-clean-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _outside;
    private readonly SafeCleaner _cleaner = new(TimeProvider.System);

    public CleanSafetyTests()
    {
        _root = Path.Combine(_base, "Temp");
        _outside = Path.Combine(_base, "Outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    private static string Touch(string path, int ageDays = 30)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "data");
        var t = DateTime.UtcNow.AddDays(-ageDays);
        File.SetCreationTimeUtc(path, t);
        File.SetLastWriteTimeUtc(path, t);
        return path;
    }

    private static void Junction(string link, string target)
    {
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "junction creation failed");
    }

    private CleanTarget All() => new("test", _root, CleanRule.All(TimeSpan.Zero));

    [Fact]
    public void Deletes_files_inside_root_and_empty_subfolders_but_keeps_root()
    {
        Touch(Path.Combine(_root, "a.tmp"));
        Touch(Path.Combine(_root, "sub", "b.tmp"));
        var r = _cleaner.Clean(All(), dryRun: false);
        Assert.Equal(2, r.Files);
        Assert.True(Directory.Exists(_root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void Junction_inside_root_is_not_followed_and_target_survives()
    {
        var victim = Touch(Path.Combine(_outside, "important.docx"));
        Junction(Path.Combine(_root, "link"), _outside);
        Touch(Path.Combine(_root, "junk.tmp"));

        var r = _cleaner.Clean(All(), dryRun: false);

        Assert.True(File.Exists(victim));
        Assert.True(Directory.Exists(Path.Combine(_root, "link"))); // the link itself is not removed either
        Assert.Equal(1, r.Files);
        Assert.True(r.Skipped >= 1);
    }

    [Fact]
    public void Root_that_is_a_junction_is_refused()
    {
        var victim = Touch(Path.Combine(_outside, "important.docx"));
        var linkRoot = Path.Combine(_base, "LinkRoot");
        Junction(linkRoot, _outside);
        var r = _cleaner.Clean(new CleanTarget("t", linkRoot, CleanRule.All(TimeSpan.Zero)), dryRun: false);
        Assert.True(File.Exists(victim));
        Assert.Equal(0, r.Files);
    }

    [Fact]
    public void Root_reached_through_a_junctioned_parent_is_refused()
    {
        var victim = Touch(Path.Combine(_outside, "Temp", "important.docx"));
        var fakeProfile = Path.Combine(_base, "Profile");
        Junction(fakeProfile, _outside); // Profile -> Outside, so Profile\Temp resolves outside
        var r = _cleaner.Clean(new CleanTarget("t", Path.Combine(fakeProfile, "Temp"), CleanRule.All(TimeSpan.Zero)), dryRun: false);
        Assert.True(File.Exists(victim));
        Assert.Equal(0, r.Files);
    }

    [Fact]
    public void Dry_run_deletes_nothing_and_reports_what_would_go()
    {
        var f1 = Touch(Path.Combine(_root, "a.tmp"));
        var f2 = Touch(Path.Combine(_root, "x", "b.tmp"));
        var samples = new List<string>();
        var r = _cleaner.Clean(All(), dryRun: true, samples);
        Assert.Equal(2, r.Files);
        Assert.Equal(8, r.Bytes);
        Assert.True(File.Exists(f1) && File.Exists(f2));
        Assert.Contains(f1, samples);
    }

    [Fact]
    public void Retention_rule_keeps_recent_downloads()
    {
        var old = Touch(Path.Combine(_root, "old.zip"), ageDays: 20);
        var recent = Touch(Path.Combine(_root, "new.zip"), ageDays: 3);
        var r = _cleaner.Clean(new CleanTarget("dl", _root, CleanRule.Older(TimeSpan.FromDays(14))), dryRun: false);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.Equal(1, r.Files);
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void Locked_file_is_skipped_and_run_continues()
    {
        var locked = Touch(Path.Combine(_root, "locked.tmp"));
        Touch(Path.Combine(_root, "free.tmp"));
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = _cleaner.Clean(All(), dryRun: false);
            Assert.Equal(1, r.Files);
            Assert.Equal(1, r.Skipped);
        }
        Assert.True(File.Exists(locked));
    }

    [Fact]
    public void Keep_list_and_min_age_are_respected()
    {
        var ini = Touch(Path.Combine(_root, "desktop.ini"));
        var fresh = Touch(Path.Combine(_root, "fresh.tmp"), ageDays: 0);
        var rule = CleanRule.All(TimeSpan.FromHours(1)) with { KeepFileNames = new HashSet<string>(["desktop.ini"], StringComparer.OrdinalIgnoreCase) };
        _cleaner.Clean(new CleanTarget("t", _root, rule), dryRun: false);
        Assert.True(File.Exists(ini));
        Assert.True(File.Exists(fresh));
    }

    [Theory]
    [InlineData(@"C:\Temp\root", @"C:\Temp\root\a.txt", true)]
    [InlineData(@"C:\Temp\root", @"C:\Temp\root2\a.txt", false)]
    [InlineData(@"C:\Temp\root", @"C:\Temp\root", false)]
    [InlineData(@"C:\Temp\root", @"C:\Temp\root\..\other\a.txt", false)]
    [InlineData(@"C:\Temp\root\", @"C:\TEMP\ROOT\sub\a.txt", true)]
    public void IsUnder_is_strict(string root, string path, bool expected) => Assert.Equal(expected, SafeCleaner.IsUnder(root, path));

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"\\server\share\temp")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Users")]
    public void Dangerous_roots_are_refused(string root) => Assert.False(SafeCleaner.IsSafeRoot(root));

    public void Dispose()
    {
        // Remove junctions first (non-recursive) so cleanup never follows them.
        foreach (var d in Directory.EnumerateDirectories(_base, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(d);
        try { Directory.Delete(_base, true); } catch (IOException) { }
    }
}

public class CleanPlannerTests
{
    private sealed class FakeEnv(bool chromeRunning = false) : ICleanEnvironment
    {
        public string WindowsDirectory => @"C:\Windows";
        public IReadOnlyList<UserProfile> Profiles() => [new("S-1-5-21-1-2-3-1001", @"C:\Users\teacher")];
        public IReadOnlyList<string> FixedDriveRoots() => [@"C:\", @"D:\"];
        public bool IsProcessRunning(string processName) => chromeRunning && processName == "chrome";
    }

    [Fact]
    public void Only_whitelisted_roots_are_planned()
    {
        var targets = CleanPlanner.BuildTargets(new CleanPolicy(), new FakeEnv());
        string[] allowedPrefixes =
        [
            @"C:\Windows\Temp", @"C:\Users\teacher\AppData\Local\Temp", @"C:\Users\teacher\AppData\Local\Google\Chrome\User Data\",
            @"C:\Users\teacher\AppData\Local\Microsoft\Edge\User Data\", @"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001", @"D:\$Recycle.Bin\S-1-5-21-1-2-3-1001",
            @"C:\Users\teacher\Downloads",
        ];
        Assert.NotEmpty(targets);
        Assert.All(targets, t => Assert.Contains(allowedPrefixes, p => t.Root.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
        Assert.All(targets.Where(t => t.Root.Contains("User Data")), t => Assert.True(t.Root.EndsWith(@"\Cache") || t.Root.EndsWith(@"\Code Cache")));
        var downloads = Assert.Single(targets, t => t.Root.EndsWith("Downloads"));
        Assert.Equal(TimeSpan.FromDays(14), downloads.Rule.OlderThan);
    }

    [Fact]
    public void Disabled_targets_are_not_planned()
    {
        var policy = new CleanPolicy { WindowsTemp = false, UserTemp = false, BrowserCache = false, RecycleBin = false, Downloads = true, DownloadsRetentionDays = 30 };
        var t = Assert.Single(CleanPlanner.BuildTargets(policy, new FakeEnv()));
        Assert.Equal(TimeSpan.FromDays(30), t.Rule.OlderThan);
    }

    [Fact]
    public void Running_browser_is_skipped_with_note()
    {
        var notes = new List<string>();
        var targets = CleanPlanner.BuildTargets(new CleanPolicy(), new FakeEnv(chromeRunning: true), notes);
        Assert.DoesNotContain(targets, t => t.Root.Contains(@"Google\Chrome"));
        Assert.Contains(notes, n => n.Contains("Chrome"));
    }
}
