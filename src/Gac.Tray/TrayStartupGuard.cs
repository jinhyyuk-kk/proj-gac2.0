using System.Management;

namespace Gac.Tray;

/// <summary>
/// CHANGE-001 1.1, check site #4 — the Tray App refuses to run on a laptop using its own probes A (PCSystemType) and
/// E (Win32_Battery), with its own code (no shared helper). Fails closed when the probes cannot be read.
/// </summary>
internal static class TrayStartupGuard
{
    public const string Message = "이 프로그램은 데스크톱 PC에만 설치할 수 있습니다. 노트북에는 설치할 수 없습니다.";

    public static bool IsLaptopOrUnknown()
    {
        try
        {
            int? pcSystemType = null;
            using (var s = new ManagementObjectSearcher("SELECT PCSystemType FROM Win32_ComputerSystem"))
                foreach (var o in s.Get())
                    using (o)
                        if (o["PCSystemType"] is { } v) pcSystemType = Convert.ToInt32(v);
            if (pcSystemType is null) return true;      // fail closed
            if (pcSystemType == 2) return true;         // Mobile — always blocked

            var hasBattery = false;
            using (var s = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_Battery"))
                foreach (var o in s.Get()) { o.Dispose(); hasBattery = true; break; }
            // A battery may be a UPS on a desktop: trust the server's approval if this device was accepted/approved.
            if (hasBattery && !ServerApproved()) return true;
            return false;
        }
        catch { return true; }
    }

    /// <summary>True if the Agent recorded that the GAC Server accepted this device (handles UPS-on-desktop).</summary>
    private static bool ServerApproved()
    {
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GAC", "Agent", "state", "device-approved.txt");
            return File.Exists(p);
        }
        catch { return false; }
    }
}

