using System.Diagnostics;
using Gac.Shared.Contracts;
using Microsoft.Win32;

namespace Gac.Modules.Clean;

public sealed record UserProfile(string Sid, string Path);

/// <summary>Machine facts the planner needs. Abstracted so the whitelist can be unit-tested.</summary>
public interface ICleanEnvironment
{
    string WindowsDirectory { get; }
    IReadOnlyList<UserProfile> Profiles();
    IReadOnlyList<string> FixedDriveRoots();
    bool IsProcessRunning(string processName);
}

public sealed class WindowsCleanEnvironment : ICleanEnvironment
{
    public string WindowsDirectory => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public IReadOnlyList<UserProfile> Profiles()
    {
        var list = new List<UserProfile>();
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        if (key == null) return list;
        foreach (var sid in key.GetSubKeyNames())
        {
            if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal)) continue; // real user accounts only
            using var sub = key.OpenSubKey(sid);
            if (sub?.GetValue("ProfileImagePath") is string p)
            {
                var path = Environment.ExpandEnvironmentVariables(p);
                if (Directory.Exists(path)) list.Add(new UserProfile(sid, path));
            }
        }
        return list;
    }

    public IReadOnlyList<string> FixedDriveRoots() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList();

    public bool IsProcessRunning(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }
}

/// <summary>
/// Builds the hard-coded whitelist of clean targets (CLAUDE.md 4.1). Nothing outside these folders is ever deleted.
/// </summary>
public static class CleanPlanner
{
    /// <summary>Temp files younger than this are left alone (installers may be using them).</summary>
    public static readonly TimeSpan TempMinAge = TimeSpan.FromHours(1);

    public static readonly (string Process, string Name, string[] UserDataPath)[] Browsers =
    [
        ("chrome", "Chrome", ["AppData", "Local", "Google", "Chrome", "User Data"]),
        ("msedge", "Edge", ["AppData", "Local", "Microsoft", "Edge", "User Data"]),
    ];

    public static IReadOnlyList<CleanTarget> BuildTargets(CleanPolicy policy, ICleanEnvironment env, ICollection<string>? notes = null)
    {
        var targets = new List<CleanTarget>();
        var profiles = env.Profiles();

        if (policy.WindowsTemp)
            targets.Add(new("Windows 임시 파일", Path.Combine(env.WindowsDirectory, "Temp"), CleanRule.All(TempMinAge)));

        if (policy.UserTemp)
            foreach (var p in profiles)
                targets.Add(new($"사용자 임시 파일 ({Path.GetFileName(p.Path)})", Path.Combine(p.Path, "AppData", "Local", "Temp"), CleanRule.All(TempMinAge)));

        if (policy.BrowserCache)
        {
            foreach (var (process, name, userData) in Browsers)
            {
                if (env.IsProcessRunning(process)) { notes?.Add($"{name} 실행 중이라 캐시 정리를 건너뜀"); continue; }
                foreach (var p in profiles)
                {
                    var dataDir = Path.Combine([p.Path, .. userData]);
                    if (!Directory.Exists(dataDir)) continue;
                    IEnumerable<string> browserProfiles;
                    try
                    {
                        browserProfiles = Directory.EnumerateDirectories(dataDir)
                            .Where(d => Path.GetFileName(d) is "Default" or "Guest Profile" || Path.GetFileName(d).StartsWith("Profile ", StringComparison.Ordinal))
                            .ToList();
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                    foreach (var bp in browserProfiles)
                    {
                        if (new DirectoryInfo(bp).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                        targets.Add(new($"{name} 캐시 ({Path.GetFileName(p.Path)})", Path.Combine(bp, "Cache"), CleanRule.All(TimeSpan.Zero)));
                        targets.Add(new($"{name} 코드 캐시 ({Path.GetFileName(p.Path)})", Path.Combine(bp, "Code Cache"), CleanRule.All(TimeSpan.Zero)));
                    }
                }
            }
        }

        if (policy.RecycleBin)
        {
            var sids = profiles.Select(p => p.Sid).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var drive in env.FixedDriveRoots())
            {
                var bin = Path.Combine(drive, "$Recycle.Bin");
                foreach (var sid in sids)
                {
                    var rule = CleanRule.All(TimeSpan.Zero) with { KeepFileNames = new HashSet<string>(["desktop.ini"], StringComparer.OrdinalIgnoreCase) };
                    targets.Add(new($"휴지통 ({drive.TrimEnd('\\')})", Path.Combine(bin, sid), rule));
                }
            }
        }

        if (policy.Downloads)
        {
            var days = Math.Clamp(policy.DownloadsRetentionDays, 1, 365);
            foreach (var p in profiles)
                targets.Add(new($"다운로드 {days}일 지난 파일 ({Path.GetFileName(p.Path)})", Path.Combine(p.Path, "Downloads"), CleanRule.Older(TimeSpan.FromDays(days))));
        }
        return targets;
    }
}
