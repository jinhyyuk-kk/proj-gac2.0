using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Gac.Modules.Clean;

/// <summary>
/// Handle-based file operations that defeat junction/symlink swap races while running as SYSTEM in user-writable folders.
/// Directories are opened without FILE_SHARE_DELETE, so while we hold the handle nobody can rename or replace them.
/// Files are opened with FILE_FLAG_OPEN_REPARSE_POINT and deleted through that handle, so the object deleted is
/// exactly the object that was checked.
/// </summary>
internal static class NativeFs
{
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_LIST_DIRECTORY = 0x0001;
    private const uint DELETE = 0x00010000;
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const int FileAttributeTagInfo = 9;
    private const int FileDispositionInfo = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ATTRIBUTE_TAG_INFO { public uint FileAttributes; public uint ReparseTag; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_DISPOSITION_INFO { [MarshalAs(UnmanagedType.U1)] public bool DeleteFile; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int cls, out FILE_ATTRIBUTE_TAG_INFO info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int cls, ref FILE_DISPOSITION_INFO info, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, StringBuilder buf, uint size, uint flags);

    private static string Long(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + path;

    /// <summary>Opens a directory and pins it (no share-delete). Returns null if it is a reparse point or cannot be opened.</summary>
    public static SafeFileHandle? OpenPinnedDirectory(string path)
    {
        var h = CreateFileW(Long(path), FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        if (!Attributes(h, out var attrs) || (attrs & FileAttributes.ReparsePoint) != 0 || (attrs & FileAttributes.Directory) == 0) { h.Dispose(); return null; }
        return h;
    }

    /// <summary>Opens a file for inspection/deletion without following reparse points.</summary>
    public static SafeFileHandle? OpenFile(string path, bool forDelete, out FileAttributes attrs)
    {
        attrs = 0;
        var access = FILE_READ_ATTRIBUTES | SYNCHRONIZE | (forDelete ? DELETE : 0);
        var h = CreateFileW(Long(path), access, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        if (!Attributes(h, out attrs)) { h.Dispose(); return null; }
        return h;
    }

    public static bool Attributes(SafeFileHandle h, out FileAttributes attrs)
    {
        var ok = GetFileInformationByHandleEx(h, FileAttributeTagInfo, out var info, (uint)Marshal.SizeOf<FILE_ATTRIBUTE_TAG_INFO>());
        attrs = (FileAttributes)info.FileAttributes;
        return ok;
    }

    /// <summary>Normalized final path (no \\?\ prefix) of the object behind the handle.</summary>
    public static string? FinalPath(SafeFileHandle h)
    {
        var sb = new StringBuilder(1024);
        var n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
        if (n == 0) return null;
        if (n > sb.Capacity) { sb = new StringBuilder((int)n + 1); n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0); if (n == 0) return null; }
        var s = sb.ToString();
        if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + s[8..];
        return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
    }

    /// <summary>Marks the opened object for deletion. Throws <see cref="IOException"/> on failure.</summary>
    public static void DeleteByHandle(SafeFileHandle h)
    {
        var info = new FILE_DISPOSITION_INFO { DeleteFile = true };
        if (!SetFileInformationByHandle(h, FileDispositionInfo, ref info, (uint)Marshal.SizeOf<FILE_DISPOSITION_INFO>()))
            throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }
}
