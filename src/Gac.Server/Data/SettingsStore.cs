using Dapper;
using Gac.Shared;

namespace Gac.Server.Data;

public sealed class SettingsStore(Db db)
{
    public string? Get(string key)
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<string>("SELECT value FROM settings WHERE key = @key", new { key });
    }

    public void Set(string key, string value)
    {
        using var c = db.Open();
        c.Execute("INSERT INTO settings(key, value) VALUES(@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value", new { key, value });
    }

    public T GetJson<T>(string key, T fallback)
    {
        var s = Get(key);
        if (s == null) return fallback;
        try { return GacJson.Deserialize<T>(s) ?? fallback; } catch { return fallback; }
    }

    public void SetJson<T>(string key, T value) => Set(key, GacJson.Serialize(value));

    public static class Keys
    {
        public const string EnrollCode = "enroll_code";
        public const string ServerSettings = "server_settings";
        public const string SharedPresets = "shared_presets";
        public const string PolicyRevision = "policy_revision";
    }
}

public sealed class AuditLog(Db db, TimeProvider time)
{
    public void Write(string actor, string action, string? target = null, string? detail = null)
    {
        using var c = db.Open();
        c.Execute("INSERT INTO audit(at, actor, action, target, detail) VALUES(@at, @actor, @action, @target, @detail)",
            new { at = Time.Ms(time.GetUtcNow()), actor, action, target, detail });
    }

    public IReadOnlyList<Gac.Shared.Contracts.AuditEntry> Query(DateTimeOffset? from, DateTimeOffset? to, int limit)
    {
        using var c = db.Open();
        var rows = c.Query<(long id, long at, string actor, string action, string? target, string? detail)>(
            "SELECT id, at, actor, action, target, detail FROM audit WHERE at >= @f AND at <= @t ORDER BY id DESC LIMIT @limit",
            new { f = from.HasValue ? Time.Ms(from.Value) : 0, t = to.HasValue ? Time.Ms(to.Value) : long.MaxValue, limit });
        return rows.Select(r => new Gac.Shared.Contracts.AuditEntry(r.id, Time.At(r.at), r.actor, r.action, r.target, r.detail)).ToList();
    }
}
