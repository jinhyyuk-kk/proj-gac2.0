namespace Gac.Shared.Contracts;

public sealed record EnrollHelloResponse(string Proof, string ServerName, string Version);

/// <param name="PreviousAgentId">Set when the agent re-enrolls and still has its old identity.</param>
/// <param name="PreviousProof">HMAC(old agent key, <see cref="Security.EnrollmentProof.ReenrollMessage"/>) proving it owns that identity.</param>
/// <param name="Device">Raw hardware probes, install markers and integrity hashes. The server decides desktop vs. laptop.</param>
public sealed record EnrollRequest(string Code, string MachineName, string? OsVersion, string? MacAddress, string AgentVersion,
    string? PreviousAgentId = null, string? PreviousProof = null, DeviceReport? Device = null);

public enum EnrollStatus { Enrolled, Rejected, PendingReview }

public sealed record EnrollResponse(string AgentId, string AgentKeyBase64, string PresetPublicKeyPem, int PollSeconds,
    EnrollStatus Status = EnrollStatus.Enrolled, string? Message = null);

/// <summary>Command as delivered to an agent. Signed with the per-agent HMAC key (see <see cref="Security.CommandSigner"/>).</summary>
public sealed record AgentCommand(
    Guid Id,
    string AgentId,
    string Module,
    string Action,
    string PayloadJson,
    DateTimeOffset IssuedAt,
    string Signature = "");

public sealed record PollRequest(
    string AgentVersion,
    string? LoggedOnUser,
    string? IpAddress,
    string? OsVersion,
    int QueuedReports,
    string? PolicyVersion = null,
    DeviceReport? Device = null);

/// <summary>Tells a polling agent the server has revoked it (laptop found later, tampering, Owner revoke). The agent stops.</summary>
public sealed record PollResponse(
    IReadOnlyList<AgentCommand> Commands,
    AgentPolicy? Policy,
    string? PolicyVersion,
    int PollSeconds,
    bool Revoked = false,
    string? RevokedReason = null,
    IReadOnlyList<PeriodPreset>? Periods = null,
    AgentCalendar? Calendar = null);

public enum ReportTrigger { Command, Schedule, Tray, Event }

/// <summary>A module run result uploaded by an agent. Data is module-specific JSON.</summary>
public sealed record ModuleReport(
    Guid Id,
    Guid? CommandId,
    string Module,
    string Action,
    bool Success,
    string Summary,
    string? DataJson,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    ReportTrigger Trigger);

public sealed record ReportUpload(IReadOnlyList<ModuleReport> Reports);

/// <summary>Sent by the agent when it refuses a command (bad signature, expired, replayed).</summary>
public sealed record CommandRejection(Guid CommandId, string Reason);

public sealed record RejectionUpload(IReadOnlyList<CommandRejection> Rejections);
