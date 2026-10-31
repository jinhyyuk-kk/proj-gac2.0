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
/// <list type="bullet">
/// <item>never follows or deletes symlinks/junctions/mount points (checked on the opened handle, not by path);</item>
/// <item>the root and every directory being walked stay pinned by a handle without share-delete, so they cannot be swapped
/// for a junction while we work (the agent runs as SYSTEM inside user-writable folders);</item>
/// <item>every object is re-verified by its final path to be strictly inside the root, and deleted through the same handle;</item>
/// <item>locked, read-only or inaccessible files are skipped; one bad file never fails the run.</item>
/// </list>
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

        if (!IsSafeRoot(root) || !Directory.Exists(root)) return new(target.Name, 0, 0, 0);

        using (var rootHandle = NativeFs.OpenPinnedDirectory(root))
        {
            // Null: the root is a reparse point. Final path mismatch: some parent component is a junction/symlink.
            if (rootHandle == null || !SamePath(NativeFs.FinalPath(rootHandle), root)) return new(target.Name, 0, 0, 1);
            Walk(root, isRoot: true);
        }
        return new CleanTargetResult(target.Name, bytes, files, skipped);

        // Precondition: dirPath is pinned by the caller. Returns true when the directory is empty afterwards.
        bool Walk(string dirPath, bool isRoot)
        {
            List<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dirPath).EnumerateFileSystemInfos("*", new EnumerationOptions
                    { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false }).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { skipped++; return false; }

            var empty = true;
            var nowUtc = time.GetUtcNow().UtcDateTime;
            foreach (var entry in entries)
            {
                var childPath = Path.Combine(dirPath, entry.Name);
                if (!IsUnder(root, childPath)) { skipped++; empty = false; continue; }

                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) { skipped++; empty = false; continue; }

                if (entry.Attributes.HasFlag(FileAttributes.Directory))
                {
                    bool subEmpty;
                    using (var dh = NativeFs.OpenPinnedDirectory(childPath))
                    {
                        if (dh == null || !SamePath(NativeFs.FinalPath(dh), childPath)) { skipped++; empty = false; continue; }
                        subEmpty = Walk(childPath, isRoot: false);
                    }
                    if (subEmpty && target.Rule.RemoveEmptyDirectories)
                    {
                        if (!dryRun && !TryDeleteEmptyDirectory(childPath)) empty = false;
                    }
                    else empty = false;
                    continue;
                }

                if (!ShouldDelete(entry, target.Rule, nowUtc)) { empty = false; continue; }

                using var fh = NativeFs.OpenFile(childPath, forDelete: !dryRun, out var attrs);
                if (fh == null) { skipped++; empty = false; continue; } // locked / in use / access denied
                if ((attrs & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || (attrs & FileAttributes.ReadOnly) != 0)
                { skipped++; empty = false; continue; }
                var final = NativeFs.FinalPath(fh);
                if (final == null || !IsUnder(root, final)) { skipped++; empty = false; continue; }

                var len = ((FileInfo)entry).Length;
                if (!dryRun)
                {
                    try { NativeFs.DeleteByHandle(fh); }
                    catch (IOException) { skipped++; empty = false; continue; }
                }
                bytes += len; files++;
                if (samples != null && samples.Count < MaxSamples) samples.Add(childPath);
            }
            return empty && !isRoot;
        }

        bool TryDeleteEmptyDirectory(string path)
        {
            // Re-open without following reparse points; if it was swapped for a junction we see the reparse flag and stop.
            using var h = NativeFs.OpenFile(path, forDelete: true, out var attrs);
            if (h == null || (attrs & FileAttributes.ReparsePoint) != 0 || (attrs & FileAttributes.Directory) == 0) return false;
            if (!SamePath(NativeFs.FinalPath(h), path)) return false;
            try { NativeFs.DeleteByHandle(h); return true; }
            catch (IOException) { return false; } // not empty or in use
        }
    }

    private static bool ShouldDelete(FileSystemInfo f, CleanRule rule, DateTime nowUtc)
    {
        if (rule.KeepFileNames.Contains(f.Name)) return false;
        DateTime newest;
        try { newest = f.LastWriteTimeUtc > f.CreationTimeUtc ? f.LastWriteTimeUtc : f.CreationTimeUtc; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        var age = nowUtc - newest;
        if (rule.OlderThan is { } older) return age > older;
        return age >= rule.MinAge;
    }

    private static bool SamePath(string? a, string b) =>
        a != null && string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

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
