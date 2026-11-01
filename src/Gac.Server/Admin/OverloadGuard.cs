using Dapper;
using Gac.Server.Data;
using Gac.Shared.Contracts;

namespace Gac.Server.Admin;

/// <summary>
/// Overload blocking (CHANGE-002 §4.3). In-memory counters, checked before any expensive work:
/// verified agent requests per agent per minute, failed authentications per source per 10 minutes, enrollment
/// attempts per source per hour. Exceeding a limit blocks the agent (bound to its hardware fingerprint) and the source
/// address for the block duration — doubling for repeat offenders within 24 hours, capped — and queues an alert for the
/// operator. Blocked requests get HTTP 429 with no body. Console requests are never blocked except a console login
/// from a source blocked for failed authentications.
/// </summary>
public sealed class OverloadGuard(Db db, SettingsStore settings, AuditLog audit, TimeProvider time, ILogger? log = null)
{
    public const string SettingsKey = "overload_settings";
    private DateTimeOffset _settingsReadAt;

    private sealed record Block(string Key, string? AgentId, string Ip, OverloadLimit Limit, DateTimeOffset Started, DateTimeOffset Until, int Offences, int Count);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, (long Minute, int Count)> _perAgent = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _authFails = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _enrolls = new();
    private readonly Dictionary<string, Block> _blocks = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _offences = new();
    private OverloadSettings? _settings;

    /// <summary>Re-read every 30 s so a change saved by GAC 서버 관리 (straight into the database) takes effect.</summary>
    public OverloadSettings Settings
    {
        get
        {
            lock (_lock)
            {
                var now = time.GetUtcNow();
                if (_settings == null || now - _settingsReadAt > TimeSpan.FromSeconds(30))
                {
                    _settings = settings.GetJson(SettingsKey, new OverloadSettings());
                    _settingsReadAt = now;
                }
                return _settings;
            }
        }
    }

    public void SaveSettings(string actor, OverloadSettings s)
    {
        if (OverloadSettings.Validate(s) is { } err) throw new Accounts.GacException(err);
        settings.SetJson(SettingsKey, s);
        lock (_lock) { _settings = s; _settingsReadAt = time.GetUtcNow(); }
        audit.Write(actor, "server.overload.settings", null,
            $"요청 {s.MaxRequestsPerAgentPerMinute}/분, 인증 실패 {s.MaxFailedAuthPerSourcePer10Min}/10분, 등록 {s.MaxEnrollPerSourcePerHour}/시간, 차단 {s.BlockMinutes}분(최대 {s.MaxBlockHours}시간)");
    }

    /// <summary>Agent/enrollment traffic: blocked when the agent id or the source address has an active block.</summary>
    public bool IsBlocked(string? agentId, string ip)
    {
        var now = time.GetUtcNow();
        lock (_lock) return _blocks.Values.Any(b => b.Until > now && (b.Ip == ip || (agentId != null && b.AgentId == agentId)));
    }

    /// <summary>Console login: only the failed-authentication block applies (CHANGE-002 §4.3).</summary>
    public bool IsLoginBlocked(string ip)
    {
        var now = time.GetUtcNow();
        lock (_lock) return _blocks.Values.Any(b => b.Until > now && b.Ip == ip && b.Limit == OverloadLimit.FailedAuth);
    }

    /// <summary>Counts one HMAC-verified agent request (counted only after verification, so a spoofed agent id cannot get another PC blocked).</summary>
    public void CountAgentRequest(string agentId, string ip)
    {
        var now = time.GetUtcNow();
        var minute = now.ToUnixTimeSeconds() / 60;
        int count;
        lock (_lock)
        {
            var cur = _perAgent.TryGetValue(agentId, out var x) && x.Minute == minute ? x : (minute, 0);
            count = cur.Item2 + 1;
            _perAgent[agentId] = (minute, count);
        }
        if (count == Settings.MaxRequestsPerAgentPerMinute + 1) Raise(agentId, agentId, ip, OverloadLimit.Requests, count);
    }

    public void CountAuthFailure(string ip) => CountWindow(_authFails, ip, TimeSpan.FromMinutes(10), Settings.MaxFailedAuthPerSourcePer10Min, OverloadLimit.FailedAuth);

    public void CountEnrollment(string ip) => CountWindow(_enrolls, ip, TimeSpan.FromHours(1), Settings.MaxEnrollPerSourcePerHour, OverloadLimit.Enrollment);

    private void CountWindow(Dictionary<string, Queue<DateTimeOffset>> map, string ip, TimeSpan window, int max, OverloadLimit limit)
    {
        var now = time.GetUtcNow();
        int count;
        lock (_lock)
        {
            if (!map.TryGetValue(ip, out var q)) map[ip] = q = new Queue<DateTimeOffset>();
            while (q.Count > 0 && now - q.Peek() > window) q.Dequeue();
            q.Enqueue(now);
            count = q.Count;
            if (map.Count > 10_000) map.Clear(); // memory bound under a flood of spoofed sources
        }
        if (count == max + 1) Raise("ip:" + ip, null, ip, limit, count);
    }

    private void Raise(string key, string? agentId, string ip, OverloadLimit limit, int count)
    {
        var now = time.GetUtcNow();
        Block block;
        lock (_lock)
        {
            if (!_offences.TryGetValue(key, out var list)) _offences[key] = list = [];
            list.RemoveAll(t => now - t > TimeSpan.FromHours(24));
            list.Add(now);
            block = new Block(key, agentId, ip, limit, now, now + Settings.BlockFor(list.Count), list.Count, count);
            _blocks[key] = block;
        }
        var (alias, name) = PcNames(agentId);
        using (var c = db.Open())
            c.Execute("""
                INSERT INTO alerts(id, pc_alias, windows_name, ip, limit_kind, request_count, started_at, blocked_until, read)
                VALUES(@id, @alias, @name, @ip, @limit, @count, @start, @until, 0)
                """, new { id = Guid.NewGuid().ToString("N"), alias, name, ip, limit = limit.ToString(), count, start = Time.Ms(now), until = Time.Ms(block.Until) });
        var who = $"{name ?? "-"} ({ip})";
        log?.LogWarning("Overload block: {Who} {Limit} count={Count} until={Until:HH:mm} offence={Offence}", who, limit, count, block.Until.ToLocalTime(), block.Offences);
        audit.Write("system", "overload.block", who, $"{OverloadText.Korean(limit)}: {count}회, {(block.Until - now).TotalMinutes:0}분 차단 ({block.Offences}회째)");
        audit.Write("system", "overload.alert", who, "운영자 PC로 알림 전송");
    }

    private (string? Alias, string? Name) PcNames(string? agentId)
    {
        if (agentId == null) return (null, null);
        using var c = db.Open();
        var row = c.QuerySingleOrDefault<(string? alias, string? name)>("SELECT alias, name FROM pcs WHERE id = @agentId", new { agentId });
        return (string.IsNullOrWhiteSpace(row.alias) ? null : row.alias, row.name);
    }

    public IReadOnlyList<OverloadBlock> Blocks()
    {
        var now = time.GetUtcNow();
        List<Block> active;
        lock (_lock) active = _blocks.Values.Where(b => b.Until > now).OrderByDescending(b => b.Started).ToList();
        return active.Select(b =>
        {
            var (alias, name) = PcNames(b.AgentId);
            string? fp = null;
            if (b.AgentId != null) using (var c = db.Open()) fp = c.QuerySingleOrDefault<string?>("SELECT fingerprint FROM pcs WHERE id = @id", new { id = b.AgentId });
            return new OverloadBlock(b.Key, alias, name, b.Ip, fp, b.Limit, b.Until, b.Offences);
        }).ToList();
    }

    public void Unblock(string actor, string key)
    {
        Block b;
        lock (_lock)
        {
            if (!_blocks.Remove(key, out b!)) throw new Accounts.GacException("차단 항목을 찾을 수 없습니다.", 404);
            if (b.AgentId != null) _perAgent.Remove(b.AgentId);
            _authFails.Remove(b.Ip);
            _enrolls.Remove(b.Ip);
        }
        audit.Write(actor, "overload.unblock", $"{PcNames(b.AgentId).Name ?? "-"} ({b.Ip})");
    }

    /// <summary>Removes the temporary block and returns the agent id so the caller can block the device permanently.</summary>
    public string TakeForPermanentBlock(string key)
    {
        lock (_lock)
        {
            if (!_blocks.TryGetValue(key, out var b)) throw new Accounts.GacException("차단 항목을 찾을 수 없습니다.", 404);
            if (b.AgentId == null) throw new Accounts.GacException("이 차단 항목에는 PC 정보가 없어 영구 차단할 수 없습니다 (주소만 차단됨).");
            _blocks.Remove(key);
            return b.AgentId;
        }
    }
}

/// <summary>Overload alerts queued for the operator (CHANGE-002 §4.3/§4.5); shown at the next Console login if none is open.</summary>
public sealed class AlertStore(Db db, TimeProvider time)
{
    private sealed class Row
    {
        public string id = ""; public string? pc_alias; public string? windows_name; public string ip = ""; public string limit_kind = "";
        public long request_count; public long started_at; public long blocked_until; public long read;
    }

    public IReadOnlyList<OverloadAlert> List(int limit = 500)
    {
        using var c = db.Open();
        c.Execute("DELETE FROM alerts WHERE started_at < @t", new { t = Time.Ms(time.GetUtcNow().AddDays(-90)) });
        return c.Query<Row>("SELECT * FROM alerts ORDER BY started_at DESC LIMIT @limit", new { limit })
            .Select(r => new OverloadAlert(r.id, r.pc_alias, r.windows_name, r.ip, Enum.TryParse<OverloadLimit>(r.limit_kind, out var l) ? l : OverloadLimit.Requests,
                (int)r.request_count, Time.At(r.started_at), Time.At(r.blocked_until), r.read == 1)).ToList();
    }

    public void MarkRead(IEnumerable<string> ids)
    {
        using var c = db.Open();
        foreach (var id in ids.Take(1000)) c.Execute("UPDATE alerts SET read = 1 WHERE id = @id", new { id });
    }
}
