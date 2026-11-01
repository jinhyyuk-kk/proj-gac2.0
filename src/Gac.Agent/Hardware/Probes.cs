using System.Management;
using System.Runtime.InteropServices;

namespace Gac.Agent.Hardware;

// CHANGE-001 section 1.1: the five independent probes. Each is implemented separately with its own code and fails
// closed (returns null / throws → the collector reports the absence and the SERVER treats missing data as a laptop).
// There is deliberately NO shared IsLaptop helper; the decision is made only by the server from these raw values.

/// <summary>Probe A — WMI Win32_ComputerSystem.PCSystemType (2 = Mobile).</summary>
public static class ProbeA
{
    public static int? PcSystemType()
    {
        using var searcher = new ManagementObjectSearcher("SELECT PCSystemType FROM Win32_ComputerSystem");
        foreach (var o in searcher.Get())
            using (o)
                if (o["PCSystemType"] is { } v) return Convert.ToInt32(v);
        return null;
    }
}

/// <summary>Probe B — WMI Win32_SystemEnclosure.ChassisTypes.</summary>
public static class ProbeB
{
    public static int[]? ChassisTypes()
    {
        var list = new List<int>();
        using var searcher = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
        foreach (var o in searcher.Get())
            using (o)
                if (o["ChassisTypes"] is ushort[] types)
                    list.AddRange(types.Select(t => (int)t));
        return list.Count > 0 ? list.ToArray() : null;
    }
}

/// <summary>Probe C — raw SMBIOS table (GetSystemFirmwareTable 'RSMB'); parse the type-3 System Enclosure structure.</summary>
public static class ProbeC
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint id, byte[]? buffer, uint size);

    private const uint Rsmb = 0x52534D42; // 'RSMB'

    public static int[]? ChassisTypes()
    {
        var size = GetSystemFirmwareTable(Rsmb, 0, null, 0);
        if (size == 0) return null;
        var buf = new byte[size];
        if (GetSystemFirmwareTable(Rsmb, 0, buf, size) == 0) return null;

        // RawSMBIOSData header is 8 bytes; the SMBIOS structure table follows.
        var i = 8;
        var result = new List<int>();
        while (i + 4 <= buf.Length)
        {
            int type = buf[i];
            int len = buf[i + 1];
            if (len < 4) break;
            if (type == 127) break; // end-of-table
            if (type == 3 && i + 5 < buf.Length) // System Enclosure: byte 0x05 is the chassis type (bit 7 = lock flag)
                result.Add(buf[i + 5] & 0x7F);
            // Skip the formatted area, then the unformatted string-set terminated by 0x0000.
            var p = i + len;
            while (p + 1 < buf.Length && !(buf[p] == 0 && buf[p + 1] == 0)) p++;
            i = p + 2;
        }
        return result.Count > 0 ? result.ToArray() : null;
    }
}

/// <summary>
/// Probe D — power capabilities. <c>LidPresent</c> comes from GetPwrCapabilities (read from a safely over-sized native
/// buffer at the stable offset 2, so a struct-size mismatch can never corrupt memory). The battery signal comes from
/// GetSystemPowerStatus.BatteryFlag, which is the reliable, version-stable way to tell whether a system battery exists.
/// </summary>
public static class ProbeD
{
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool GetPwrCapabilities(IntPtr caps);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    public static (bool? Lid, bool? Batteries) Read()
    {
        bool? lid = null, batteries = null;
        const int bufSize = 1024; // far larger than any SYSTEM_POWER_CAPABILITIES revision
        var buf = Marshal.AllocHGlobal(bufSize);
        try
        {
            for (var i = 0; i < bufSize; i++) Marshal.WriteByte(buf, i, 0);
            if (GetPwrCapabilities(buf)) lid = Marshal.ReadByte(buf, 2) != 0; // offset 2 = LidPresent (stable)
        }
        finally { Marshal.FreeHGlobal(buf); }

        if (GetSystemPowerStatus(out var s))
            batteries = s.BatteryFlag != 128 && s.BatteryFlag != 255; // 128 = no system battery, 255 = unknown
        return (lid, batteries);
    }
}

/// <summary>Probe E — WMI Win32_Battery instance present.</summary>
public static class ProbeE
{
    public static bool? AnyBattery()
    {
        using var searcher = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_Battery");
        foreach (var o in searcher.Get()) { o.Dispose(); return true; }
        return false;
    }
}
