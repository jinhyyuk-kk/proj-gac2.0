using Dapper;
using Gac.Server.Accounts;
using Gac.Server.Data;
using Gac.Server.Hosting;
using Gac.Shared;
using Gac.Shared.ClassMode;
using Gac.Shared.Contracts;
using Gac.Shared.Security;

namespace Gac.Server.Periods;

/// <summary>
/// Console-created period presets (CHANGE-001 2.3). Presets are stored unsigned; when delivered to a PC they are signed
/// with the server ECDSA key so the agent can trust a preset that may contain <c>lock_pc</c>. Work PCs get the preset
/// with <c>lock_pc</c> removed by the server (the exemption is a server decision, 2.4).
/// </summary>
public sealed class PeriodService(Db db, AuditLog audit, ServerIdentity identity, TimeProvider time)
{
    public const int MaxDays = 400;

    private sealed class Row
    {
        public string id = ""; public string name = ""; public string preset = ""; public long has_lock;
        public long start_at; public long end_at; public string started_by = ""; public long stopped;
    }

    public PeriodPresetInfo Create(Session actor, CreatePeriodRequest req, Func<IEnumerable<int>, IEnumerable<string>> pcsInGroups, Func<string, bool> canTargetPc)
    {
        if (string.IsNullOrWhiteSpace(req.PeriodName) || req.PeriodName.Length > 40) throw new GacException("기간 이름을 1~40자로 입력하세요.");
        if (req.Days is < 1 or > MaxDays) throw new GacException($"기간(일)은 1~{MaxDays} 사이여야 합니다.");
        var (file, result) = PresetValidator.Parse(WrapAsFile(req.PresetJson), allowRunCommand: true);
        if (file == null || file.Presets.Count != 1) throw new GacException(file == null ? result.ErrorText : "기간제 프리셋은 하나의 프리셋만 담아야 합니다.");
        var preset = file.Presets[0];
        var hasLock = preset.Actions.Any(a => a.Type == PresetActionTypes.LockPc);

        // Explicit PcIds must be in the session's accessible groups (same rule as command dispatch, security review).
        foreach (var pc in req.PcIds.Distinct())
            if (!canTargetPc(pc)) throw new GacException("담당 그룹이 아닌 PC는 대상으로 지정할 수 없습니다.", 403);
        var targets = req.PcIds.Concat(pcsInGroups(req.GroupIds ?? [])).Distinct().ToList();
        if (targets.Count == 0) throw new GacException("대상 PC를 선택하세요.");

        var id = Guid.NewGuid().ToString("N");
        var start = req.StartAt == default ? time.GetUtcNow() : req.StartAt;
        var end = start.AddDays(req.Days);
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("INSERT INTO periods(id, name, preset, has_lock, start_at, end_at, started_by, created_at, stopped) VALUES(@id,@name,@preset,@lock,@s,@e,@by,@now,0)",
            new { id, name = req.PeriodName.Trim(), preset = GacJson.Serialize(preset), @lock = hasLock ? 1 : 0, s = Time.Ms(start), e = Time.Ms(end), by = actor.UserName, now = Time.Ms(time.GetUtcNow()) }, tx);
        foreach (var pc in targets) c.Execute("INSERT OR IGNORE INTO period_targets(period_id, pc_id) VALUES(@id,@pc)", new { id, pc }, tx);
        tx.Commit();
        audit.Write(actor.UserName, "period.create", req.PeriodName, $"{targets.Count} PCs, {req.Days}d, lock={hasLock}");
        return new PeriodPresetInfo(id, req.PeriodName.Trim(), start, end, actor.UserName, targets.Count, true, hasLock);
    }

    public void Stop(Session actor, string id)
    {
        using var c = db.Open();
        var name = c.QuerySingleOrDefault<string>("SELECT name FROM periods WHERE id = @id", new { id }) ?? throw new GacException("기간을 찾을 수 없습니다.", 404);
        c.Execute("UPDATE periods SET stopped = 1 WHERE id = @id", new { id });
        audit.Write(actor.UserName, "period.stop", name);
    }

    public IReadOnlyList<PeriodPresetInfo> List()
    {
        using var c = db.Open();
        var now = time.GetUtcNow();
        return c.Query<Row>("SELECT p.*, (SELECT COUNT(*) FROM period_targets t WHERE t.period_id = p.id) AS n FROM periods p ORDER BY created_at DESC")
            .Select(r => new PeriodPresetInfo(r.id, r.name, Time.At(r.start_at), Time.At(r.end_at), r.started_by,
                c.ExecuteScalar<int>("SELECT COUNT(*) FROM period_targets WHERE period_id = @id", new { id = r.id }),
                r.stopped == 0 && Time.At(r.end_at) > now && Time.At(r.start_at) <= now, r.has_lock == 1)).ToList();
    }

    /// <summary>Signed period presets currently active for <paramref name="pcId"/>. On a work PC, lock_pc is stripped first.</summary>
    public IReadOnlyList<PeriodPreset> ActiveForPc(string pcId, bool workPc)
    {
        var now = time.GetUtcNow();
        using var c = db.Open();
        var rows = c.Query<Row>("""
            SELECT p.* FROM periods p JOIN period_targets t ON t.period_id = p.id
            WHERE t.pc_id = @pcId AND p.stopped = 0 AND p.start_at <= @now AND p.end_at > @now
            """, new { pcId, now = Time.Ms(now) });
        var result = new List<PeriodPreset>();
        foreach (var r in rows)
        {
            var preset = GacJson.Deserialize<ClassPreset>(r.preset)!;
            if (workPc && r.has_lock == 1)
                preset.Actions = preset.Actions.Where(a => a.Type != PresetActionTypes.LockPc).ToList();
            var presetJson = GacJson.Serialize(preset);
            var bundle = PresetSigner.Sign(presetJson, Time.At(r.start_at), identity.PresetKey);
            result.Add(new PeriodPreset(r.id, r.name, presetJson, Time.At(r.start_at), Time.At(r.end_at), bundle.Signature));
        }
        return result;
    }

    private static string WrapAsFile(string presetJson)
    {
        // Accept either a bare ClassPreset or a PresetFile with one preset.
        var trimmed = (presetJson ?? "").TrimStart();
        if (trimmed.Contains("\"presets\"")) return presetJson!;
        return $"{{\"version\":1,\"presets\":[{presetJson}]}}";
    }
}
