namespace Gac.Shared.Contracts;

/// <summary>Effective settings for one agent, composed by the server from its group and global settings.</summary>
public sealed record AgentPolicy
{
    public CleanPolicy Clean { get; init; } = new();
    public ShutdownPolicy Shutdown { get; init; } = new();
    public HealthPolicy Health { get; init; } = new();
    public ErrorsPolicy Errors { get; init; } = new();
    public NetDiagPolicy NetDiag { get; init; } = new();
    public InspectPolicy Inspect { get; init; } = new();
    public SignedPresetBundle? SharedPresets { get; init; }
}

public sealed record CleanPolicy
{
    public bool Enabled { get; init; } = true;
    /// <summary>Dry-run is the default for the first deployment (CLAUDE.md 4.1).</summary>
    public bool DryRun { get; init; } = true;
    public int DownloadsRetentionDays { get; init; } = 14;
    public bool RunOnShutdown { get; init; } = true;
    /// <summary>Daily time "HH:mm" or null for no scheduled run.</summary>
    public string? ScheduleTime { get; init; }
    public bool WindowsTemp { get; init; } = true;
    public bool UserTemp { get; init; } = true;
    public bool BrowserCache { get; init; } = true;
    public bool RecycleBin { get; init; } = true;
    public bool Downloads { get; init; } = true;
}

public sealed record ShutdownPolicy
{
    public bool Enabled { get; init; }
    public string Time { get; init; } = "17:30";
    public DayOfWeek[] Days { get; init; } = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
    public int WarningMinutes { get; init; } = 5;
    public int ExtensionMinutes { get; init; } = 30;
    public int MaxExtensions { get; init; } = 2;
    public int ForceGraceMinutes { get; init; } = 10;
}

public sealed record HealthPolicy
{
    public int IntervalHours { get; init; } = 6;
    public int FreeSpaceWarnPercent { get; init; } = 10;
}

public sealed record ErrorsPolicy
{
    public int IntervalHours { get; init; } = 6;
    public int MaxEvents { get; init; } = 200;
}

public sealed record NetDiagPolicy
{
    /// <summary>Optional external address checked by netdiag. Off (null) by default.</summary>
    public string? ExternalTarget { get; init; }
    /// <summary>Optional LAN host name used for the DNS check. Defaults to the GAC Server host name.</summary>
    public string? DnsTestName { get; init; }
}

public sealed record InspectPolicy
{
    public string WallpaperPath { get; init; } = @"C:\Windows\Web\Wallpaper\Windows\img0.jpg";
    /// <summary>Program display-name patterns (case-insensitive substring) that are allowed.</summary>
    public string[] Allowlist { get; init; } = [];
}

public sealed record SignedPresetBundle(string PresetsJson, DateTimeOffset IssuedAt, string Signature);
