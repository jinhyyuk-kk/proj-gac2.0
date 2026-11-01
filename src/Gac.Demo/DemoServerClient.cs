using Gac.Console.Services;
using Gac.Shared;
using Gac.Shared.Auth;
using Gac.Shared.ClassMode;
using Gac.Shared.Contracts;

namespace Gac.Demo;

/// <summary>
/// In-memory fake GAC Server for GAC-Demo.exe. Nothing here touches the real machine: no files, no registry, no
/// processes, no sockets. Every module "runs" through <see cref="DemoSimulator"/> after a short fake delay.
/// Everything is discarded when the demo closes.
/// </summary>
public sealed class DemoServerClient : IGacServerClient
{
    private readonly Lock _lock = new();
    private readonly Random _rng = new(20261031);
    private readonly List<PcGroup> _groups = [];
    private readonly List<DemoPc> _pcs = [];
    private readonly List<AccountInfo> _accounts = [];
    private readonly List<(CommandBatchSummary Batch, List<CommandRow> Rows)> _batches = [];
    private readonly List<ReportRow> _reports = [];
    private readonly Dictionary<(string Pc, string Module), ReportRow> _latest = [];
    private readonly List<AuditEntry> _audit = [];
    private readonly List<DeployPackage> _packages = [];
    private readonly Dictionary<int, GroupPolicy> _policies = [];
    private ServerSettings _settings = new(null, null, @"C:\Windows\Web\Wallpaper\Windows\img0.jpg", ["Microsoft", "한컴", "Hancom", "Google Chrome", "Adobe Acrobat", "NVIDIA", "Intel", "Realtek", "AMD", "알집", "곰플레이어"], 30);
    private SharedPresetInfo _shared;
    private PermissionMatrix _matrix = PermissionMatrix.Default();
    private readonly List<BlockedDevice> _blocked = [];
    private readonly List<PendingDevice> _pending = [];
    private readonly List<PeriodPresetInfo> _periods = [];
    private string _code = "DEMO-2026-GAC0";
    private bool _ownerSignedUp;
    private long _auditId;
    private int _nextAccountId = 5;
    private int _nextGroupId = 6;
    private readonly DemoSimulator _sim;
    private readonly int _delayScale;

    public DemoServerClient(int delayScale = 1)
    {
        _delayScale = delayScale;
        _sim = new DemoSimulator(_rng);
        DemoSchool.Populate(this, _rng);
        _shared = new SharedPresetInfo(GacJson.Serialize(DemoSchool.SharedPresets()), DateTimeOffset.Now.AddDays(-3), "owner");
    }

    public bool IsDemo => true;
    public string ServerDisplayName => "체험용 가상 서버";
    public AccountInfo Owner => _accounts[0];
    internal IReadOnlyList<DemoPc> Pcs => _pcs;

    // ---- population helpers (used by DemoSchool) ----
    internal void AddGroup(string name) => _groups.Add(new PcGroup(_groups.Count + 1, name, 0));
    internal void AddPc(DemoPc pc) => _pcs.Add(pc);
    internal void AddAccount(AccountInfo a) => _accounts.Add(a);
    internal void AddPackage(DeployPackage p) => _packages.Add(p);
    internal void AddBlocked(BlockedDevice d) => _blocked.Add(d);
    internal void AddPending(PendingDevice d) => _pending.Add(d);
    internal void SeedReport(DemoPc pc, string module, string action, ModuleSimResult r, DateTimeOffset at)
    {
        var row = new ReportRow(Guid.NewGuid(), pc.Id, pc.Name, module, action, r.Success, r.Summary, r.DataJson, at, ReportTrigger.Schedule);
        _reports.Add(row);
        _latest[(pc.Id, module)] = row;
    }
    internal void SeedAudit(DateTimeOffset at, string actor, string action, string? target, string? detail) =>
        _audit.Add(new AuditEntry(++_auditId, at, actor, action, target, detail));
    internal DemoSimulator Simulator => _sim;

    private Task Delay(int ms) => Task.Delay(ms * _delayScale);

    private void Audit(string action, string? target = null, string? detail = null) =>
        _audit.Add(new AuditEntry(++_auditId, DateTimeOffset.Now, Owner.UserName, action, target, detail));

    // ---- auth ----
    // CHANGE-001 §7: the demo console view starts at the sign-up screen. The first sign-up becomes the Owner and then
    // the 회원가입 button disappears (NeedsOwner flips to false), exactly like the real first-run flow.
    public bool OwnerSignedUp { get { lock (_lock) return _ownerSignedUp; } }
    public Task<SetupStatus> GetSetupAsync() { lock (_lock) return Task.FromResult(new SetupStatus(!_ownerSignedUp, ServerDisplayName, "2.0.0")); }

    public async Task<AccountInfo> CreateOwnerAsync(CreateOwnerRequest request)
    {
        await Delay(200);
        lock (_lock)
        {
            if (_ownerSignedUp) throw new GacClientException("이미 최고 관리자 계정이 있습니다. 회원가입은 한 번만 할 수 있습니다.");
            if (string.IsNullOrWhiteSpace(request.UserName) || request.UserName.Trim().Length < 3) throw new GacClientException("아이디는 3자 이상이어야 합니다.");
            if (string.IsNullOrWhiteSpace(request.DisplayName)) throw new GacClientException("이름을 입력하세요.");
            var pw = request.Password ?? "";
            if (pw.Length < 8 || !pw.Any(char.IsLetter) || !pw.Any(char.IsDigit)) throw new GacClientException("비밀번호는 8자 이상이고 문자와 숫자가 모두 들어가야 합니다.");
            if (string.IsNullOrWhiteSpace(request.EnrollCode)) throw new GacClientException("설치 코드를 입력하세요. (체험 모드에서는 서버 PC 화면에 표시된 코드를 쓰거나 아무 값이나 넣어도 됩니다.)");
            _accounts[0] = _accounts[0] with { UserName = request.UserName.Trim(), DisplayName = request.DisplayName.Trim() };
            _ownerSignedUp = true;
            Audit("account.create_owner", request.UserName.Trim());
            return _accounts[0];
        }
    }

    public Task<LoginResponse> LoginAsync(string userName, string password)
    {
        lock (_lock)
        {
            if (!_ownerSignedUp) throw new GacClientException("먼저 회원가입으로 최고 관리자 계정을 만드세요.");
            return Task.FromResult(new LoginResponse("demo", Owner, DateTimeOffset.Now.AddHours(8)));
        }
    }
    public Task LogoutAsync() => Task.CompletedTask;
    public async Task ChangePasswordAsync(ChangePasswordRequest request) { await Delay(200); Audit("account.change_password", Owner.UserName); }

    // ---- accounts ----
    public async Task<IReadOnlyList<AccountInfo>> GetAccountsAsync() { await Delay(120); lock (_lock) return _accounts.ToList(); }

    public async Task<AccountInfo> CreateAccountAsync(SaveAccountRequest r)
    {
        await Delay(200);
        ValidateAccount(r, isNew: true);
        lock (_lock)
        {
            if (_accounts.Any(a => a.UserName.Equals(r.UserName, StringComparison.OrdinalIgnoreCase))) throw new GacClientException("이미 사용 중인 아이디입니다.");
            var a = new AccountInfo(_nextAccountId++, r.UserName.Trim(), r.DisplayName.Trim(), r.Role, r.Disabled, r.GroupIds, null, null);
            _accounts.Add(a);
            Audit("account.create", a.UserName, $"role={a.Role}");
            return a;
        }
    }

    public async Task<AccountInfo> UpdateAccountAsync(int id, SaveAccountRequest r)
    {
        await Delay(200);
        ValidateAccount(r, isNew: false);
        lock (_lock)
        {
            var i = _accounts.FindIndex(a => a.Id == id);
            if (i < 0) throw new GacClientException("계정을 찾을 수 없습니다.");
            var old = _accounts[i];
            if (old.Role == Role.Owner && (r.Role != Role.Owner || r.Disabled)) throw new GacClientException("최고 관리자는 항상 한 명 있어야 합니다.");
            if (old.Role != Role.Owner && r.Role == Role.Owner) throw new GacClientException("최고 관리자는 한 명만 있을 수 있습니다.");
            _accounts[i] = old with { UserName = r.UserName.Trim(), DisplayName = r.DisplayName.Trim(), Role = r.Role, Disabled = r.Disabled, GroupIds = r.GroupIds };
            Audit("account.update", r.UserName, $"role={r.Role}; disabled={r.Disabled}");
            return _accounts[i];
        }
    }

    public async Task DeleteAccountAsync(int id)
    {
        await Delay(150);
        lock (_lock)
        {
            var a = _accounts.FirstOrDefault(x => x.Id == id) ?? throw new GacClientException("계정을 찾을 수 없습니다.");
            if (a.Role == Role.Owner) throw new GacClientException("최고 관리자 계정은 삭제할 수 없습니다.");
            _accounts.Remove(a);
            Audit("account.delete", a.UserName);
        }
    }

    public async Task ResetPasswordAsync(int id, string newPassword)
    {
        await Delay(150);
        if (newPassword.Length < 8 || !newPassword.Any(char.IsLetter) || !newPassword.Any(char.IsDigit)) throw new GacClientException("비밀번호는 8자 이상이고 문자와 숫자가 모두 들어가야 합니다.");
        lock (_lock) Audit("account.reset_password", _accounts.FirstOrDefault(a => a.Id == id)?.UserName);
    }

    private static void ValidateAccount(SaveAccountRequest r, bool isNew)
    {
        if (string.IsNullOrWhiteSpace(r.UserName) || r.UserName.Trim().Length < 3) throw new GacClientException("아이디는 3자 이상이어야 합니다.");
        if (string.IsNullOrWhiteSpace(r.DisplayName)) throw new GacClientException("이름을 입력하세요.");
        if (isNew && r.Role == Role.Owner) throw new GacClientException("최고 관리자는 한 명만 있을 수 있습니다.");
        if ((isNew || r.Password != null) && (r.Password is null || r.Password.Length < 8 || !r.Password.Any(char.IsLetter) || !r.Password.Any(char.IsDigit)))
            throw new GacClientException("비밀번호는 8자 이상이고 문자와 숫자가 모두 들어가야 합니다.");
    }

    // ---- PCs & groups ----
    public async Task<IReadOnlyList<PcSummary>> GetPcsAsync()
    {
        await Delay(150);
        lock (_lock) return _pcs.Select(p => p.ToSummary(_groups)).ToList();
    }

    public async Task MovePcsAsync(MovePcRequest r)
    {
        await Delay(150);
        lock (_lock)
        {
            foreach (var p in _pcs.Where(p => r.PcIds.Contains(p.Id))) p.GroupId = r.GroupId;
            Audit("pc.move", string.Join(",", _pcs.Where(p => r.PcIds.Contains(p.Id)).Select(p => p.Name)), $"group={r.GroupId}");
        }
    }

    public async Task DeletePcAsync(string pcId)
    {
        await Delay(100);
        lock (_lock) { var p = _pcs.First(x => x.Id == pcId); _pcs.Remove(p); Audit("pc.delete", p.Name); }
    }

    public async Task<IReadOnlyList<PcGroup>> GetGroupsAsync()
    {
        await Delay(80);
        lock (_lock) return _groups.Select(g => g with { PcCount = _pcs.Count(p => p.GroupId == g.Id) }).OrderBy(g => g.Name).ToList();
    }

    public async Task<PcGroup> CreateGroupAsync(string name)
    {
        await Delay(120);
        lock (_lock)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new GacClientException("그룹 이름을 입력하세요.");
            if (_groups.Any(g => g.Name == name)) throw new GacClientException("같은 이름의 그룹이 이미 있습니다.");
            var g = new PcGroup(_nextGroupId++, name, 0);
            _groups.Add(g);
            Audit("group.create", name);
            return g;
        }
    }

    public async Task RenameGroupAsync(int id, string name)
    {
        await Delay(120);
        lock (_lock) { var i = _groups.FindIndex(g => g.Id == id); _groups[i] = _groups[i] with { Name = name.Trim() }; Audit("group.rename", name); }
    }

    public async Task DeleteGroupAsync(int id)
    {
        await Delay(120);
        lock (_lock)
        {
            var g = _groups.First(x => x.Id == id);
            _groups.Remove(g);
            foreach (var p in _pcs.Where(p => p.GroupId == id)) p.GroupId = null;
            Audit("group.delete", g.Name);
        }
    }

    public async Task<GroupPolicy> GetGroupPolicyAsync(int groupId)
    {
        await Delay(100);
        lock (_lock)
            return _policies.TryGetValue(groupId, out var p) ? p
                : new GroupPolicy(groupId, new CleanPolicy(), new ShutdownPolicy { Enabled = groupId == 4, Time = "17:00" }, new HealthPolicy(), new ErrorsPolicy());
    }

    public async Task SaveGroupPolicyAsync(GroupPolicy policy)
    {
        await Delay(200);
        lock (_lock) { _policies[policy.GroupId] = policy; Audit("group.policy", _groups.FirstOrDefault(g => g.Id == policy.GroupId)?.Name, GacJson.Serialize(policy)); }
    }

    // ---- commands ----
    public async Task<SendCommandResponse> SendCommandAsync(SendCommandRequest r)
    {
        await Delay(200);
        if (!ModuleActions.IsKnown(r.Module, r.Action)) throw new GacClientException("알 수 없는 모듈 명령입니다.");
        var batchId = Guid.NewGuid();
        var now = DateTimeOffset.Now;
        List<(DemoPc Pc, CommandRow Row)> started;
        lock (_lock)
        {
            var pcs = _pcs.Where(p => r.PcIds.Contains(p.Id)).ToList();
            var rows = pcs.Select(p => new CommandRow(Guid.NewGuid(), batchId, p.Id, p.Name, r.Module, r.Action,
                p.Online ? CommandStatus.Delivered : CommandStatus.Pending, now, null, p.Online ? "실행 중…" : "PC가 꺼져 있어 다음에 켜지면 실행합니다.", null)).ToList();
            _batches.Insert(0, (new CommandBatchSummary(batchId, r.Module, r.Action, Owner.UserName, now, rows.Count, 0, 0), rows));
            Audit($"command.{r.Module}.{r.Action}", string.Join(", ", pcs.Select(p => p.Name)), r.PayloadJson.Length > 300 ? r.PayloadJson[..300] : r.PayloadJson);
            started = pcs.Zip(rows).Where(x => x.First.Online).Select(x => (x.First, x.Second)).ToList();
        }
        foreach (var (pc, row) in started) _ = CompleteLaterAsync(pc, row, r);
        return new SendCommandResponse(batchId, r.PcIds.Count);
    }

    private async Task CompleteLaterAsync(DemoPc pc, CommandRow row, SendCommandRequest r)
    {
        int delay;
        lock (_lock) delay = _rng.Next(900, 3500) + (r.Module is ModuleIds.Update or ModuleIds.Deploy ? 2500 : 0);
        await Delay(delay);
        lock (_lock)
        {
            var policy = pc.GroupId is { } g && _policies.TryGetValue(g, out var p) ? p : null;
            var res = _sim.Run(pc, r.Module, r.Action, r.PayloadJson, policy, _settings);
            var finished = DateTimeOffset.Now;
            for (var b = 0; b < _batches.Count; b++)
            {
                var (batch, rows) = _batches[b];
                var i = rows.FindIndex(x => x.Id == row.Id);
                if (i < 0) continue;
                rows[i] = rows[i] with { Status = res.Success ? CommandStatus.Succeeded : CommandStatus.Failed, FinishedAt = finished, Summary = res.Summary, DataJson = res.DataJson };
                _batches[b] = (batch with { Done = rows.Count(x => x.FinishedAt != null), Failed = rows.Count(x => x.Status == CommandStatus.Failed) }, rows);
            }
            var report = new ReportRow(Guid.NewGuid(), pc.Id, pc.Name, r.Module, r.Action, res.Success, res.Summary, res.DataJson, finished, ReportTrigger.Command);
            _reports.Insert(0, report);
            if (r.Module != ModuleIds.Inspect || r.Action == ModuleActions.InspectRun) _latest[(pc.Id, r.Module)] = report;
        }
    }

    public async Task<IReadOnlyList<CommandBatchSummary>> GetBatchesAsync(int limit = 100) { await Delay(80); lock (_lock) return _batches.Take(limit).Select(b => b.Batch).ToList(); }

    public async Task<IReadOnlyList<CommandRow>> GetBatchAsync(Guid batchId)
    {
        await Delay(80);
        lock (_lock) return _batches.FirstOrDefault(b => b.Batch.BatchId == batchId).Rows?.ToList() ?? [];
    }

    public async Task<IReadOnlyList<ReportRow>> GetReportsAsync(string? module, string? pcId, int limit = 200)
    {
        await Delay(100);
        lock (_lock) return _reports.Where(r => (module == null || r.Module == module) && (pcId == null || r.PcId == pcId)).OrderByDescending(r => r.FinishedAt).Take(limit).ToList();
    }

    public async Task<IReadOnlyList<ReportRow>> GetLatestAsync(string module)
    {
        await Delay(100);
        lock (_lock) return _latest.Where(kv => kv.Key.Module == module).Select(kv => kv.Value).OrderBy(r => r.PcName).ToList();
    }

    // ---- presets, packages, audit, settings ----
    public async Task<SharedPresetInfo> GetSharedPresetsAsync() { await Delay(80); lock (_lock) return _shared; }

    public async Task SaveSharedPresetsAsync(string presetsJson)
    {
        await Delay(200);
        var (file, result) = PresetValidator.Parse(presetsJson, allowRunCommand: true);
        if (file == null) throw new GacClientException(result.ErrorText);
        lock (_lock) { _shared = new SharedPresetInfo(presetsJson, DateTimeOffset.Now, Owner.UserName); Audit("presets.shared.save", detail: $"{file.Presets.Count} presets"); }
    }

    public async Task<IReadOnlyList<DeployPackage>> GetPackagesAsync() { await Delay(80); lock (_lock) return _packages.ToList(); }

    /// <summary>Does not read the file: the demo only pretends to upload it.</summary>
    public async Task<DeployPackage> UploadPackageAsync(string name, string filePath, string silentArgs, IProgress<double>? progress, CancellationToken ct)
    {
        for (var i = 1; i <= 10; i++) { await Delay(120); progress?.Report(i / 10.0); }
        var fileName = Path.GetFileName(filePath);
        lock (_lock)
        {
            var p = new DeployPackage(Guid.NewGuid().ToString("N"), name, fileName, Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray()),
                _rng.Next(5, 400) * 1024L * 1024, silentArgs, DateTimeOffset.Now, Owner.UserName);
            _packages.Insert(0, p);
            Audit("package.add", name, fileName);
            return p;
        }
    }

    public async Task DeletePackageAsync(string id) { await Delay(100); lock (_lock) { var p = _packages.First(x => x.Id == id); _packages.Remove(p); Audit("package.delete", p.Name); } }

    public async Task<IReadOnlyList<AuditEntry>> GetAuditAsync(int limit = 2000) { await Delay(100); lock (_lock) return _audit.OrderByDescending(a => a.Id).Take(limit).ToList(); }
    public async Task<ServerSettings> GetSettingsAsync() { await Delay(80); lock (_lock) return _settings; }
    public async Task SaveSettingsAsync(ServerSettings settings) { await Delay(150); lock (_lock) { _settings = settings; Audit("server.settings"); } }
    public async Task<EnrollmentInfo> GetEnrollmentAsync() { await Delay(80); return new EnrollmentInfo(_code, "ADMIN-PC", 8443, "체험 모드"); }

    public async Task<EnrollmentInfo> RegenerateEnrollmentAsync()
    {
        await Delay(150);
        lock (_lock) { _code = $"DEMO-{_rng.Next(1000, 9999)}-{_rng.Next(1000, 9999)}"; Audit("server.regenerate_enroll_code"); }
        return await GetEnrollmentAsync();
    }

    // ---- CHANGE-001: permission matrix, ownership, devices, aliases, periods ----
    public async Task<PermissionMatrix> GetMatrixAsync() { await Delay(80); lock (_lock) return _matrix; }
    public async Task SaveMatrixAsync(PermissionMatrix matrix) { await Delay(120); lock (_lock) { _matrix = matrix; Audit("accounts.permission_matrix"); } }

    public async Task TransferOwnershipAsync(int toAccountId)
    {
        await Delay(150);
        lock (_lock)
        {
            var target = _accounts.FirstOrDefault(a => a.Id == toAccountId) ?? throw new GacClientException("대상 계정을 찾을 수 없습니다.");
            if (target.Id == Owner.Id) throw new GacClientException("자기 자신에게 소유권을 넘길 수 없습니다.");
            var i0 = _accounts.FindIndex(a => a.Role == Role.Owner);
            _accounts[i0] = _accounts[i0] with { Role = Role.Admin };
            var it = _accounts.FindIndex(a => a.Id == toAccountId);
            _accounts[it] = _accounts[it] with { Role = Role.Owner };
            var owner = _accounts[it]; _accounts.RemoveAt(it); _accounts.Insert(0, owner);
            Audit("account.transfer_ownership", target.UserName);
        }
    }

    public async Task SetAliasAsync(string pcId, string? alias)
    {
        await Delay(100);
        lock (_lock) { var pc = _pcs.First(p => p.Id == pcId); pc.Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim(); Audit("pc.alias", pc.Name, pc.Alias ?? "(지움)"); }
    }

    public async Task RevokePcAsync(string pcId) { await Delay(120); lock (_lock) { var pc = _pcs.First(p => p.Id == pcId); pc.Revoked = true; Audit("device.revoke", pc.Name); } }
    public async Task RemoveWorkExemptionAsync(string pcId) { await Delay(100); lock (_lock) { var pc = _pcs.First(p => p.Id == pcId); pc.WorkPc = false; Audit("device.remove_work_exemption", pc.Name); } }

    public async Task<IReadOnlyList<BlockedDevice>> GetBlockedDevicesAsync() { await Delay(100); lock (_lock) return _blocked.ToList(); }
    public async Task<IReadOnlyList<PendingDevice>> GetPendingDevicesAsync() { await Delay(100); lock (_lock) return _pending.ToList(); }
    public async Task ApproveDeviceAsync(string fingerprint) { await Delay(120); lock (_lock) { _pending.RemoveAll(d => d.Fingerprint == fingerprint); Audit("device.approve", fingerprint); } }
    public async Task ClearConsoleFirstAsync(string fingerprint) { await Delay(120); lock (_lock) { _blocked.RemoveAll(d => d.Fingerprint == fingerprint && !d.PermanentlyBlocked); Audit("device.clear_console_first", fingerprint); } }

    public async Task<IReadOnlyList<PeriodPresetInfo>> GetPeriodsAsync() { await Delay(90); lock (_lock) return _periods.ToList(); }

    public async Task<PeriodPresetInfo> CreatePeriodAsync(CreatePeriodRequest r)
    {
        await Delay(200);
        var (file, result) = PresetValidator.Parse(r.PresetJson.TrimStart().StartsWith('{') && r.PresetJson.Contains("\"presets\"") ? r.PresetJson : $"{{\"version\":1,\"presets\":[{r.PresetJson}]}}", allowRunCommand: true);
        if (file == null || file.Presets.Count != 1) throw new GacClientException(file == null ? result.ErrorText : "프리셋은 하나만 담아야 합니다.");
        if (r.Days is < 1 or > 400) throw new GacClientException("기간은 1~400일이어야 합니다.");
        var targets = r.PcIds.Count + (r.GroupIds?.Sum(g => _pcs.Count(p => p.GroupId == g)) ?? 0);
        if (targets == 0) throw new GacClientException("대상 PC를 선택하세요.");
        var hasLock = file.Presets[0].Actions.Any(a => a.Type == PresetActionTypes.LockPc);
        var start = r.StartAt == default ? DateTimeOffset.Now : r.StartAt;
        var info = new PeriodPresetInfo(Guid.NewGuid().ToString("N"), r.PeriodName, start, start.AddDays(r.Days), Owner.UserName, targets, true, hasLock);
        lock (_lock) { _periods.Insert(0, info); Audit("period.create", r.PeriodName, $"{targets} PCs, {r.Days}d, lock={hasLock}"); }
        return info;
    }

    public async Task StopPeriodAsync(string id)
    {
        await Delay(120);
        lock (_lock)
        {
            var i = _periods.FindIndex(p => p.Id == id);
            if (i >= 0) { _periods[i] = _periods[i] with { Running = false }; Audit("period.stop", _periods[i].PeriodName); }
        }
    }

    public void Dispose() { }
}
