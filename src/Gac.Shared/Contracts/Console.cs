using Gac.Shared.Auth;

namespace Gac.Shared.Contracts;

public sealed record SetupStatus(bool NeedsOwner, string ServerName, string Version);
public sealed record CreateOwnerRequest(string UserName, string DisplayName, string Password, string EnrollCode);
public sealed record LoginRequest(string UserName, string Password, string? MachineFingerprint = null);
public sealed record LoginResponse(string Token, AccountInfo Account, DateTimeOffset ExpiresAt);

public sealed record AccountInfo(int Id, string UserName, string DisplayName, Role Role, bool Disabled, IReadOnlyList<int> GroupIds,
    DateTimeOffset? LastLogin, DateTimeOffset? LockedUntil);
public sealed record SaveAccountRequest(string UserName, string DisplayName, Role Role, bool Disabled, IReadOnlyList<int> GroupIds, string? Password);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record ResetPasswordRequest(string NewPassword);

public sealed record PcGroup(int Id, string Name, int PcCount);
public sealed record SaveGroupRequest(string Name);

/// <param name="Name">Alias if set, otherwise the Windows computer name (what to show large).</param>
/// <param name="WindowsName">The real Windows computer name (shown small; never changed by GAC).</param>
public sealed record PcSummary(
    string Id, string Name, int? GroupId, string? GroupName, bool Online, DateTimeOffset? LastSeen,
    string? IpAddress, string? OsVersion, string? LoggedOnUser, string? AgentVersion,
    double? MinFreePercent, int? PendingUpdates, int? RecentErrors, bool RebootRequired,
    string WindowsName = "", bool WorkPc = false, bool Revoked = false, string? Alias = null);

public sealed record MovePcRequest(IReadOnlyList<string> PcIds, int? GroupId);
public sealed record SetAliasRequest(string? Alias);

public sealed record SendCommandRequest(string Module, string Action, string PayloadJson, IReadOnlyList<string> PcIds);
public sealed record SendCommandResponse(Guid BatchId, int Count);

public enum CommandStatus { Pending, Delivered, Succeeded, Failed, Expired, Rejected }
public sealed record CommandBatchSummary(Guid BatchId, string Module, string Action, string CreatedBy, DateTimeOffset CreatedAt, int Total, int Done, int Failed);
public sealed record CommandRow(Guid Id, Guid BatchId, string PcId, string PcName, string Module, string Action, CommandStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt, string? Summary, string? DataJson);

public sealed record ReportRow(Guid Id, string PcId, string PcName, string Module, string Action, bool Success, string Summary, string? DataJson,
    DateTimeOffset FinishedAt, ReportTrigger Trigger);

public sealed record AuditEntry(long Id, DateTimeOffset At, string Actor, string Action, string? Target, string? Detail);

public sealed record GroupPolicy(int GroupId, CleanPolicy Clean, ShutdownPolicy Shutdown, HealthPolicy Health, ErrorsPolicy Errors);

public sealed record ServerSettings(string? NetDiagExternalTarget, string? NetDiagDnsTestName, string WallpaperPath, string[] ProgramAllowlist, int PollSeconds);

public sealed record EnrollmentInfo(string Code, string ServerAddress, int Port, string Thumbprint);

public sealed record SharedPresetInfo(string PresetsJson, DateTimeOffset? UpdatedAt, string? UpdatedBy);
public sealed record SaveSharedPresetsRequest(string PresetsJson);

public sealed record DeployPackage(string Id, string Name, string FileName, string Sha256, long SizeBytes, string SilentArgs, DateTimeOffset UploadedAt, string UploadedBy);
public sealed record CreatePackageRequest(string Name, string FileName, string SilentArgs, string ContentBase64);
