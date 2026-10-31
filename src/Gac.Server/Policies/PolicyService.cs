using System.Security.Cryptography;
using System.Text;
using Dapper;
using Gac.Server.Accounts;
using Gac.Server.Data;
using Gac.Server.Hosting;
using Gac.Shared;
using Gac.Shared.ClassMode;
using Gac.Shared.Contracts;
using Gac.Shared.Security;

namespace Gac.Server.Policies;

public sealed class PolicyService(Db db, SettingsStore settings, AuditLog audit, ServerIdentity identity, TimeProvider time)
{
    public static readonly ServerSettings DefaultSettings = new(null, null, new InspectPolicy().WallpaperPath, [], 30);

    public ServerSettings GetSettings() => settings.GetJson(SettingsStore.Keys.ServerSettings, DefaultSettings);

    public void SaveSettings(Session actor, ServerSettings s)
    {
        if (s.PollSeconds is < 10 or > 600) throw new GacException("확인 주기는 10~600초 사이여야 합니다.");
        if (s.NetDiagExternalTarget is { Length: > 0 } t && Uri.CheckHostName(t) == UriHostNameType.Unknown)
            throw new GacException("외부 확인 주소 형식이 올바르지 않습니다 (예: 8.8.8.8 또는 example.com).");
        settings.SetJson(SettingsStore.Keys.ServerSettings, s with { ProgramAllowlist = s.ProgramAllowlist.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToArray() });
        Bump();
        audit.Write(actor.UserName, "server.settings", detail: GacJson.Serialize(s));
    }

    public GroupPolicy GetGroupPolicy(int groupId)
    {
        using var c = db.Open();
        var json = c.QuerySingleOrDefault<string>("SELECT json FROM group_policies WHERE group_id = @groupId", new { groupId });
        var p = json == null ? null : GacJson.Deserialize<GroupPolicy>(json);
        return p == null ? new GroupPolicy(groupId, new CleanPolicy(), new ShutdownPolicy(), new HealthPolicy(), new ErrorsPolicy()) : p with { GroupId = groupId };
    }

    public void SaveGroupPolicy(Session actor, GroupPolicy p)
    {
        if (!TimeOnly.TryParse(p.Shutdown.Time, out _)) throw new GacException("종료 시각 형식이 올바르지 않습니다 (예: 17:30).");
        if (p.Clean.ScheduleTime is { Length: > 0 } st && !TimeOnly.TryParse(st, out _)) throw new GacException("정리 시각 형식이 올바르지 않습니다 (예: 12:40).");
        if (p.Shutdown.MaxExtensions is < 0 or > 10) throw new GacException("연장 횟수는 0~10회 사이여야 합니다.");
        if (p.Shutdown.WarningMinutes is < 1 or > 30) throw new GacException("경고 시간은 1~30분 사이여야 합니다.");
        if (p.Shutdown.ForceGraceMinutes is < 1 or > 60) throw new GacException("강제 종료 유예 시간은 1~60분 사이여야 합니다.");
        if (p.Clean.DownloadsRetentionDays is < 1 or > 365) throw new GacException("다운로드 보관 기간은 1~365일 사이여야 합니다.");
        if (p.Health.IntervalHours is < 1 or > 168) throw new GacException("상태 보고 주기는 1~168시간 사이여야 합니다.");
        using var c = db.Open();
        var name = c.QuerySingleOrDefault<string>("SELECT name FROM pc_groups WHERE id = @g", new { g = p.GroupId }) ?? throw new GacException("그룹을 찾을 수 없습니다.", 404);
        c.Execute("INSERT INTO group_policies(group_id, json, updated_at) VALUES(@g, @j, @n) ON CONFLICT(group_id) DO UPDATE SET json = excluded.json, updated_at = excluded.updated_at",
            new { g = p.GroupId, j = GacJson.Serialize(p), n = Time.Ms(time.GetUtcNow()) });
        Bump();
        audit.Write(actor.UserName, "group.policy", name, GacJson.Serialize(p));
    }

    public SharedPresetInfo GetSharedPresets() =>
        settings.GetJson<SharedPresetInfo?>(SettingsStore.Keys.SharedPresets, null) ?? new SharedPresetInfo(GacJson.Serialize(new PresetFile()), null, null);

    public void SaveSharedPresets(Session actor, string presetsJson)
    {
        var (file, result) = PresetValidator.Parse(presetsJson, allowRunCommand: true);
        if (file == null) throw new GacException(result.ErrorText);
        settings.SetJson(SettingsStore.Keys.SharedPresets, new SharedPresetInfo(GacJson.Serialize(file), time.GetUtcNow(), actor.UserName));
        Bump();
        audit.Write(actor.UserName, "presets.shared.save", detail: $"{file.Presets.Count} presets");
    }

    /// <summary>Composes the effective policy for a PC. Shared presets are signed with the server ECDSA key.</summary>
    public (AgentPolicy Policy, string Version) ForPc(int? groupId)
    {
        var s = GetSettings();
        var gp = groupId.HasValue ? GetGroupPolicy(groupId.Value) : new GroupPolicy(0, new CleanPolicy(), new ShutdownPolicy(), new HealthPolicy(), new ErrorsPolicy());
        var presets = GetSharedPresets();
        var bundle = presets.UpdatedAt.HasValue ? PresetSigner.Sign(presets.PresetsJson, presets.UpdatedAt.Value, identity.PresetKey) : null;
        var policy = new AgentPolicy
        {
            Clean = gp.Clean,
            Shutdown = gp.Shutdown,
            Health = gp.Health,
            Errors = gp.Errors,
            NetDiag = new NetDiagPolicy { ExternalTarget = s.NetDiagExternalTarget, DnsTestName = s.NetDiagDnsTestName },
            Inspect = new InspectPolicy { WallpaperPath = s.WallpaperPath, Allowlist = s.ProgramAllowlist },
            SharedPresets = bundle,
        };
        // Version depends on content (minus signature randomness) so agents only re-apply on change.
        var basis = GacJson.Serialize(policy with { SharedPresets = null }) + (bundle == null ? "" : bundle.PresetsJson + bundle.IssuedAt.ToUnixTimeMilliseconds());
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(basis)))[..16];
        return (policy, version);
    }

    public int PollSeconds => GetSettings().PollSeconds;

    private void Bump() => settings.Set(SettingsStore.Keys.PolicyRevision, time.GetUtcNow().ToUnixTimeMilliseconds().ToString());
}
