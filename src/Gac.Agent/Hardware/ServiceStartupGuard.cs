using Gac.Agent.Config;
using Gac.Shared;
using Gac.Shared.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gac.Agent.Hardware;

/// <summary>
/// CHANGE-001 1.1, check site #2 — runs at Agent Service startup using probes B (WMI enclosure) and C (raw SMBIOS),
/// with its own code (no shared IsLaptop helper). If either reports a portable chassis, or both fail, it fails closed:
/// the service writes a "blocked" marker and stops. The server still enforces the real decision (1.2).
/// </summary>
public sealed class ServiceStartupGuard(AgentStore store, IHostApplicationLifetime lifetime, ILogger<ServiceStartupGuard> log) : IHostedService
{
    // Portable/mobile chassis values (SMBIOS System Enclosure type 3). Deliberately duplicated here, not shared.
    private static readonly int[] Portable = [8, 9, 10, 11, 14, 30, 31, 32];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        bool blocked;
        try
        {
            var b = ProbeB.ChassisTypes();
            var c = ProbeC.ChassisTypes();
            // Fail closed: if both probes return nothing, treat as a laptop.
            if (b is null && c is null) blocked = true;
            else blocked = (b ?? []).Any(Portable.Contains) || (c ?? []).Any(Portable.Contains);
        }
        catch { blocked = true; }

        if (blocked)
        {
            log.LogError(DeviceGuardMessage);
            try
            {
                Directory.CreateDirectory(store.StateDir);
                File.WriteAllText(Path.Combine(store.StateDir, "blocked.txt"), DeviceGuardMessage);
            }
            catch { }
            lifetime.StopApplication(); // the agent must not run as a client on a laptop
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public const string DeviceGuardMessage = "이 프로그램은 데스크톱 PC에만 설치할 수 있습니다. 노트북에는 설치할 수 없습니다.";
}
