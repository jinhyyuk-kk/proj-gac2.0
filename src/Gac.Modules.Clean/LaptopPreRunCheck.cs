using System.Runtime.InteropServices;

namespace Gac.Modules.Clean;

/// <summary>
/// CHANGE-001 1.1, check site #5 (module pre-run) — the Clean module refuses to run on a laptop. It uses its own raw
/// SMBIOS chassis probe (own code, no shared helper); fails closed if the chassis cannot be read. This is defence in
/// depth only — the server is the authority.
/// </summary>
internal static class LaptopPreRunCheck
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint id, byte[]? buffer, uint size);

    private static readonly int[] Portable = [8, 9, 10, 11, 14, 30, 31, 32];

    public static bool IsLaptopOrUnknown()
    {
        try
        {
            var size = GetSystemFirmwareTable(0x52534D42, 0, null, 0);
            if (size == 0) return true; // fail closed
            var buf = new byte[size];
            if (GetSystemFirmwareTable(0x52534D42, 0, buf, size) == 0) return true;
            var i = 8;
            var found = false;
            while (i + 4 <= buf.Length)
            {
                int type = buf[i], len = buf[i + 1];
                if (len < 4 || type == 127) break;
                if (type == 3 && i + 5 < buf.Length)
                {
                    found = true;
                    if (Portable.Contains(buf[i + 5] & 0x7F)) return true;
                }
                var p = i + len;
                while (p + 1 < buf.Length && !(buf[p] == 0 && buf[p + 1] == 0)) p++;
                i = p + 2;
            }
            return !found; // no enclosure structure at all → fail closed
        }
        catch { return true; }
    }

    public const string Message = "이 프로그램은 데스크톱 PC에만 설치할 수 있습니다. 노트북에는 설치할 수 없습니다.";
}
