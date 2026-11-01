namespace Gac.Shared.Contracts;

/// <summary>
/// Raw values of the five independent hardware probes (CHANGE-001 section 1.1). The agent sends raw values only;
/// the server alone decides whether a device is a laptop (section 1.2). Any null/missing value is treated as a laptop.
/// </summary>
public sealed record HardwareProbes
{
    /// <summary>Probe A: WMI Win32_ComputerSystem.PCSystemType (2 = Mobile).</summary>
    public int? PcSystemType { get; init; }
    /// <summary>Probe B: WMI Win32_SystemEnclosure.ChassisTypes.</summary>
    public int[]? ChassisTypesWmi { get; init; }
    /// <summary>Probe C: chassis types parsed from the raw SMBIOS type-3 structure.</summary>
    public int[]? ChassisTypesSmbios { get; init; }
    /// <summary>Probe D: GetPwrCapabilities → SYSTEM_POWER_CAPABILITIES.LidPresent.</summary>
    public bool? LidPresent { get; init; }
    /// <summary>Probe D: GetPwrCapabilities → SYSTEM_POWER_CAPABILITIES.SystemBatteriesPresent.</summary>
    public bool? SystemBatteriesPresent { get; init; }
    /// <summary>Probe E: WMI Win32_Battery instance present.</summary>
    public bool? BatteryInstancePresent { get; init; }

    /// <summary>True when every probe field carries a value (used to reject partial reports).</summary>
    public bool IsComplete =>
        PcSystemType is not null && ChassisTypesWmi is not null && ChassisTypesSmbios is not null
        && LidPresent is not null && SystemBatteriesPresent is not null && BatteryInstancePresent is not null;
}

/// <summary>Console-install markers found on the machine (CHANGE-001 section 1.4). Raw data; the server decides order.</summary>
public sealed record InstallMarkers
{
    public bool ConsoleKeyPresent { get; init; }
    public bool ConsoleUninstallEntryPresent { get; init; }
    public bool ConsoleProgramFolderPresent { get; init; }
    public bool ConsoleProgramDataPresent { get; init; }
    public bool ConsoleTombstonePresent { get; init; }
    public DateTimeOffset? ConsoleFirstInstall { get; init; }
    /// <summary>The agent's own earliest install time from HKLM\SOFTWARE\GAC\Agent, if present.</summary>
    public DateTimeOffset? AgentFirstInstall { get; init; }

    public bool AnyConsoleMarker =>
        ConsoleKeyPresent || ConsoleUninstallEntryPresent || ConsoleProgramFolderPresent || ConsoleProgramDataPresent || ConsoleTombstonePresent;
}

/// <summary>SHA-256 of the agent's own executables and module DLLs (CHANGE-001 section 1.1 integrity report).</summary>
public sealed record IntegrityReport(IReadOnlyDictionary<string, string> FileHashes);

/// <summary>Everything the server needs to identify a device and apply the desktop-only + install-order rules.</summary>
public sealed record DeviceReport
{
    public required HardwareProbes Probes { get; init; }
    public required InstallMarkers Markers { get; init; }
    public IntegrityReport? Integrity { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    /// <summary>Wired Ethernet MAC addresses (upper hex, no separators), used for the hardware fingerprint.</summary>
    public string[] WiredMacs { get; init; } = [];
    public string ComputerName { get; init; } = "";
    /// <summary>True when the only active network connection is Wi-Fi (enrollment over Wi-Fi only is rejected).</summary>
    public bool WiredLinkActive { get; init; }
}

/// <summary>Why the server rejected a device. Mobile/portable reasons are permanent; others can be Owner-approved.</summary>
public enum DeviceVerdict
{
    Desktop,
    LaptopMobileType,
    LaptopChassis,
    LaptopLid,
    ProbeMissing,
    ProbeInconsistent,
    WifiOnly,
    IntegrityMismatch,
    NeedsBatteryReview,
    ConsoleFirst,
}

public sealed record DeviceDecision(DeviceVerdict Verdict, bool Accepted, bool NeedsReview, bool PermanentlyBlocked, string Reason);

/// <summary>Console view of a device that was refused enrollment (CHANGE-001 "차단된 기기").</summary>
public sealed record BlockedDevice(string Fingerprint, DateTimeOffset At, string? Ip, string ReportedName, string? Model, string Reason, bool PermanentlyBlocked);

/// <summary>Console view of a device awaiting Owner approval (battery-only signal, "확인 필요").</summary>
public sealed record PendingDevice(string Fingerprint, DateTimeOffset At, string? Ip, string ReportedName, string? Model, string Reason);
