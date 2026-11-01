using Dapper;
using Microsoft.Data.Sqlite;

namespace Gac.Server.Data;

/// <summary>SQLite connection factory and schema. One file, WAL mode, no external database.</summary>
public sealed class Db
{
    private readonly string _connectionString;

    public Db(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, Pooling = true }.ToString();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        c.Execute("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return c;
    }

    public void EnsureSchema()
    {
        using var c = Open();
        c.Execute("PRAGMA journal_mode = WAL;");
        c.Execute(Schema);
        // Idempotent migrations for CHANGE-001 (add columns if an older DB is being upgraded).
        AddColumn(c, "pcs", "alias", "TEXT NULL");
        AddColumn(c, "pcs", "fingerprint", "TEXT NULL");
        AddColumn(c, "pcs", "work_pc", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(c, "pcs", "revoked", "INTEGER NOT NULL DEFAULT 0");
    }

    private static void AddColumn(SqliteConnection c, string table, string column, string decl)
    {
        var exists = c.Query<string>($"SELECT name FROM pragma_table_info('{table}')").Any(n => n.Equals(column, StringComparison.OrdinalIgnoreCase));
        if (!exists) c.Execute($"ALTER TABLE {table} ADD COLUMN {column} {decl}");
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS pc_groups (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE COLLATE NOCASE);

        CREATE TABLE IF NOT EXISTS accounts (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            user_name TEXT NOT NULL UNIQUE COLLATE NOCASE,
            display_name TEXT NOT NULL,
            role TEXT NOT NULL,
            disabled INTEGER NOT NULL DEFAULT 0,
            password_hash TEXT NOT NULL,
            password_salt TEXT NOT NULL,
            iterations INTEGER NOT NULL,
            failed_count INTEGER NOT NULL DEFAULT 0,
            locked_until INTEGER NULL,
            last_login INTEGER NULL,
            created_at INTEGER NOT NULL);

        CREATE TABLE IF NOT EXISTS account_groups (
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            group_id INTEGER NOT NULL REFERENCES pc_groups(id) ON DELETE CASCADE,
            PRIMARY KEY (account_id, group_id));

        CREATE TABLE IF NOT EXISTS sessions (
            token_hash TEXT PRIMARY KEY,
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            created_at INTEGER NOT NULL,
            last_seen INTEGER NOT NULL);

        CREATE TABLE IF NOT EXISTS pcs (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            group_id INTEGER NULL REFERENCES pc_groups(id) ON DELETE SET NULL,
            agent_key TEXT NOT NULL,
            enrolled_at INTEGER NOT NULL,
            last_seen INTEGER NULL,
            ip TEXT NULL, os TEXT NULL, logged_on_user TEXT NULL, agent_version TEXT NULL, mac TEXT NULL);

        CREATE TABLE IF NOT EXISTS batches (
            id TEXT PRIMARY KEY, module TEXT NOT NULL, action TEXT NOT NULL, created_by TEXT NOT NULL, created_at INTEGER NOT NULL);

        CREATE TABLE IF NOT EXISTS commands (
            id TEXT PRIMARY KEY,
            batch_id TEXT NOT NULL REFERENCES batches(id) ON DELETE CASCADE,
            pc_id TEXT NOT NULL REFERENCES pcs(id) ON DELETE CASCADE,
            module TEXT NOT NULL, action TEXT NOT NULL, payload TEXT NOT NULL,
            status TEXT NOT NULL, created_at INTEGER NOT NULL, delivered_at INTEGER NULL, finished_at INTEGER NULL,
            summary TEXT NULL, data TEXT NULL);
        CREATE INDEX IF NOT EXISTS ix_commands_pc_status ON commands(pc_id, status);
        CREATE INDEX IF NOT EXISTS ix_commands_batch ON commands(batch_id);

        CREATE TABLE IF NOT EXISTS reports (
            id TEXT PRIMARY KEY,
            pc_id TEXT NOT NULL REFERENCES pcs(id) ON DELETE CASCADE,
            command_id TEXT NULL, module TEXT NOT NULL, action TEXT NOT NULL, success INTEGER NOT NULL,
            summary TEXT NOT NULL, data TEXT NULL, started_at INTEGER NOT NULL, finished_at INTEGER NOT NULL, trigger TEXT NOT NULL, received_at INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_reports_pc_module ON reports(pc_id, module, finished_at);
        CREATE INDEX IF NOT EXISTS ix_reports_finished ON reports(finished_at);

        CREATE TABLE IF NOT EXISTS pc_latest (
            pc_id TEXT NOT NULL REFERENCES pcs(id) ON DELETE CASCADE, module TEXT NOT NULL,
            data TEXT NULL, success INTEGER NOT NULL, summary TEXT NOT NULL, at INTEGER NOT NULL,
            PRIMARY KEY (pc_id, module));

        CREATE TABLE IF NOT EXISTS audit (
            id INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, actor TEXT NOT NULL, action TEXT NOT NULL, target TEXT NULL, detail TEXT NULL);
        CREATE INDEX IF NOT EXISTS ix_audit_at ON audit(at);

        CREATE TABLE IF NOT EXISTS group_policies (group_id INTEGER PRIMARY KEY REFERENCES pc_groups(id) ON DELETE CASCADE, json TEXT NOT NULL, updated_at INTEGER NOT NULL);

        CREATE TABLE IF NOT EXISTS packages (
            id TEXT PRIMARY KEY, name TEXT NOT NULL, file_name TEXT NOT NULL, sha256 TEXT NOT NULL, size INTEGER NOT NULL,
            silent_args TEXT NOT NULL, uploaded_at INTEGER NOT NULL, uploaded_by TEXT NOT NULL);

        -- CHANGE-001: every device the server has seen, by hardware fingerprint.
        CREATE TABLE IF NOT EXISTS devices (
            fingerprint TEXT PRIMARY KEY,
            pc_id TEXT NULL,
            status TEXT NOT NULL,            -- accepted | blocked | pending | revoked
            verdict TEXT NULL,
            reason TEXT NULL,
            permanent INTEGER NOT NULL DEFAULT 0,
            name TEXT NULL, model TEXT NULL, ip TEXT NULL,
            report TEXT NULL,
            first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL,
            console_login_at INTEGER NULL,   -- first successful Console login from this fingerprint
            agent_enrolled_at INTEGER NULL);

        -- CHANGE-001 2.3/2.4: Console-created period presets.
        CREATE TABLE IF NOT EXISTS periods (
            id TEXT PRIMARY KEY, name TEXT NOT NULL, preset TEXT NOT NULL, has_lock INTEGER NOT NULL,
            start_at INTEGER NOT NULL, end_at INTEGER NOT NULL, started_by TEXT NOT NULL, created_at INTEGER NOT NULL, stopped INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS period_targets (period_id TEXT NOT NULL REFERENCES periods(id) ON DELETE CASCADE, pc_id TEXT NOT NULL, PRIMARY KEY (period_id, pc_id));

        -- CHANGE-002 §2: lock exemption days (rules; expanded for agents on every poll).
        CREATE TABLE IF NOT EXISTS exemption_days (
            id TEXT PRIMARY KEY, name TEXT NOT NULL, recurrence TEXT NOT NULL, date TEXT NOT NULL,
            dom INTEGER NULL, nth INTEGER NULL, weekday INTEGER NULL, skipped TEXT NOT NULL DEFAULT '[]',
            created_by TEXT NOT NULL, created_at INTEGER NOT NULL);

        -- CHANGE-002 §4.3: overload alerts for the operator (queued until a Console reads them).
        CREATE TABLE IF NOT EXISTS alerts (
            id TEXT PRIMARY KEY, pc_alias TEXT NULL, windows_name TEXT NULL, ip TEXT NOT NULL, limit_kind TEXT NOT NULL,
            request_count INTEGER NOT NULL, started_at INTEGER NOT NULL, blocked_until INTEGER NOT NULL, read INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX IF NOT EXISTS ix_alerts_started ON alerts(started_at);
        """;
}

internal static class Time
{
    public static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();
    public static DateTimeOffset At(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
    public static DateTimeOffset? At(long? ms) => ms is null ? null : At(ms.Value);
}
