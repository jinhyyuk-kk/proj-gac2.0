using Gac.Shared.Contracts;

namespace Gac.Modules.Clean;

/// <summary>How files under a target root are selected.</summary>
public sealed record CleanRule(TimeSpan? OlderThan, TimeSpan MinAge, bool RemoveEmptyDirectories, IReadOnlySet<string> KeepFileNames)
{
    public static CleanRule All(TimeSpan minAge) => new(null, minAge, true, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    public static CleanRule Older(TimeSpan age) => new(age, TimeSpan.Zero, false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>One whitelisted folder. Only files below <see cref="Root"/> can ever be touched.</summary>
public sealed record CleanTarget(string Name, string Root, CleanRule Rule);

/// <summary>
/// Deletes files under one whitelisted root. Safety rules (CLAUDE.md 4.1 and section 8 rule 6):
/// never follows or deletes symlinks/junctions, never touches a path outside the root, skips locked files,
/// and never throws because of a single file.
/// </summary>
public sealed class SafeCleaner(TimeProvider time)
{
    public const int MaxSamples = 50;

    public CleanTargetResult Clean(CleanTarget target, bool dryRun, ICollection<string>? samples = null)
    {
        long bytes = 0;
        int files = 0, skipped = 0;

        string root;
        try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Root)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return new(target.Name, 0, 0, 0); }

        if (!IsSafeRoot(root)) return new(target.Name, 0, 0, 0);
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists) return new(target.Name, 0, 0, 0);
        if (rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)) return new(target.Name, 0, 0, 1);

        var now = time.GetUtcNow().UtcDateTime;
        Walk(rootInfo, isRoot: true);
        return new CleanTargetResult(target.Name, bytes, files, skipped);

        // Returns true when the directory is empty afterwards.
        bool Walk(DirectoryInfo dir, bool isRoot)
        {
            IEnumerable<FileSystemInfo> entries;
            try { entries = dir.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false }).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { skipped++; return false; }

            var empty = true;
            foreach (var entry in entries)
            {
                if (!IsUnder(root, entry.FullName)) { skipped++; empty = false; continue; }
                FileAttributes attrs;
                try { attrs = entry.Attributes; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; empty = false; continue; }

                // Never follow or remove reparse points (symlinks, junctions, mount points, cloud placeholders).
                if (attrs.HasFlag(FileAttributes.ReparsePoint)) { skipped++; empty = false; continue; }

                if (entry is DirectoryInfo sub)
                {
                    var subEmpty = Walk(sub, isRoot: false);
                    if (subEmpty && target.Rule.RemoveEmptyDirectories)
                    {
                        if (!dryRun)
                        {
                            try { sub.Delete(recursive: false); }
                            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { empty = false; }
                        }
                    }
                    else empty = false;
                    continue;
                }

                var file = (FileInfo)entry;
                if (!ShouldDelete(file, target.Rule, now)) { empty = false; continue; }
                long len;
                try { len = file.Length; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; empty = false; continue; }

                if (dryRun)
                {
                    bytes += len; files++;
                    if (samples != null && samples.Count < MaxSamples) samples.Add(file.FullName);
                    continue;
                }
                try
                {
                    if (attrs.HasFlag(FileAttributes.ReadOnly)) file.Attributes = attrs & ~FileAttributes.ReadOnly;
                    file.Delete();
                    bytes += len; files++;
                    if (samples != null && samples.Count < MaxSamples) samples.Add(file.FullName);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    skipped++; empty = false; // locked or in use
                }
            }
            return empty && !isRoot;
        }
    }

    private static bool ShouldDelete(FileInfo f, CleanRule rule, DateTime nowUtc)
    {
        if (rule.KeepFileNames.Contains(f.Name)) return false;
        DateTime newest;
        try { newest = f.LastWriteTimeUtc > f.CreationTimeUtc ? f.LastWriteTimeUtc : f.CreationTimeUtc; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        var age = nowUtc - newest;
        if (rule.OlderThan is { } older) return age > older;
        return age >= rule.MinAge;
    }

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="root"/> (not equal, not a sibling with the same prefix).</summary>
    public static bool IsUnder(string root, string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { return false; }
        var r = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return full.StartsWith(r, StringComparison.OrdinalIgnoreCase) && full.Length > r.Length;
    }

    /// <summary>Refuses drive roots, UNC shares and system folders as roots even if misconfigured.</summary>
    public static bool IsSafeRoot(string root)
    {
        if (root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        if (Path.GetPathRoot(root) is { } pr && Path.TrimEndingDirectorySeparator(pr).Equals(root, StringComparison.OrdinalIgnoreCase)) return false;
        var depth = root.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;
        if (depth < 2) return false;
        string[] forbidden =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "",
        ];
        return !forbidden.Any(f => f.Length > 0 && Path.TrimEndingDirectorySeparator(f).Equals(root, StringComparison.OrdinalIgnoreCase));
    }
}
