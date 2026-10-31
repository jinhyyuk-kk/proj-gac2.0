using Gac.Shared.Contracts;
using Gac.Shared.Pipe;

namespace Gac.Shared.Modules;

/// <summary>
/// Plugin interface implemented by every agent-side module. The agent host routes verified commands and
/// scheduled triggers to <see cref="ExecuteAsync"/>. Modules must not reference each other.
/// </summary>
public interface IAgentModule
{
    string Id { get; }
    Task<ModuleOutcome> ExecuteAsync(ModuleRequest request, CancellationToken ct);
}

public sealed record ModuleRequest(string Action, string PayloadJson, ReportTrigger Trigger, AgentPolicy Policy, string? TriggeredBy = null);

public sealed record ModuleOutcome(bool Success, string Summary, object? Data = null)
{
    public static ModuleOutcome Ok(string summary, object? data = null) => new(true, summary, data);
    public static ModuleOutcome Fail(string summary, object? data = null) => new(false, summary, data);
}

/// <summary>Lets service-side modules ask the tray (user session) to do user-context work.</summary>
public interface ITrayBridge
{
    bool AnyUserSession { get; }
    /// <summary>Sends an event to every connected tray and returns the first response, or null on timeout / no tray.</summary>
    Task<PipeResponse?> RequestAsync(ServiceEvent type, object? body, TimeSpan timeout, CancellationToken ct);
    void Broadcast(ServiceEvent type, object? body);
}

/// <summary>Downloads a deploy package from the GAC Server over the LAN and verifies its SHA-256.</summary>
public interface IPackageSource
{
    Task<string> DownloadVerifiedAsync(string packageId, string fileName, string sha256, CancellationToken ct);
}

/// <summary>Runs an external process. Abstracted so modules can be tested without launching anything.</summary>
public interface IProcessRunner
{
    /// <param name="arguments">Raw command-line argument string (installers need exact quoting).</param>
    Task<ProcessRunResult> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken ct);
}

public sealed record ProcessRunResult(int? ExitCode, bool TimedOut, string StdOut, string StdErr, TimeSpan Duration);
