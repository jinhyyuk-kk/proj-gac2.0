using System.Management;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using Gac.Shared.Contracts;
using Microsoft.Win32;

namespace Gac.Agent.Hardware;

/// <summary>
/// Gathers the raw values the server needs (CHANGE-001 1.1/1.2): all five hardware probes, Console-install markers, the
/// agent's integrity hashes, wired MACs, manufacturer/model and whether a wired link is active. It only collects — it
/// never decides desktop vs. laptop; the server does that.
/// </summary>
public static class DeviceReportCollector
{
    public static DeviceReport Collect()
    {
        var (lid, batteries) = SafeD();
        var probes = new HardwareProbes
        {
            PcSystemType = Safe(ProbeA.PcSystemType),
            ChassisTypesWmi = Safe(ProbeB.ChassisTypes),
            ChassisTypesSmbios = Safe(ProbeC.ChassisTypes),
            LidPresent = lid,
            SystemBatteriesPresent = batteries,
            BatteryInstancePresent = Safe(ProbeE.AnyBattery),
        };
        var (manufacturer, model) = SystemModel();
        return new DeviceReport
        {
            Probes = probes,
            Markers = InstallMarkers(),
            Integrity = Integrity(),
            Manufacturer = manufacturer,
            Model = model,
            WiredMacs = WiredMacs(),
            ComputerName = Environment.MachineName,
            WiredLinkActive = WiredLinkActive(),
        };
    }

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
    private static (bool?, bool?) SafeD() { try { return ProbeD.Read(); } catch { return (null, null); } }

    private static (string?, string?) SystemModel()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (var o in s.Get())
                using (o)
                    return (o["Manufacturer"]?.ToString(), o["Model"]?.ToString());
        }
        catch { }
        return (null, null);
    }

    public static InstallMarkers InstallMarkers()
    {
        try
        {
            using var gac = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\GAC");
            using var console = gac?.OpenSubKey("Console");
            using var agent = gac?.OpenSubKey("Agent");
            var consoleKey = console != null;
            var tombstone = console?.GetValue("EverInstalled") != null;
            var consoleFirst = ReadTime(console, "InstallTime");
            var agentFirst = ReadTime(agent, "InstallTime");
            var uninstall = UninstallEntryExists("GAC 2.0 콘솔") || UninstallEntryExists("GAC 2.0 Console");
            var programFolder = Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GAC 2.0", "Console"));
            var programData = Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GAC", "Console"));
            return new InstallMarkers
            {
                ConsoleKeyPresent = consoleKey,
                ConsoleTombstonePresent = tombstone,
                ConsoleUninstallEntryPresent = uninstall,
                ConsoleProgramFolderPresent = programFolder,
                ConsoleProgramDataPresent = programData,
                ConsoleFirstInstall = consoleFirst,
                AgentFirstInstall = agentFirst,
            };
        }
        catch
        {
            // Registry inaccessible → fail closed: report a console marker so the server refuses (section 1.4).
            return new InstallMarkers { ConsoleKeyPresent = true, ConsoleFirstInstall = DateTimeOffset.MinValue };
        }
    }

    private static DateTimeOffset? ReadTime(RegistryKey? key, string name) =>
        key?.GetValue(name) is string s && long.TryParse(s, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    private static bool UninstallEntryExists(string displayName)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (k == null) continue;
            foreach (var sub in k.GetSubKeyNames())
            {
                using var e = k.OpenSubKey(sub);
                if (string.Equals(e?.GetValue("DisplayName") as string, displayName, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    /// <summary>Records the agent's own first-install time so the server can compare install order.</summary>
    public static void WriteAgentInstallMarker()
    {
        try
        {
            using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).CreateSubKey(@"SOFTWARE\GAC\Agent");
            if (k.GetValue("InstallTime") == null) k.SetValue("InstallTime", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
        }
        catch { }
    }

    public static IntegrityReport Integrity()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var dir = AppContext.BaseDirectory;
            foreach (var file in Directory.EnumerateFiles(dir, "*.*").Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                if (!Path.GetFileName(file).StartsWith("Gac.", StringComparison.OrdinalIgnoreCase)) continue;
                using var fs = File.OpenRead(file);
                dict[Path.GetFileName(file)] = Convert.ToHexString(SHA256.HashData(fs));
            }
        }
        catch { }
        return new IntegrityReport(dict);
    }

    public static string[] WiredMacs() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            .Select(n => n.GetPhysicalAddress().ToString())
            .Where(m => m.Length >= 12)
            .Distinct()
            .ToArray();

    public static bool WiredLinkActive() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet && n.OperationalStatus == OperationalStatus.Up
                      && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase));
}
