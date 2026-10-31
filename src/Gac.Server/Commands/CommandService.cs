using System.Text.Json;
using Dapper;
using Gac.Server.Accounts;
using Gac.Server.Data;
using Gac.Shared;
using Gac.Shared.Auth;
using Gac.Shared.Contracts;
using Gac.Shared.Security;

namespace Gac.Server.Commands;

public sealed class CommandService(Db db, AuditLog audit, TimeProvider time)
{
    /// <summary>Commands not picked up within this time are marked expired.</summary>
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromDays(3);
    public const int MaxPayloadBytes = 64 * 1024;
    public const int MaxDataBytes = 512 * 1024;

    private sealed class CmdRow
    {
        public string id = ""; public string batch_id = ""; public string pc_id = ""; public string? pc_name; public string module = ""; public string action = "";
        public string payload = ""; public string status = ""; public long created_at; public long? finished_at; public string? summary; public string? data;
    }

    public SendCommandResponse Create(Session actor, SendCommandRequest req, Func<string, int?> groupOf)
    {
        if (!ModuleActions.IsKnown(req.Module, req.Action)) throw new GacException("알 수 없는 모듈 명령입니다.");
        var needed = RolePermissions.RequiredFor(req.Module, req.Action);
        if (!actor.Has(needed)) throw new GacException("이 작업을 할 권한이 없습니다.", 403);
        if (req.PcIds.Count == 0) throw new GacException("대상 PC를 선택하세요.");
        if (System.Text.Encoding.UTF8.GetByteCount(req.PayloadJson ?? "") > MaxPayloadBytes) throw new GacException("명령 내용이 너무 큽니다.");
        var payload = string.IsNullOrWhiteSpace(req.PayloadJson) ? "{}" : req.PayloadJson;
        try { using var _ = JsonDocument.Parse(payload); } catch (JsonException) { throw new GacException("명령 내용 형식이 올바르지 않습니다."); }

        var pcs = req.PcIds.Distinct().ToList();
        foreach (var pc in pcs)
        {
            var g = groupOf(pc);
            if (g == null && !PcExists(pc)) throw new GacException("PC를 찾을 수 없습니다.", 404);
            if (!actor.CanAccessGroup(g)) throw new GacException("담당 그룹이 아닌 PC에는 명령을 보낼 수 없습니다.", 403);
        }

        var batchId = Guid.NewGuid();
        var now = Time.Ms(time.GetUtcNow());
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("INSERT INTO batches(id, module, action, created_by, created_at) VALUES(@id, @m, @a, @by, @now)",
            new { id = batchId.ToString(), m = req.Module, a = req.Action, by = actor.UserName, now }, tx);
        foreach (var pc in pcs)
            c.Execute("INSERT INTO commands(id, batch_id, pc_id, module, action, payload, status, created_at) VALUES(@id, @b, @pc, @m, @a, @p, 'Pending', @now)",
                new { id = Guid.NewGuid().ToString(), b = batchId.ToString(), pc, m = req.Module, a = req.Action, p = payload, now }, tx);
        tx.Commit();

        var names = c.Query<string>("SELECT name FROM pcs WHERE id IN @pcs", new { pcs });
        audit.Write(actor.UserName, $"command.{req.Module}.{req.Action}", string.Join(", ", names), Truncate(payload, 500));
        return new SendCommandResponse(batchId, pcs.Count);
    }

    private bool PcExists(string pcId)
    {
        using var c = db.Open();
        return c.ExecuteScalar<long>("SELECT COUNT(*) FROM pcs WHERE id = @pcId", new { pcId }) > 0;
    }

    /// <summary>Returns pending commands for an agent, signed now (so the 5-minute expiry starts at delivery).</summary>
    public IReadOnlyList<AgentCommand> Deliver(string agentId, byte[] key)
    {
        var now = time.GetUtcNow();
        using var c = db.Open();
        c.Execute("UPDATE commands SET status = 'Expired', finished_at = @n, summary = '시간 안에 PC가 응답하지 않아 만료되었습니다.' WHERE pc_id = @agentId AND status = 'Pending' AND created_at < @cut",
            new { n = Time.Ms(now), agentId, cut = Time.Ms(now - PendingLifetime) });
        var rows = c.Query<CmdRow>("SELECT * FROM commands WHERE pc_id = @agentId AND status = 'Pending' ORDER BY created_at LIMIT 20", new { agentId }).ToList();
        var result = new List<AgentCommand>();
        foreach (var r in rows)
        {
            c.Execute("UPDATE commands SET status = 'Delivered', delivered_at = @n WHERE id = @id", new { n = Time.Ms(now), id = r.id });
            result.Add(CommandSigner.Sign(new AgentCommand(Guid.Parse(r.id), agentId, r.module, r.action, r.payload, now), key));
        }
        return result;
    }

    public void StoreReports(string agentId, IReadOnlyList<ModuleReport> reports)
    {
        var now = Time.Ms(time.GetUtcNow());
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        foreach (var r in reports.Take(200))
        {
            var data = r.DataJson != null && r.DataJson.Length > MaxDataBytes ? null : r.DataJson;
            var summary = Truncate(r.Summary ?? "", 1000);
            var inserted = c.Execute("""
                INSERT OR IGNORE INTO reports(id, pc_id, command_id, module, action, success, summary, data, started_at, finished_at, trigger, received_at)
                VALUES(@id, @pc, @cmd, @m, @a, @s, @sum, @data, @st, @fin, @tr, @now)
                """, new { id = r.Id.ToString(), pc = agentId, cmd = r.CommandId?.ToString(), m = r.Module, a = r.Action, s = r.Success ? 1 : 0, sum = summary, data,
                    st = Time.Ms(r.StartedAt), fin = Time.Ms(r.FinishedAt), tr = r.Trigger.ToString(), now }, tx);
            if (inserted == 0) continue; // duplicate upload after a retry

            if (r.CommandId is { } cid)
                c.Execute("UPDATE commands SET status = @st, finished_at = @fin, summary = @sum, data = @data WHERE id = @id AND pc_id = @pc",
                    new { st = r.Success ? "Succeeded" : "Failed", fin = Time.Ms(r.FinishedAt), sum = summary, data, id = cid.ToString(), pc = agentId }, tx);

            // Keep the latest state per module for the PC list. Failed scans do not overwrite good data.
            if (r.Success || r.Module is not (ModuleIds.Health or ModuleIds.Update))
                c.Execute("""
                    INSERT INTO pc_latest(pc_id, module, data, success, summary, at) VALUES(@pc, @m, @data, @s, @sum, @fin)
                    ON CONFLICT(pc_id, module) DO UPDATE SET data = excluded.data, success = excluded.success, summary = excluded.summary, at = excluded.at
                    WHERE excluded.at >= pc_latest.at
                    """, new { pc = agentId, m = r.Module, data, s = r.Success ? 1 : 0, sum = summary, fin = Time.Ms(r.FinishedAt) }, tx);

            if (r.Module is ModuleIds.Clean or ModuleIds.Shutdown or ModuleIds.Deploy || (r.Module == ModuleIds.Inspect && r.Action == ModuleActions.InspectUninstall))
                c.Execute("INSERT INTO audit(at, actor, action, target, detail) SELECT @now, 'agent:' || name, @act, name, @sum FROM pcs WHERE id = @pc",
                    new { now, act = $"executed.{r.Module}.{r.Action}", sum = summary, pc = agentId }, tx);
        }
        tx.Commit();
    }

    public void StoreRejections(string agentId, IReadOnlyList<CommandRejection> rejections)
    {
        using var c = db.Open();
        foreach (var r in rejections.Take(100))
            c.Execute("UPDATE commands SET status = 'Rejected', finished_at = @n, summary = @s WHERE id = @id AND pc_id = @pc",
                new { n = Time.Ms(time.GetUtcNow()), s = "PC가 명령을 거부했습니다: " + Truncate(r.Reason, 200), id = r.CommandId.ToString(), pc = agentId });
    }

    public IReadOnlyList<CommandBatchSummary> Batches(Session s, int limit)
    {
        using var c = db.Open();
        var rows = c.Query<(string id, string module, string action, string created_by, long created_at, long total, long done, long failed)>("""
            SELECT b.id, b.module, b.action, b.created_by, b.created_at,
                   COUNT(c.id),
                   SUM(CASE WHEN c.status IN ('Succeeded','Failed','Expired','Rejected') THEN 1 ELSE 0 END),
                   SUM(CASE WHEN c.status IN ('Failed','Expired','Rejected') THEN 1 ELSE 0 END)
            FROM batches b JOIN commands c ON c.batch_id = b.id
            GROUP BY b.id ORDER BY b.created_at DESC LIMIT @limit
            """, new { limit });
        return rows.Select(r => new CommandBatchSummary(Guid.Parse(r.id), r.module, r.action, r.created_by, Time.At(r.created_at), (int)r.total, (int)r.done, (int)r.failed)).ToList();
    }

    public IReadOnlyList<CommandRow> BatchCommands(Session s, Guid batchId)
    {
        using var c = db.Open();
        return c.Query<CmdRow>("SELECT c.*, p.name AS pc_name, p.group_id AS gid FROM commands c JOIN pcs p ON p.id = c.pc_id WHERE c.batch_id = @b ORDER BY p.name",
                new { b = batchId.ToString() })
            .Select(ToRow).ToList();
    }

    public IReadOnlyList<ReportRow> Reports(Session s, string? module, string? pcId, DateTimeOffset? since, int limit, Func<string, int?> groupOf)
    {
        using var c = db.Open();
        var rows = c.Query<(string id, string pc_id, string pc_name, string module, string action, long success, string summary, string? data, long finished_at, string trigger)>("""
            SELECT r.id, r.pc_id, p.name, r.module, r.action, r.success, r.summary, r.data, r.finished_at, r.trigger
            FROM reports r JOIN pcs p ON p.id = r.pc_id
            WHERE (@module IS NULL OR r.module = @module) AND (@pcId IS NULL OR r.pc_id = @pcId) AND r.finished_at >= @since
            ORDER BY r.finished_at DESC LIMIT @limit
            """, new { module, pcId, since = since.HasValue ? Time.Ms(since.Value) : 0, limit });
        return rows.Where(r => s.CanAccessGroup(groupOf(r.pc_id)))
            .Select(r => new ReportRow(Guid.Parse(r.id), r.pc_id, r.pc_name, r.module, r.action, r.success != 0, r.summary, r.data, Time.At(r.finished_at), Enum.Parse<ReportTrigger>(r.trigger)))
            .ToList();
    }

    /// <summary>Latest report per PC for one module (used by the console report tabs and inspection export).</summary>
    public IReadOnlyList<ReportRow> Latest(Session s, string module, Func<string, int?> groupOf)
    {
        using var c = db.Open();
        var rows = c.Query<(string pc_id, string pc_name, long success, string summary, string? data, long at)>(
            "SELECT l.pc_id, p.name, l.success, l.summary, l.data, l.at FROM pc_latest l JOIN pcs p ON p.id = l.pc_id WHERE l.module = @module ORDER BY p.name", new { module });
        return rows.Where(r => s.CanAccessGroup(groupOf(r.pc_id)))
            .Select(r => new ReportRow(Guid.Empty, r.pc_id, r.pc_name, module, "", r.success != 0, r.summary, r.data, Time.At(r.at), ReportTrigger.Schedule)).ToList();
    }

    private static CommandRow ToRow(CmdRow r) => new(Guid.Parse(r.id), Guid.Parse(r.batch_id), r.pc_id, r.pc_name ?? r.pc_id, r.module, r.action,
        Enum.Parse<CommandStatus>(r.status), Time.At(r.created_at), Time.At(r.finished_at), r.summary, r.data);

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
