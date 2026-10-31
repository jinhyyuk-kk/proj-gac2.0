using System.ServiceProcess;
using Gac.Shared;
using Gac.Shared.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gac.Agent.Runtime;

/// <summary>
/// Windows service lifetime that also reacts to user logoff and system shutdown, which trigger Auto Clean (CLAUDE.md 4.1).
/// </summary>
public sealed class GacServiceLifetime : WindowsServiceLifetime
{
    private static readonly TimeSpan ShutdownCleanBudget = TimeSpan.FromSeconds(12);
    private readonly AgentCore _core;
    private readonly ILogger _log;

    public GacServiceLifetime(IHostEnvironment env, IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory,
        IOptions<HostOptions> hostOptions, IOptions<WindowsServiceLifetimeOptions> serviceOptions, AgentCore core)
        : base(env, lifetime, loggerFactory, hostOptions, serviceOptions)
    {
        _core = core;
        _log = loggerFactory.CreateLogger<GacServiceLifetime>();
        CanHandleSessionChangeEvent = true;
        CanShutdown = true;
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        if (changeDescription.Reason == SessionChangeReason.SessionLogoff && _core.Policy.Clean is { Enabled: true, RunOnShutdown: true })
        {
            _log.LogInformation("User logoff in session {Session}: running clean", changeDescription.SessionId);
            _ = Task.Run(() => _core.RunModuleAsync(ModuleIds.Clean, ModuleActions.CleanRun, "{}", ReportTrigger.Event, null, "logoff", CancellationToken.None));
        }
        base.OnSessionChange(changeDescription);
    }

    protected override void OnShutdown()
    {
        if (_core.Policy.Clean is { Enabled: true, RunOnShutdown: true })
        {
            _log.LogInformation("System shutdown: running time-boxed clean");
            using var cts = new CancellationTokenSource(ShutdownCleanBudget);
            try
            {
                _core.RunModuleAsync(ModuleIds.Clean, ModuleActions.CleanRun, "{}", ReportTrigger.Event, null, "shutdown", cts.Token)
                    .Wait(ShutdownCleanBudget + TimeSpan.FromSeconds(1));
            }
            catch (AggregateException) { }
        }
        base.OnShutdown();
    }
}
