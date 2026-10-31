using Gac.Shared;
using Gac.Shared.Contracts;
using Gac.Shared.Modules;
using Gac.Shared.Pipe;

namespace Gac.Modules.Shutdown;

public interface IShutdownExecutor
{
    Task ShutdownAsync(bool force, CancellationToken ct);
}

/// <summary>Uses shutdown.exe. "/t 0" does not imply "/f", so the first attempt is a normal shutdown.</summary>
public sealed class WindowsShutdownExecutor(IProcessRunner runner) : IShutdownExecutor
{
    public Task ShutdownAsync(bool force, CancellationToken ct) =>
        runner.RunAsync(Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
            force ? "/s /f /t 0 /d p:0:0" : "/s /t 0 /d p:0:0", TimeSpan.FromSeconds(30), ct);
}

public enum ShutdownPhase { Idle, Warning, ShuttingDown }

/// <summary>
/// Auto-shutdown state machine (CLAUDE.md 4.2). The agent calls <see cref="TickAsync"/> about every 15 seconds.
/// Warning popups and "30분 연장"/"지금 종료" answers travel over the tray pipe.
/// </summary>
public sealed class ShutdownCoordinator(ITrayBridge tray, IShutdownExecutor executor, TimeProvider time, Action<ShutdownLog> log)
{
    private readonly Lock _lock = new();
    private DateOnly? _handledDate;

    public ShutdownPhase Phase { get; private set; } = ShutdownPhase.Idle;
    public DateTimeOffset? Deadline { get; private set; }
    public int ExtensionsUsed { get; private set; }
    private DateTimeOffset? _shutdownStartedAt;
    private bool _forced;
    private bool _warned;
    private ShutdownPolicy _policy = new();

    /// <summary>The next scheduled shutdown time today, if today is a scheduled day.</summary>
    public static DateTimeOffset? ScheduledToday(ShutdownPolicy p, DateTimeOffset nowLocal)
    {
        if (!p.Enabled || !p.Days.Contains(nowLocal.DayOfWeek) || !TimeOnly.TryParse(p.Time, out var t)) return null;
        var d = nowLocal.Date + t.ToTimeSpan();
        return new DateTimeOffset(d, nowLocal.Offset);
    }

    public async Task TickAsync(ShutdownPolicy policy, CancellationToken ct)
    {
        var now = time.GetLocalNow();
        bool doShutdown = false, doForce = false, warn = false;
        lock (_lock)
        {
            _policy = policy;
            switch (Phase)
            {
                case ShutdownPhase.Idle:
                {
                    var today = DateOnly.FromDateTime(now.DateTime);
                    if (_handledDate == today) break;
                    var at = ScheduledToday(policy, now);
                    if (at == null) break;
                    // Do not shut down a PC that was turned on long after the scheduled time (e.g. evening class).
                    if (now > at.Value.AddMinutes(30)) { _handledDate = today; break; }
                    if (now >= at.Value.AddMinutes(-policy.WarningMinutes))
                    {
                        _handledDate = today;
                        Phase = ShutdownPhase.Warning;
                        Deadline = at.Value < now ? now.AddMinutes(policy.WarningMinutes) : at.Value;
                        ExtensionsUsed = 0;
                        _warned = false;
                    }
                    break;
                }
                case ShutdownPhase.ShuttingDown:
                    if (!_forced && _shutdownStartedAt is { } s && now >= s.AddMinutes(policy.ForceGraceMinutes))
                    { _forced = true; doForce = true; }
                    break;
            }

            if (Phase == ShutdownPhase.Warning && Deadline is { } dl)
            {
                if (!tray.AnyUserSession)
                {
                    // Nobody logged in: no popup, shut down at the deadline.
                    if (now >= dl) doShutdown = BeginShutdown(now);
                }
                else if (now >= dl) doShutdown = BeginShutdown(now);
                else if (!_warned && now >= dl.AddMinutes(-policy.WarningMinutes)) { _warned = true; warn = true; }
            }
        }

        if (warn) tray.Broadcast(ServiceEvent.ShutdownWarning, new ShutdownWarningBody(Deadline!.Value, Math.Max(0, policy.MaxExtensions - ExtensionsUsed), policy.ExtensionMinutes));
        if (doShutdown)
        {
            log(new ShutdownLog(ShutdownOutcome.ShutDown, now, ExtensionsUsed, null));
            await executor.ShutdownAsync(false, ct);
        }
        if (doForce)
        {
            log(new ShutdownLog(ShutdownOutcome.Forced, now, ExtensionsUsed, null));
            await executor.ShutdownAsync(true, ct);
        }
    }

    private bool BeginShutdown(DateTimeOffset now)
    {
        Phase = ShutdownPhase.ShuttingDown;
        _shutdownStartedAt = now;
        _forced = false;
        return true;
    }

    /// <summary>Called when a user clicks "30분 연장". Returns the Korean message for the tray.</summary>
    public PipeResponse Extend(string? user)
    {
        lock (_lock)
        {
            if (Phase != ShutdownPhase.Warning || Deadline == null) return new PipeResponse(false, "예약된 종료가 없습니다.");
            if (ExtensionsUsed >= _policy.MaxExtensions) return new PipeResponse(false, "더 이상 연장할 수 없습니다.");
            ExtensionsUsed++;
            Deadline = Deadline.Value.AddMinutes(_policy.ExtensionMinutes);
            _warned = false;
        }
        tray.Broadcast(ServiceEvent.ShutdownCancelled, null);
        log(new ShutdownLog(ShutdownOutcome.Extended, time.GetLocalNow(), ExtensionsUsed, user));
        return new PipeResponse(true, $"종료가 {_policy.ExtensionMinutes}분 연장되었습니다. ({Deadline:HH:mm} 종료 예정)");
    }

    /// <summary>"지금 종료" from the popup.</summary>
    public async Task<PipeResponse> ShutdownNowAsync(string? user, CancellationToken ct)
    {
        lock (_lock)
        {
            if (Phase == ShutdownPhase.ShuttingDown) return new PipeResponse(true, "종료 중입니다.");
            BeginShutdown(time.GetLocalNow());
        }
        log(new ShutdownLog(ShutdownOutcome.ShutDown, time.GetLocalNow(), ExtensionsUsed, user));
        await executor.ShutdownAsync(false, ct);
        return new PipeResponse(true, "PC를 종료합니다.");
    }

    /// <summary>Console "shutdown now" command: warn for <paramref name="delayMinutes"/> then shut down.</summary>
    public void Request(int delayMinutes, string? by)
    {
        var now = time.GetLocalNow();
        lock (_lock)
        {
            if (Phase == ShutdownPhase.ShuttingDown) return;
            Phase = ShutdownPhase.Warning;
            Deadline = now.AddMinutes(Math.Clamp(delayMinutes, 0, 120));
            ExtensionsUsed = 0;
            _warned = false;
        }
    }

    public bool Cancel(string? by)
    {
        lock (_lock)
        {
            if (Phase != ShutdownPhase.Warning) return false;
            Phase = ShutdownPhase.Idle;
            Deadline = null;
        }
        tray.Broadcast(ServiceEvent.ShutdownCancelled, null);
        log(new ShutdownLog(ShutdownOutcome.Cancelled, time.GetLocalNow(), ExtensionsUsed, by));
        return true;
    }
}

/// <summary>Console-facing actions for the shutdown module. The schedule itself is driven by the coordinator.</summary>
public sealed class ShutdownModule(ShutdownCoordinator coordinator) : IAgentModule
{
    public string Id => ModuleIds.Shutdown;

    public async Task<ModuleOutcome> ExecuteAsync(ModuleRequest request, CancellationToken ct)
    {
        switch (request.Action)
        {
            case ModuleActions.ShutdownNow:
                var req = GacJson.Deserialize<ShutdownNowRequest>(string.IsNullOrWhiteSpace(request.PayloadJson) ? "{}" : request.PayloadJson) ?? new ShutdownNowRequest();
                coordinator.Request(req.DelayMinutes, request.TriggeredBy);
                await coordinator.TickAsync(request.Policy.Shutdown, ct);
                return ModuleOutcome.Ok(req.DelayMinutes <= 0 ? "종료 명령을 실행했습니다." : $"{req.DelayMinutes}분 뒤 종료하도록 예약했습니다 (사용자에게 경고 표시).");
            case ModuleActions.ShutdownCancel:
                return coordinator.Cancel(request.TriggeredBy) ? ModuleOutcome.Ok("예약된 종료를 취소했습니다.") : ModuleOutcome.Ok("취소할 예약 종료가 없습니다.");
            default:
                return ModuleOutcome.Fail("알 수 없는 명령입니다.");
        }
    }
}
