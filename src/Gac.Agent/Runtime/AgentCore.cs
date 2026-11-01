using Gac.Agent.Config;
using Gac.Agent.Net;
using Gac.Modules.NetDiag;
using Gac.Modules.Shutdown;
using Gac.Shared;
using Gac.Shared.Contracts;
using Gac.Shared.Modules;
using Gac.Shared.Pipe;
using Gac.Shared.Security;
using Microsoft.Extensions.Logging;

namespace Gac.Agent.Runtime;

/// <summary>Central agent state: identity, policy, module registry, command verification, tray requests.</summary>
public sealed class AgentCore : IPackageSource
{
    private readonly Dictionary<string, IAgentModule> _modules;
    private readonly ILogger<AgentCore> _log;
    private readonly TimeProvider _time;
    private readonly NetRepairService _netRepair;
    private readonly Lock _lock = new();
    private ServerClient? _client;
    private AgentIdentity? _identity;

    public AgentStore Store { get; }
    public ReportQueue Queue { get; }
    public TrayPipeServer Tray { get; }
    public ShutdownCoordinator Shutdown { get; }
    public ReplayGuard Replay { get; }
    public AgentPolicy Policy { get; private set; }
    public string Version { get; } = typeof(AgentCore).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";

    /// <summary>Set when the server revokes this agent (laptop found later, tampering, or Owner revoke). Modules stop running.</summary>
    public bool Revoked { get; private set; }
    public string? RevokedReason { get; private set; }
    public void MarkRevoked(string reason) { Revoked = true; RevokedReason = reason; }

    /// <summary>Writes/removes the marker the tray reads to trust a UPS battery on a server-accepted desktop.</summary>
    public void SetDeviceApproved(bool approved)
    {
        try
        {
            Directory.CreateDirectory(Store.StateDir);
            var p = Path.Combine(Store.StateDir, "device-approved.txt");
            if (approved) { if (!File.Exists(p)) File.WriteAllText(p, DateTimeOffset.UtcNow.ToString("o")); }
            else if (File.Exists(p)) File.Delete(p);
        }
        catch (IOException) { }
    }

    /// <summary>Signed period presets currently active for this PC (CHANGE-001 2.3/2.4), refreshed on each poll.</summary>
    public IReadOnlyList<Gac.Shared.Contracts.PeriodPreset> Periods { get; private set; } = [];
    public void SetPeriods(IReadOnlyList<Gac.Shared.Contracts.PeriodPreset>? periods) => Periods = periods ?? [];

    private Gac.Shared.Contracts.DeviceReport? _deviceCache;
    private DateTimeOffset _deviceCachedAt;
    /// <summary>Test seam: when set, the agent sends this report instead of probing the real machine.</summary>
    public Func<Gac.Shared.Contracts.DeviceReport>? DeviceReportOverride { get; set; }
    /// <summary>A cached full device report (probes/markers/integrity), refreshed hourly, sent with enroll and every heartbeat.</summary>
    public Gac.Shared.Contracts.DeviceReport DeviceReport()
    {
        if (DeviceReportOverride != null) return DeviceReportOverride();
        if (_deviceCache == null || _time.GetUtcNow() - _deviceCachedAt > TimeSpan.FromHours(1))
        {
            _deviceCache = Hardware.DeviceReportCollector.Collect();
            _deviceCachedAt = _time.GetUtcNow();
        }
        return _deviceCache;
    }

    public AgentCore(AgentStore store, TrayPipeServer tray, IProcessRunner runner, TimeProvider time, ILogger<AgentCore> log,
        Func<AgentCore, IEnumerable<IAgentModule>> moduleFactory, IShutdownExecutor? shutdownExecutor = null)
    {
        Store = store;
        Tray = tray;
        _time = time;
        _log = log;
        Queue = new ReportQueue(store.QueueDir);
        Replay = new ReplayGuard(2000, store.LoadReplay());
        Policy = store.LoadPolicy();
        _identity = store.LoadIdentity();
        _netRepair = new NetRepairService(runner);
        Shutdown = new ShutdownCoordinator(tray, shutdownExecutor ?? new WindowsShutdownExecutor(runner), time, OnShutdownLog);
        _modules = moduleFactory(this).ToDictionary(m => m.Id);
        tray.Handler = HandleTrayAsync;
    }

    public AgentIdentity? Identity { get { lock (_lock) return _identity; } }

    public ServerClient? Client
    {
        get
        {
            lock (_lock)
            {
                if (_identity == null) return null;
                return _client ??= new ServerClient(_identity, _time);
            }
        }
    }

    public void ReloadIdentity()
    {
        lock (_lock)
        {
            _identity = Store.LoadIdentity();
            _client?.Dispose();
            _client = null;
        }
    }

    public void ApplyPolicy(AgentPolicy policy, string? version)
    {
        Policy = policy;
        Store.SavePolicy(policy);
        Store.UpdateState(s => s.PolicyVersion = version);
        _log.LogInformation("Policy updated to version {Version}", version);
    }

    public IReadOnlyCollection<string> ModuleIdsLoaded => _modules.Keys;

    /// <summary>Verifies a server command (signature, expiry, replay). Returns null when accepted, otherwise the reason.</summary>
    public string? Verify(AgentCommand cmd)
    {
        var id = Identity;
        if (id == null) return "not enrolled";
        var r = CommandSigner.Verify(cmd, id.Key, id.AgentId, _time.GetUtcNow(), Replay);
        if (r != CommandVerifyResult.Ok)
        {
            _log.LogWarning("Rejected command {Id} {Module}.{Action}: {Reason}", cmd.Id, cmd.Module, cmd.Action, r);
            return r.ToString();
        }
        Store.SaveReplay(Replay.Snapshot());
        return null;
    }

    /// <summary>Runs a module and queues the result for upload. Never throws.</summary>
    public async Task<ModuleReport> RunModuleAsync(string module, string action, string payload, ReportTrigger trigger, Guid? commandId, string? by, CancellationToken ct)
    {
        var started = _time.GetUtcNow();
        ModuleOutcome outcome;
        if (!_modules.TryGetValue(module, out var m)) outcome = ModuleOutcome.Fail("이 PC에서 지원하지 않는 모듈입니다.");
        else
        {
            _log.LogInformation("Running {Module}.{Action} ({Trigger}) by {By}", module, action, trigger, by ?? "-");
            try { outcome = await m.ExecuteAsync(new ModuleRequest(action, payload, trigger, Policy, by), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { outcome = ModuleOutcome.Fail("에이전트가 중지되어 작업이 취소되었습니다."); }
            catch (Exception e)
            {
                _log.LogError(e, "Module {Module}.{Action} failed", module, action);
                outcome = ModuleOutcome.Fail("작업 중 오류가 발생했습니다: " + e.Message);
            }
        }
        var report = new ModuleReport(Guid.NewGuid(), commandId, module, action, outcome.Success, outcome.Summary,
            outcome.Data == null ? null : GacJson.Serialize(outcome.Data), started, _time.GetUtcNow(), trigger);
        Queue.Enqueue(report);
        _log.LogInformation("{Module}.{Action} finished: success={Success} {Summary}", module, action, outcome.Success, outcome.Summary);
        return report;
    }

    private void OnShutdownLog(ShutdownLog entry)
    {
        var text = entry.Outcome switch
        {
            ShutdownOutcome.ShutDown => "예약 종료 실행",
            ShutdownOutcome.Forced => "강제 종료 실행 (정상 종료가 끝나지 않음)",
            ShutdownOutcome.Extended => $"사용자가 종료를 연장함 ({entry.ExtensionsUsed}회째)",
            ShutdownOutcome.Cancelled => "예약 종료 취소",
            _ => entry.Outcome.ToString(),
        };
        if (entry.By != null) text += $" · {entry.By}";
        var now = _time.GetUtcNow();
        Queue.Enqueue(new ModuleReport(Guid.NewGuid(), null, ModuleIds.Shutdown, "schedule", true, text, GacJson.Serialize(entry), now, now, ReportTrigger.Event));
        _log.LogWarning("Shutdown: {Outcome} by {By}", entry.Outcome, entry.By ?? "schedule");
    }

    private async Task<PipeResponse> HandleTrayAsync(TrayRequest req, PipeMessage msg, string user, CancellationToken ct)
    {
        switch (req)
        {
            case TrayRequest.Hello:
                return new PipeResponse(true, "ok");
            case TrayRequest.GetStatus:
            {
                var id = Identity;
                var status = new AgentStatus(id != null, id?.ServerHost, id?.ServerPort ?? 0, Policy.NetDiag, id?.PresetPublicKeyPem, Policy.SharedPresets,
                    Store.LoadState().LastServerContact);
                return new PipeResponse(true, "ok", GacJson.Serialize(status));
            }
            case TrayRequest.NetRepair:
            {
                var body = msg.Body<NetRepairBody>();
                if (body == null || !Enum.IsDefined(body.Action)) return new PipeResponse(false, "허용되지 않은 작업입니다.");
                _log.LogInformation("Net repair {Action} requested by {User}", body.Action, user);
                return await _netRepair.RepairAsync(body.Action, ct);
            }
            case TrayRequest.ShutdownExtend:
                return Shutdown.Extend(user);
            case TrayRequest.ShutdownNow:
                return await Shutdown.ShutdownNowAsync(user, ct);
            case TrayRequest.SubmitNetDiagReport:
            {
                var report = msg.Body<NetDiagReport>();
                if (report == null) return new PipeResponse(false, "진단 결과가 비어 있습니다.");
                var now = _time.GetUtcNow();
                var summary = (report.Summary ?? "").Length > 500 ? report.Summary![..500] : report.Summary ?? "";
                Queue.Enqueue(new ModuleReport(Guid.NewGuid(), null, ModuleIds.NetDiag, ModuleActions.NetDiagRun, report.Healthy, $"[{user}] {summary}",
                    GacJson.Serialize(report), now, now, ReportTrigger.Tray));
                return new PipeResponse(true, "진단 결과를 관리자에게 보냈습니다.");
            }
            default:
                return new PipeResponse(false, "허용되지 않은 요청입니다.");
        }
    }

    public async Task<string> DownloadVerifiedAsync(string packageId, string fileName, string sha256, CancellationToken ct)
    {
        var client = Client ?? throw new IOException("GAC 서버에 등록되지 않았습니다.");
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is not (".exe" or ".msi")) throw new InvalidDataException("지원하지 않는 설치 파일 형식입니다.");
        var target = Path.Combine(Store.PackagesDir, sha256.ToUpperInvariant() + ext);
        await client.DownloadPackageAsync(packageId, sha256, target, ct);
        return target;
    }
}
