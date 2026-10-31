namespace Gac.Shared.Contracts;

// ---- clean ----
public sealed record CleanRequest(bool? DryRun);
public sealed record CleanTargetResult(string Target, long Bytes, int Files, int Skipped);
public sealed record CleanReport(bool DryRun, long BytesFreed, int FileCount, int SkippedCount, double DurationSeconds,
    IReadOnlyList<CleanTargetResult> Targets, IReadOnlyList<string> SampleFiles);

// ---- shutdown ----
public sealed record ShutdownNowRequest(int DelayMinutes = 5);
public enum ShutdownOutcome { ShutDown, Extended, Cancelled, Forced, Skipped }
public sealed record ShutdownLog(ShutdownOutcome Outcome, DateTimeOffset At, int ExtensionsUsed, string? By);

// ---- errors ----
public sealed record ErrorEventSummary(string Log, string Source, int EventId, string Level, int Count, DateTimeOffset LastSeen, string Message);
public sealed record FailedService(string Name, string DisplayName, string Status);
public sealed record ErrorsReport(DateTimeOffset Since, IReadOnlyList<ErrorEventSummary> Events, IReadOnlyList<FailedService> FailedServices);

// ---- update ----
public sealed record PendingUpdate(string Title, string? Kb, bool IsSecurity, double SizeMb);
public sealed record UpdateReport(bool SourceReachable, string? SourceError, IReadOnlyList<PendingUpdate> Pending, DateTimeOffset? LastSuccessfulInstall,
    bool RebootRequired, int? InstalledCount = null, string? InstallResult = null);

// ---- deploy ----
public sealed record DeployPackageRequest(string PackageId, string FileName, string Sha256, string SilentArgs, int TimeoutSeconds);
public enum ScriptShell { Cmd, PowerShell }
public sealed record RunScriptRequest(ScriptShell Shell, string Script, int TimeoutSeconds);
public sealed record DeployResult(int? ExitCode, bool TimedOut, string StdOut, string StdErr, double DurationSeconds);

// ---- health ----
public sealed record VolumeHealth(string Name, string? Label, long TotalBytes, long FreeBytes, double FreePercent);
public sealed record DiskHealth(string Model, string HealthStatus, long? PowerOnHours, int? Temperature, long? ReadErrors, int? Wear);
public sealed record HealthReport(IReadOnlyList<VolumeHealth> Volumes, IReadOnlyList<DiskHealth> Disks, long TotalRamBytes, long UsedRamBytes,
    double UptimeHours, DateTimeOffset BootTime, double? LastBootDurationSeconds, IReadOnlyList<string> Warnings);

// ---- inspect ----
public sealed record InspectRequest(bool ResetWallpaper = true, bool LedTest = true, bool CheckUpdates = true, bool CheckPrograms = true);
public sealed record InspectItem(string Name, bool? Passed, string Detail);
public sealed record InstalledProgram(string Name, string? Publisher, string? Version, bool Allowed, bool CanUninstall, string Key);
public sealed record InspectReport(IReadOnlyList<InspectItem> Items, string? WindowsEdition, string? WindowsVersion, IReadOnlyList<InstalledProgram> UnwantedPrograms);
public sealed record UninstallRequest(IReadOnlyList<string> Keys);
public sealed record UninstallResult(string Key, string Name, bool Success, string Detail);

// ---- netdiag ----
public enum NetCheckStatus { Ok, Warning, Failed, Skipped, Repaired }
public sealed record NetCheck(string Name, NetCheckStatus Status, string Message);
public sealed record NetDiagReport(IReadOnlyList<NetCheck> Checks, IReadOnlyList<string> RepairsTried, bool Healthy, string Summary);
