namespace Gac.Tamper.Tests;

/// <summary>
/// CHANGE-001 1.1 tamper test: fails the build if any of the six distributed client check sites, the three server
/// enforcement sites, or the install-order checks is removed or no longer calls its probe. Removing a check therefore
/// also breaks the test suite. It scans the source tree for the required markers at each site.
/// </summary>
public class TamperTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Gac.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative)
    {
        var path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"check site file missing: {relative}");
        return File.ReadAllText(path);
    }

    [Theory]
    // --- six client check sites (CHANGE-001 1.1) ---
    [InlineData("installer/GAC-Agent.iss", "PCSystemType", "installer laptop probe A")]
    [InlineData("installer/GAC-Agent.iss", "Win32_Battery", "installer laptop probe D")]
    [InlineData("src/Gac.Agent/Hardware/ServiceStartupGuard.cs", "ProbeB.ChassisTypes", "service startup probe B")]
    [InlineData("src/Gac.Agent/Hardware/ServiceStartupGuard.cs", "ProbeC.ChassisTypes", "service startup probe C")]
    [InlineData("src/Gac.Agent/Runtime/AgentServices.cs", "core.DeviceReport()", "heartbeat carries device report")]
    [InlineData("src/Gac.Tray/TrayStartupGuard.cs", "PCSystemType", "tray startup probe A")]
    [InlineData("src/Gac.Tray/TrayStartupGuard.cs", "Win32_Battery", "tray startup probe E")]
    [InlineData("src/Gac.Tray/Program.cs", "TrayStartupGuard.IsLaptopOrUnknown", "tray startup wired in")]
    [InlineData("src/Gac.Modules.Clean/CleanModule.cs", "LaptopPreRunCheck", "module pre-run check")]
    [InlineData("src/Gac.Agent/Hardware/DeviceReportCollector.cs", "ProbeA.PcSystemType", "enrollment collects probe A")]
    [InlineData("src/Gac.Agent/Hardware/DeviceReportCollector.cs", "ProbeE.AnyBattery", "enrollment collects probe E")]
    [InlineData("src/Gac.Agent/AgentCli.cs", "DeviceReportCollector.Collect()", "enrollment sends device report")]
    // --- three server enforcement sites (CHANGE-001 1.2) ---
    [InlineData("src/Gac.Server/Hosting/Endpoints.cs", "devices.Evaluate", "server enforces at enrollment")]
    [InlineData("src/Gac.Server/Hosting/Endpoints.cs", "devices.CheckHeartbeat", "server enforces at heartbeat")]
    [InlineData("src/Gac.Server/Hosting/Endpoints.cs", "pcs.IsRevoked", "server enforces at command dispatch")]
    // --- install-order checks (CHANGE-001 1.4) ---
    [InlineData("installer/GAC-Agent.iss", "ConsoleCameFirst", "agent installer refuses console-first")]
    [InlineData("installer/GAC-Console.iss", "EverInstalled", "console writes install-order tombstone")]
    [InlineData("src/Gac.Server/Devices/DeviceGuard.cs", "ConsoleCameFirst", "server enforces install order")]
    public void Check_site_is_present(string file, string marker, string site)
    {
        Assert.True(Read(file).Contains(marker, StringComparison.Ordinal), $"Laptop/install-order check removed: {site} ({file} must contain '{marker}').");
    }

    [Fact]
    public void Probe_implementations_all_exist()
    {
        var probes = Read("src/Gac.Agent/Hardware/Probes.cs");
        foreach (var p in new[] { "class ProbeA", "class ProbeB", "class ProbeC", "class ProbeD", "class ProbeE" })
            Assert.Contains(p, probes);
    }
}
