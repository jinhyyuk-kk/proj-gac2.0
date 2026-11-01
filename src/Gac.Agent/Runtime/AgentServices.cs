using System.Net.NetworkInformation;
using System.Net.Sockets;
using Gac.Agent.Net;
using Gac.Shared;
using Gac.Shared.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gac.Agent.Runtime;

/// <summary>Polls the GAC Server (outbound only), executes verified commands, uploads queued results with backoff.</summary>
public sealed class PollWorker(AgentCore core, TimeProvider time, ILogger<PollWorker> log) : BackgroundService
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);
    private readonly List<Task> _running = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = MinBackoff;
        var pollSeconds = 30;
        var unknownLogged = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            var client = core.Client;
            if (client == null)
            {
                core.ReloadIdentity();
                delay = TimeSpan.FromMinutes(1);
            }
            else
            {
                try
                {
                    await PollOnceAsync(client, stoppingToken, s => pollSeconds = s);
                    backoff = MinBackoff;
                    unknownLogged = false;
                    delay = Jitter(TimeSpan.FromSeconds(Math.Clamp(pollSeconds, 10, 600)));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (AgentUnknownException e)
                {
                    if (!unknownLogged) log.LogError(e.Message);
                    unknownLogged = true;
                    delay = MaxBackoff;
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or System.Security.Authentication.AuthenticationException)
                {
                    log.LogInformation("Server unreachable ({Error}); retry in {Delay}", e.Message, backoff);
                    delay = Jitter(backoff);
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                }
                catch (Exception e)
                {
                    log.LogError(e, "Poll failed");
                    delay = Jitter(backoff);
                }
            }
            lock (_running) _running.RemoveAll(t => t.IsCompleted);
            try { await Task.Delay(delay, time, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    internal async Task PollOnceAsync(ServerClient client, CancellationToken ct, Action<int> setPoll)
    {
        var state = core.Store.LoadState();
        var resp = await client.PollAsync(new PollRequest(core.Version, Sessions.ActiveUser(), LocalIp(), Environment.OSVersion.VersionString, core.Queue.Count, state.PolicyVersion, core.DeviceReport()), ct);
        core.Store.UpdateState(s => s.LastServerContact = time.GetUtcNow());
        setPoll(resp.PollSeconds);
        if (resp.Revoked)
        {
            core.MarkRevoked(resp.RevokedReason ?? "이 PC는 관리 대상에서 제외되었습니다.");
            core.SetPeriods([]);
            core.SetDeviceApproved(false);
            return; // stop processing commands/periods
        }
        core.SetDeviceApproved(true); // server accepted this device (lets the tray trust a UPS battery on a desktop)
        core.SetPeriods(resp.Periods);
        core.SetCalendar(resp.Calendar); // server time + exemption days (CHANGE-002 §2.3)
        core.ReconcileLocks(); // drive PC locks for active lock_pc periods (CHANGE-001 2.4), suspended on exemption days
        if (resp.Policy != null) core.ApplyPolicy(resp.Policy, resp.PolicyVersion);

        var rejections = new List<CommandRejection>();
        foreach (var cmd in resp.Commands)
        {
            var reason = core.Verify(cmd);
            if (reason != null) { rejections.Add(new CommandRejection(cmd.Id, reason)); continue; }
            var task = Task.Run(() => core.RunModuleAsync(cmd.Module, cmd.Action, cmd.PayloadJson, ReportTrigger.Command, cmd.Id, "console", ct), ct)
                .ContinueWith(_ => UploadQueueAsync(client, CancellationToken.None), TaskScheduler.Default).Unwrap();
            lock (_running) _running.Add(task);
        }
        if (rejections.Count > 0) await client.UploadRejectionsAsync(rejections, ct);
        await UploadQueueAsync(client, ct);
    }

    /// <summary>Waits until all commands started by previous polls (and their uploads) have finished. Used by tests.</summary>
    internal async Task WhenIdleAsync()
    {
        Task[] tasks;
        lock (_running) tasks = _running.ToArray();
        await Task.WhenAll(tasks);
    }

    private readonly SemaphoreSlim _uploadGate = new(1, 1);

    private async Task UploadQueueAsync(ServerClient client, CancellationToken ct)
    {
        if (!await _uploadGate.WaitAsync(0, ct)) return;
        try
        {
            while (true)
            {
                var batch = core.Queue.Peek(50);
                if (batch.Count == 0) return;
                await client.UploadReportsAsync(batch.Select(b => b.Report).ToList(), ct);
                core.Queue.Remove(batch.Select(b => b.Path));
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) { log.LogInformation("Upload deferred: {Error}", e.Message); }
        finally { _uploadGate.Release(); }
    }

    private static TimeSpan Jitter(TimeSpan t) => t * (0.8 + Random.Shared.NextDouble() * 0.4);

    private static string? LocalIp() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.GetIPProperties().GatewayAddresses.Count > 0)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
}

/// <summary>Local schedules that run even when the server is off: shutdown, clean, health, errors.</summary>
public sealed class SchedulerWorker(AgentCore core, TimeProvider time, ILogger<SchedulerWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(started, stoppingToken); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Scheduler tick failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), time, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(DateTimeOffset started, CancellationToken ct)
    {
        var policy = core.Policy;
        await core.Shutdown.TickAsync(policy.Shutdown, ct);
        // Exemption days start/end at midnight (Asia/Seoul) even while the server is unreachable (CHANGE-002 §2.2/2.3).
        if (!core.Revoked) core.ReconcileLocks();

        var now = time.GetUtcNow();
        var state = core.Store.LoadState();
        var warmedUp = now - started > TimeSpan.FromMinutes(2);

        if (warmedUp && (state.LastHealth == null || now - state.LastHealth > TimeSpan.FromHours(Math.Max(1, policy.Health.IntervalHours))))
        {
            core.Store.UpdateState(s => s.LastHealth = now);
            _ = core.RunModuleAsync(ModuleIds.Health, ModuleActions.HealthReport, "{}", ReportTrigger.Schedule, null, null, ct);
        }
        if (warmedUp && (state.LastErrors == null || now - state.LastErrors > TimeSpan.FromHours(Math.Max(1, policy.Errors.IntervalHours))))
        {
            core.Store.UpdateState(s => s.LastErrors = now);
            _ = core.RunModuleAsync(ModuleIds.Errors, ModuleActions.ErrorsCollect, "{}", ReportTrigger.Schedule, null, null, ct);
        }

        var local = time.GetLocalNow();
        var today = DateOnly.FromDateTime(local.DateTime);
        if (policy.Clean.Enabled && TimeOnly.TryParse(policy.Clean.ScheduleTime ?? "", out var cleanAt) && state.LastScheduledClean != today)
        {
            var at = local.Date + cleanAt.ToTimeSpan();
            if (local.DateTime >= at && local.DateTime < at.AddHours(2))
            {
                core.Store.UpdateState(s => s.LastScheduledClean = today);
                _ = core.RunModuleAsync(ModuleIds.Clean, ModuleActions.CleanRun, "{}", ReportTrigger.Schedule, null, null, ct);
            }
        }
    }
}

public sealed class TrayPipeWorker(TrayPipeServer pipe) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => pipe.RunAsync(stoppingToken);
}
