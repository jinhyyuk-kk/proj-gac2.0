using Dapper;
using Gac.Server.Accounts;
using Gac.Server.Data;
using Gac.Shared;
using Gac.Shared.Contracts;

namespace Gac.Server.Devices;

/// <summary>
/// Persists device decisions by hardware fingerprint and enforces the desktop-only + install-order rules at the three
/// server choke points (enrollment, heartbeat, command dispatch). Console-first machines and work-PC exemptions are
/// derived from recorded Console logins, never from what the client claims.
/// </summary>
public sealed class DeviceService(Db db, AuditLog audit, DeviceGuard guard, TimeProvider time)
{
    public DeviceGuard Guard => guard;

    private sealed class Row
    {
        public string fingerprint = ""; public string? pc_id; public string status = ""; public string? verdict; public string? reason;
        public long permanent; public string? name; public string? model; public string? ip; public string? report;
        public long first_seen; public long last_seen; public long? console_login_at; public long? agent_enrolled_at;
    }

    /// <summary>Records the report and decides. Does not yet bind a pc_id (enrollment does that on acceptance).</summary>
    public (DeviceDecision Decision, string Fingerprint) Evaluate(DeviceReport report, string? ip)
    {
        var fp = DeviceGuard.Fingerprint(report);
        var now = Time.Ms(time.GetUtcNow());
        using var c = db.Open();
        var existing = c.QuerySingleOrDefault<Row>("SELECT * FROM devices WHERE fingerprint = @fp", new { fp });

        // Permanently blocked devices stay blocked no matter what they now report.
        if (existing is { permanent: 1, status: "blocked" })
            return (new DeviceDecision(Enum.TryParse<DeviceVerdict>(existing.verdict, out var v) ? v : DeviceVerdict.LaptopChassis, false, false, true, existing.reason ?? DeviceGuard.LaptopMessage), fp);

        var consoleFirst = existing is { console_login_at: not null }
            && (existing.agent_enrolled_at is null || existing.console_login_at < existing.agent_enrolled_at);

        var decision = guard.Decide(report, consoleFirst);
        var status = decision.Accepted ? "accepted" : decision.NeedsReview ? "pending" : "blocked";

        c.Execute("""
            INSERT INTO devices(fingerprint, status, verdict, reason, permanent, name, model, ip, report, first_seen, last_seen, agent_enrolled_at, console_login_at)
            VALUES(@fp, @status, @verdict, @reason, @perm, @name, @model, @ip, @report, @now, @now, NULL, @consoleAt)
            ON CONFLICT(fingerprint) DO UPDATE SET status = @status, verdict = @verdict, reason = @reason,
                permanent = MAX(permanent, @perm), name = @name, model = @model, ip = @ip, report = @report, last_seen = @now
            """, new
        {
            fp, status, verdict = decision.Verdict.ToString(), reason = decision.Reason, perm = decision.PermanentlyBlocked ? 1 : 0,
            name = report.ComputerName, model = report.Model, ip, report = GacJson.Serialize(report), now,
            consoleAt = existing?.console_login_at,
        });

        if (!decision.Accepted)
            audit.Write("device:" + report.ComputerName, decision.NeedsReview ? "device.pending" : "device.blocked", fp[..Math.Min(12, fp.Length)], decision.Reason);
        return (decision, fp);
    }

    public void BindAgent(string fingerprint, string pcId)
    {
        using var c = db.Open();
        c.Execute("UPDATE devices SET pc_id = @pcId, agent_enrolled_at = COALESCE(agent_enrolled_at, @now) WHERE fingerprint = @fp",
            new { pcId, fp = fingerprint, now = Time.Ms(time.GetUtcNow()) });
    }

    /// <summary>Called after a successful Console login from <paramref name="fingerprint"/> (work-PC + install-order tracking).</summary>
    public void RecordConsoleLogin(string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint)) return;
        var now = Time.Ms(time.GetUtcNow());
        using var c = db.Open();
        c.Execute("""
            INSERT INTO devices(fingerprint, status, first_seen, last_seen, console_login_at) VALUES(@fp, 'console', @now, @now, @now)
            ON CONFLICT(fingerprint) DO UPDATE SET console_login_at = COALESCE(console_login_at, @now), last_seen = @now
            """, new { fp = fingerprint, now });
        c.Execute("UPDATE pcs SET work_pc = 1 WHERE fingerprint = @fp", new { fp = fingerprint });
    }

    /// <summary>Re-check on every heartbeat; returns a revoke reason when the device may no longer act as a client.</summary>
    public string? CheckHeartbeat(string pcId, DeviceReport? report, string? ip)
    {
        using var c = db.Open();
        if (c.QuerySingleOrDefault<long>("SELECT revoked FROM pcs WHERE id = @pcId", new { pcId }) == 1)
            return "이 PC는 관리자에 의해 차단되었습니다.";
        if (report is null) return null; // older agent; enrollment already enforced
        var fp = DeviceGuard.Fingerprint(report);
        var bound = c.QuerySingleOrDefault<string>("SELECT fingerprint FROM pcs WHERE id = @pcId", new { pcId });
        if (bound != null && !string.Equals(bound, fp, StringComparison.OrdinalIgnoreCase))
        {
            c.Execute("UPDATE pcs SET revoked = 1 WHERE id = @pcId", new { pcId });
            audit.Write("device", "device.key_fingerprint_mismatch", pcId);
            return "에이전트 키가 다른 하드웨어에서 사용되어 차단되었습니다.";
        }
        var consoleFirst = c.QuerySingleOrDefault<long?>("SELECT 1 FROM devices WHERE fingerprint = @fp AND console_login_at IS NOT NULL AND (agent_enrolled_at IS NULL OR console_login_at < agent_enrolled_at)", new { fp }) == 1;
        var decision = guard.Decide(report, consoleFirst);
        if (!decision.Accepted && !decision.NeedsReview)
        {
            c.Execute("UPDATE pcs SET revoked = 1 WHERE id = @pcId", new { pcId });
            c.Execute("UPDATE devices SET status = 'revoked', verdict = @v, reason = @r, permanent = MAX(permanent,@p) WHERE fingerprint = @fp",
                new { fp, v = decision.Verdict.ToString(), r = decision.Reason, p = decision.PermanentlyBlocked ? 1 : 0 });
            audit.Write("device:" + report.ComputerName, "device.revoked", pcId, decision.Reason);
            return decision.Reason;
        }
        return null;
    }

    public bool IsWorkPc(string pcId)
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<long>("SELECT work_pc FROM pcs WHERE id = @pcId", new { pcId }) == 1;
    }

    // ---- console views ----
    public IReadOnlyList<BlockedDevice> Blocked()
    {
        using var c = db.Open();
        return c.Query<Row>("SELECT * FROM devices WHERE status IN ('blocked','revoked') ORDER BY last_seen DESC")
            .Select(r => new BlockedDevice(r.fingerprint, Time.At(r.last_seen), r.ip, r.name ?? "?", r.model, r.reason ?? "", r.permanent == 1)).ToList();
    }

    public IReadOnlyList<PendingDevice> Pending()
    {
        using var c = db.Open();
        return c.Query<Row>("SELECT * FROM devices WHERE status = 'pending' ORDER BY last_seen DESC")
            .Select(r => new PendingDevice(r.fingerprint, Time.At(r.last_seen), r.ip, r.name ?? "?", r.model, r.reason ?? "")).ToList();
    }

    public void Approve(Session actor, string fingerprint)
    {
        using var c = db.Open();
        var r = c.QuerySingleOrDefault<Row>("SELECT * FROM devices WHERE fingerprint = @fp", new { fp = fingerprint }) ?? throw new GacException("기기를 찾을 수 없습니다.", 404);
        if (r.status != "pending") throw new GacException("승인 대기 중인 기기가 아닙니다.");
        c.Execute("UPDATE devices SET status = 'accepted', reason = '관리자 승인' WHERE fingerprint = @fp", new { fp = fingerprint });
        audit.Write(actor.UserName, "device.approve", r.name, fingerprint[..Math.Min(12, fingerprint.Length)]);
    }

    public void Revoke(Session actor, string pcId)
    {
        using var c = db.Open();
        c.Execute("UPDATE pcs SET revoked = 1 WHERE id = @pcId", new { pcId });
        c.Execute("UPDATE devices SET status = 'revoked', reason = '관리자가 차단함' WHERE pc_id = @pcId", new { pcId });
        audit.Write(actor.UserName, "device.revoke", pcId);
    }

    /// <summary>Owner clears the console-first / tombstone record for a fingerprint so the machine may enroll as an agent.</summary>
    public void ClearConsoleFirst(Session actor, string fingerprint)
    {
        using var c = db.Open();
        c.Execute("UPDATE devices SET console_login_at = NULL WHERE fingerprint = @fp", new { fp = fingerprint });
        audit.Write(actor.UserName, "device.clear_console_first", fingerprint[..Math.Min(12, fingerprint.Length)]);
    }

    public void RemoveWorkPc(Session actor, string pcId)
    {
        using var c = db.Open();
        c.Execute("UPDATE pcs SET work_pc = 0 WHERE id = @pcId", new { pcId });
        audit.Write(actor.UserName, "device.remove_work_exemption", pcId);
    }
}
