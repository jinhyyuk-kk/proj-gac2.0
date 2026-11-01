using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Gac.IconTool;

/// <summary>
/// Extracts the full main icon group (every size and colour depth) from a PE file's resources (CHANGE-002 §1.1).
/// The file is mapped as a data file only (LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE), so no code in
/// it ever runs. The first RT_GROUP_ICON found by EnumResourceNames is the one Explorer shows for the file; its
/// GRPICONDIR lists GRPICONDIRENTRY records (14 bytes, packed, ending in the RT_ICON id), each RT_ICON resource is the
/// raw image (PNG or DIB) that goes into the .ico unchanged.
/// </summary>
public static class IconExtractor
{
    private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
    private const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x00000020;
    private static readonly IntPtr RT_ICON = 3;
    private static readonly IntPtr RT_GROUP_ICON = 14;

    public static IReadOnlyList<IcoFrame> ExtractMainIcon(string peFile)
    {
        var module = LoadLibraryExW(peFile, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
        if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open {peFile} as a resource file.");
        try
        {
            byte[]? group = null;
            EnumResourceNamesW(module, RT_GROUP_ICON, (h, _, name, _) =>
            {
                group = ReadResource(h, name, RT_GROUP_ICON); // the name pointer is only valid inside the callback
                return false; // stop after the first group
            }, IntPtr.Zero);
            // EnumResourceNames returns FALSE when the callback stops it, so success is judged by "group" only.
            if (group == null) throw new InvalidDataException($"{Path.GetFileName(peFile)} has no icon group resource.");

            var g = group.AsSpan();
            int count = BinaryPrimitives.ReadUInt16LittleEndian(g[4..]);
            var frames = new List<IcoFrame>(count);
            for (var i = 0; i < count; i++)
            {
                var e = g.Slice(6 + 14 * i, 14);
                var id = BinaryPrimitives.ReadUInt16LittleEndian(e[12..]);
                var data = ReadResource(module, id, RT_ICON) ?? throw new InvalidDataException($"Icon image {id} listed in the group is missing.");
                frames.Add(new IcoFrame(e[0] == 0 ? 256 : e[0], e[1] == 0 ? 256 : e[1], e[2],
                    BinaryPrimitives.ReadUInt16LittleEndian(e[4..]), BinaryPrimitives.ReadUInt16LittleEndian(e[6..]), data));
            }
            return frames;
        }
        finally { FreeLibrary(module); }
    }

    private static byte[]? ReadResource(IntPtr module, IntPtr name, IntPtr type)
    {
        var res = FindResourceW(module, name, type);
        if (res == IntPtr.Zero) return null;
        var size = SizeofResource(module, res);
        var handle = LoadResource(module, res);
        var ptr = handle == IntPtr.Zero ? IntPtr.Zero : LockResource(handle);
        if (ptr == IntPtr.Zero || size == 0) return null;
        var bytes = new byte[size];
        Marshal.Copy(ptr, bytes, 0, (int)size);
        return bytes;
    }

    private delegate bool EnumResNameProc(IntPtr hModule, IntPtr lpType, IntPtr lpName, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumResourceNamesW(IntPtr hModule, IntPtr lpType, EnumResNameProc lpEnumFunc, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindResourceW(IntPtr hModule, IntPtr lpName, IntPtr lpType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LockResource(IntPtr hResData);
}
