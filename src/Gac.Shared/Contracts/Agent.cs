namespace Gac.Shared.Contracts;

public sealed record EnrollHelloResponse(string Proof, string ServerName, string Version);

public sealed record EnrollRequest(string Code, string MachineName, string? OsVersion, string? MacAddress, string AgentVersion);

public sealed record EnrollResponse(string AgentId, string AgentKeyBase64, string PresetPublicKeyPem, int PollSeconds);

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
    string? PolicyVersion = null);

public sealed record PollResponse(
    IReadOnlyList<AgentCommand> Commands,
    AgentPolicy? Policy,
    string? PolicyVersion,
    int PollSeconds);

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
