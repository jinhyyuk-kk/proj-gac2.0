using Gac.Server.Devices;
using Gac.Shared.Contracts;

namespace Gac.Server.Tests;

public class DeviceGuardTests
{
    private static HardwareProbes Desktop() => new()
    {
        PcSystemType = 1, // Desktop
        ChassisTypesWmi = [3], // Desktop
        ChassisTypesSmbios = [3],
        LidPresent = false,
        SystemBatteriesPresent = false,
        BatteryInstancePresent = false,
    };

    private static DeviceReport Report(HardwareProbes p, InstallMarkers? markers = null) => new()
    {
        Probes = p,
        Markers = markers ?? new InstallMarkers { AgentFirstInstall = DateTimeOffset.Now },
        Manufacturer = "Dell",
        Model = "OptiPlex 7090",
        WiredMacs = ["AABBCCDDEEFF"],
        ComputerName = "LAB-01",
        WiredLinkActive = true,
    };

    private readonly DeviceGuard _guard = new();

    [Fact]
    public void Desktop_is_accepted()
    {
        var d = _guard.Decide(Report(Desktop()), seenAsConsoleFirst: false);
        Assert.True(d.Accepted);
        Assert.Equal(DeviceVerdict.Desktop, d.Verdict);
    }

    [Fact]
    public void Mobile_pc_system_type_is_permanently_blocked()
    {
        var d = _guard.Decide(Report(Desktop() with { PcSystemType = 2 }), false);
        Assert.False(d.Accepted);
        Assert.True(d.PermanentlyBlocked);
        Assert.Equal(DeviceVerdict.LaptopMobileType, d.Verdict);
        Assert.Equal(DeviceGuard.LaptopMessage, d.Reason);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(14)]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(32)]
    public void Each_portable_chassis_is_permanently_blocked(int chassis)
    {
        var d = _guard.Decide(Report(Desktop() with { ChassisTypesWmi = [chassis] }), false);
        Assert.False(d.Accepted);
        Assert.True(d.PermanentlyBlocked);
        Assert.Equal(DeviceVerdict.LaptopChassis, d.Verdict);
    }

    [Fact]
    public void Smbios_laptop_while_wmi_desktop_is_still_blocked()
    {
        var d = _guard.Decide(Report(Desktop() with { ChassisTypesWmi = [3], ChassisTypesSmbios = [10] }), false);
        Assert.False(d.Accepted);
        Assert.Equal(DeviceVerdict.LaptopChassis, d.Verdict);
    }

    [Fact]
    public void Lid_present_is_blocked()
    {
        var d = _guard.Decide(Report(Desktop() with { LidPresent = true }), false);
        Assert.False(d.Accepted);
        Assert.Equal(DeviceVerdict.LaptopLid, d.Verdict);
    }

    [Fact]
    public void Missing_probe_is_treated_as_laptop()
    {
        var d = _guard.Decide(Report(Desktop() with { PcSystemType = null }), false);
        Assert.False(d.Accepted);
        Assert.Equal(DeviceVerdict.ProbeMissing, d.Verdict);
    }

    [Fact]
    public void Both_chassis_unknown_is_inconsistent()
    {
        var d = _guard.Decide(Report(Desktop() with { ChassisTypesWmi = [2], ChassisTypesSmbios = [] }), false);
        Assert.False(d.Accepted);
        Assert.Equal(DeviceVerdict.ProbeInconsistent, d.Verdict);
    }

    [Fact]
    public void Wifi_only_is_rejected()
    {
        var r = Report(Desktop()) with { WiredLinkActive = false, WiredMacs = [] };
        var d = _guard.Decide(r, false);
        Assert.False(d.Accepted);
        Assert.Equal(DeviceVerdict.WifiOnly, d.Verdict);
    }

    [Fact]
    public void Battery_only_desktop_goes_to_review_not_blocked()
    {
        var d = _guard.Decide(Report(Desktop() with { BatteryInstancePresent = true }), false);
        Assert.False(d.Accepted);
        Assert.True(d.NeedsReview);
        Assert.False(d.PermanentlyBlocked);
        Assert.Equal(DeviceVerdict.NeedsBatteryReview, d.Verdict);
    }

    [Fact]
    public void Console_first_flag_blocks_enrollment()
    {
        var d = _guard.Decide(Report(Desktop()), seenAsConsoleFirst: true);
        Assert.False(d.Accepted);
        Assert.Equal(DeviceVerdict.ConsoleFirst, d.Verdict);
    }

    [Fact]
    public void Console_markers_older_than_agent_block_enrollment()
    {
        var markers = new InstallMarkers
        {
            ConsoleKeyPresent = true,
            ConsoleFirstInstall = DateTimeOffset.Now.AddDays(-10),
            AgentFirstInstall = DateTimeOffset.Now.AddDays(-1),
        };
        var d = _guard.Decide(Report(Desktop(), markers), false);
        Assert.Equal(DeviceVerdict.ConsoleFirst, d.Verdict);
    }

    [Fact]
    public void Console_installed_after_agent_is_allowed()
    {
        var markers = new InstallMarkers
        {
            ConsoleKeyPresent = true,
            ConsoleFirstInstall = DateTimeOffset.Now.AddDays(-1),
            AgentFirstInstall = DateTimeOffset.Now.AddDays(-10),
        };
        var d = _guard.Decide(Report(Desktop(), markers), false);
        Assert.True(d.Accepted);
    }

    [Fact]
    public void Tombstone_without_agent_blocks()
    {
        var markers = new InstallMarkers { ConsoleTombstonePresent = true, AgentFirstInstall = null };
        var d = _guard.Decide(Report(Desktop(), markers), false);
        Assert.Equal(DeviceVerdict.ConsoleFirst, d.Verdict);
    }

    [Fact]
    public void Integrity_mismatch_is_rejected_when_hashes_known()
    {
        var guard = new DeviceGuard(new HashSet<string> { "AAAA" });
        var report = Report(Desktop()) with { Integrity = new IntegrityReport(new Dictionary<string, string> { ["Gac.Agent.exe"] = "BBBB" }) };
        var d = guard.Decide(report, false);
        Assert.Equal(DeviceVerdict.IntegrityMismatch, d.Verdict);
    }

    [Fact]
    public void Integrity_match_is_accepted()
    {
        var guard = new DeviceGuard(new HashSet<string> { "AAAA" });
        var report = Report(Desktop()) with { Integrity = new IntegrityReport(new Dictionary<string, string> { ["Gac.Agent.exe"] = "aaaa" }) };
        var d = guard.Decide(report, false);
        Assert.True(d.Accepted);
    }

    [Fact]
    public void Fingerprint_is_stable_and_mac_order_independent()
    {
        var a = DeviceGuard.Fingerprint(Report(Desktop()) with { WiredMacs = ["AA-BB-CC", "11-22-33"] });
        var b = DeviceGuard.Fingerprint(Report(Desktop()) with { WiredMacs = ["112233", "aabbcc"] });
        Assert.Equal(a, b);
    }
}
