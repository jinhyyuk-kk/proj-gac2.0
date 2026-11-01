using System.Security.Cryptography;
using Dapper;
using Gac.Server.Accounts;
using Gac.Server.Data;
using Gac.Shared;
using Gac.Shared.Contracts;
using Gac.Shared.Security;

namespace Gac.Server.Pcs;

public sealed class PcService(Db db, SettingsStore settings, AuditLog audit, TimeProvider time)
{
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(2);

    private sealed class PcRow
    {
        public string id = ""; public string name = ""; public long? group_id; public string? group_name; public long? last_seen;
        public string? ip; public string? os; public string? logged_on_user; public string? agent_version;
        public string? alias; public long work_pc; public long revoked;
    }

    public string EnrollCode
    {
        get
        {
            var code = settings.Get(SettingsStore.Keys.EnrollCode);
            if (code == null) { code = EnrollmentProof.NewCode(); settings.Set(SettingsStore.Keys.EnrollCode, code); }
            return code;
        }
    }

    public string RegenerateEnrollCode(Session actor)
    {
        var code = EnrollmentProof.NewCode();
        settings.Set(SettingsStore.Keys.EnrollCode, code);
        audit.Write(actor.UserName, "server.regenerate_enroll_code");
        return code;
    }

    public bool CheckCode(string code) =>
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(EnrollmentProof.NormalizeCode(code ?? "")),
            System.Text.Encoding.UTF8.GetBytes(EnrollmentProof.NormalizeCode(EnrollCode)));

    /// <summary>
    /// Enrolls (or re-enrolls) an agent. An existing record is reused only when the agent proves it holds that record's
    /// current key (HMAC over a re-enroll message); otherwise a new record is always created. Client-supplied names or
    /// MAC addresses never select an existing identity.
    /// </summary>
    public (string AgentId, byte[] Key) Enroll(EnrollRequest req, string? fingerprint = null)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var name = (req.MachineName ?? "").Trim();
        if (name.Length is 0 or > 64) throw new GacException("PC 이름이 올바르지 않습니다.");
        var mac = string.IsNullOrWhiteSpace(req.MacAddress) ? null : req.MacAddress.Trim().ToUpperInvariant();
        req = req with { MacAddress = mac };
        string? existing = null;
        if (!string.IsNullOrEmpty(req.PreviousAgentId) && !string.IsNullOrEmpty(req.PreviousProof) && GetAgentKey(req.PreviousAgentId) is { } oldKey
            && EnrollmentProof.VerifyReenroll(oldKey, req.PreviousAgentId, name, req.PreviousProof))
            existing = req.PreviousAgentId;
        using var c = db.Open();
        var id = existing ?? Guid.NewGuid().ToString("N");
        if (existing == null)
            c.Execute("INSERT INTO pcs(id, name, agent_key, enrolled_at, os, agent_version, mac, fingerprint, revoked) VALUES(@id, @name, @key, @now, @os, @ver, @mac, @fp, 0)",
                new { id, name, key = Convert.ToBase64String(key), now = Time.Ms(time.GetUtcNow()), os = req.OsVersion, ver = req.AgentVersion, mac = req.MacAddress, fp = fingerprint });
        else
            c.Execute("UPDATE pcs SET agent_key = @key, os = @os, agent_version = @ver, mac = @mac, fingerprint = COALESCE(@fp, fingerprint), revoked = 0 WHERE id = @id",
                new { id, key = Convert.ToBase64String(key), os = req.OsVersion, ver = req.AgentVersion, mac = req.MacAddress, fp = fingerprint });
        audit.Write("agent:" + name, existing == null ? "pc.enroll" : "pc.reenroll", name);
        return (id, key);
    }

    public bool IsRevoked(string agentId)
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<long>("SELECT revoked FROM pcs WHERE id = @agentId", new { agentId }) == 1;
    }

    public byte[]? GetAgentKey(string agentId)
    {
        using var c = db.Open();
        var k = c.QuerySingleOrDefault<string>("SELECT agent_key FROM pcs WHERE id = @agentId", new { agentId });
        return k == null ? null : Convert.FromBase64String(k);
    }

    public void Touch(string agentId, PollRequest poll)
    {
        using var c = db.Open();
        c.Execute("UPDATE pcs SET last_seen = @now, ip = @ip, os = COALESCE(@os, os), logged_on_user = @user, agent_version = @ver WHERE id = @agentId",
            new { now = Time.Ms(time.GetUtcNow()), ip = poll.IpAddress, os = poll.OsVersion, user = poll.LoggedOnUser, ver = poll.AgentVersion, agentId });
    }

    public int? GetGroupId(string pcId)
    {
        using var c = db.Open();
        return (int?)c.QuerySingleOrDefault<long?>("SELECT group_id FROM pcs WHERE id = @pcId", new { pcId });
    }

    public IReadOnlyList<PcSummary> List(Session s)
    {
        using var c = db.Open();
        var rows = c.Query<PcRow>("SELECT p.*, g.name AS group_name FROM pcs p LEFT JOIN pc_groups g ON g.id = p.group_id ORDER BY g.name, p.name").ToList();
        var latest = c.Query<(string pc_id, string module, string? data)>("SELECT pc_id, module, data FROM pc_latest WHERE module IN ('health','update','errors')")
            .GroupBy(x => x.pc_id).ToDictionary(g => g.Key, g => g.ToDictionary(x => x.module, x => x.data));
        var now = time.GetUtcNow();
        return rows.Where(r => s.CanAccessGroup((int?)r.group_id)).Select(r =>
        {
            latest.TryGetValue(r.id, out var l);
            double? minFree = null; int? pending = null; int? errors = null; var reboot = false;
            if (l != null)
            {
                if (l.TryGetValue(ModuleIds.Health, out var h) && h != null && TryParse<HealthReport>(h) is { } hr && hr.Volumes.Count > 0)
                    minFree = hr.Volumes.Min(v => v.FreePercent);
                if (l.TryGetValue(ModuleIds.Update, out var u) && u != null && TryParse<UpdateReport>(u) is { } ur)
                { pending = ur.Pending.Count; reboot = ur.RebootRequired; }
                if (l.TryGetValue(ModuleIds.Errors, out var e) && e != null && TryParse<ErrorsReport>(e) is { } er)
                    errors = er.Events.Sum(x => x.Count) + er.FailedServices.Count;
            }
            var lastSeen = Time.At(r.last_seen);
            var display = string.IsNullOrWhiteSpace(r.alias) ? r.name : r.alias!;
            return new PcSummary(r.id, display, (int?)r.group_id, r.group_name, lastSeen.HasValue && now - lastSeen.Value < OnlineWindow, lastSeen,
                r.ip, r.os, r.logged_on_user, r.agent_version, minFree, pending, errors, reboot, r.name, r.work_pc == 1, r.revoked == 1, string.IsNullOrWhiteSpace(r.alias) ? null : r.alias);
        }).ToList();
    }

    /// <summary>Sets or clears a PC's alias (CHANGE-001 section 3). Never changes the Windows computer name.</summary>
    public void SetAlias(Session actor, string pcId, string? alias)
    {
        alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        if (alias is { Length: > 64 }) throw new GacException("별명은 64자 이하여야 합니다.");
        using var c = db.Open();
        var name = c.QuerySingleOrDefault<string>("SELECT name FROM pcs WHERE id = @pcId", new { pcId }) ?? throw new GacException("PC를 찾을 수 없습니다.", 404);
        c.Execute("UPDATE pcs SET alias = @alias WHERE id = @pcId", new { alias, pcId });
        audit.Write(actor.UserName, "pc.alias", name, alias ?? "(별명 지움)");
    }

    /// <summary>PC ids in the given groups the session may access (used when targeting period presets by group).</summary>
    public IReadOnlyList<string> PcIdsInGroups(Session s, IEnumerable<int> groupIds)
    {
        var ids = groupIds.Where(g => s.CanAccessGroup(g)).Distinct().ToList();
        if (ids.Count == 0) return [];
        using var c = db.Open();
        return c.Query<string>("SELECT id FROM pcs WHERE group_id IN @ids", new { ids }).ToList();
    }

    public void Move(Session actor, MovePcRequest req)
    {
        using var c = db.Open();
        if (req.GroupId.HasValue && c.ExecuteScalar<long>("SELECT COUNT(*) FROM pc_groups WHERE id = @g", new { g = req.GroupId }) == 0)
            throw new GacException("그룹을 찾을 수 없습니다.", 404);
        foreach (var id in req.PcIds) c.Execute("UPDATE pcs SET group_id = @g WHERE id = @id", new { g = req.GroupId, id });
        audit.Write(actor.UserName, "pc.move", string.Join(",", req.PcIds), $"group={req.GroupId}");
    }

    public void Delete(Session actor, string pcId)
    {
        using var c = db.Open();
        var name = c.QuerySingleOrDefault<string>("SELECT name FROM pcs WHERE id = @pcId", new { pcId }) ?? throw new GacException("PC를 찾을 수 없습니다.", 404);
        c.Execute("DELETE FROM pcs WHERE id = @pcId", new { pcId });
        audit.Write(actor.UserName, "pc.delete", name);
    }

    public IReadOnlyList<PcGroup> Groups(Session s)
    {
        using var c = db.Open();
        return c.Query<(long id, string name, long cnt)>("SELECT g.id, g.name, (SELECT COUNT(*) FROM pcs p WHERE p.group_id = g.id) FROM pc_groups g ORDER BY g.name")
            .Where(g => s.CanAccessGroup((int)g.id)).Select(g => new PcGroup((int)g.id, g.name, (int)g.cnt)).ToList();
    }

    public PcGroup CreateGroup(Session actor, string name)
    {
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 40) throw new GacException("그룹 이름을 1~40자로 입력하세요.");
        using var c = db.Open();
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM pc_groups WHERE name = @name", new { name }) > 0) throw new GacException("같은 이름의 그룹이 이미 있습니다.", 409);
        var id = (int)c.ExecuteScalar<long>("INSERT INTO pc_groups(name) VALUES(@name); SELECT last_insert_rowid();", new { name });
        audit.Write(actor.UserName, "group.create", name);
        return new PcGroup(id, name, 0);
    }

    public void RenameGroup(Session actor, int id, string name)
    {
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 40) throw new GacException("그룹 이름을 1~40자로 입력하세요.");
        using var c = db.Open();
        if (c.Execute("UPDATE pc_groups SET name = @name WHERE id = @id", new { name, id }) == 0) throw new GacException("그룹을 찾을 수 없습니다.", 404);
        audit.Write(actor.UserName, "group.rename", name);
    }

    public void DeleteGroup(Session actor, int id)
    {
        using var c = db.Open();
        var name = c.QuerySingleOrDefault<string>("SELECT name FROM pc_groups WHERE id = @id", new { id }) ?? throw new GacException("그룹을 찾을 수 없습니다.", 404);
        c.Execute("DELETE FROM pc_groups WHERE id = @id", new { id });
        audit.Write(actor.UserName, "group.delete", name);
    }

    private static T? TryParse<T>(string json) where T : class
    {
        try { return GacJson.Deserialize<T>(json); } catch { return null; }
    }
}
