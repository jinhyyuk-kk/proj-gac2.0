using System.Security.Cryptography;
using System.Text;
using Dapper;
using Gac.Server.Data;
using Gac.Shared.Auth;
using Gac.Shared.Contracts;

namespace Gac.Server.Accounts;

public sealed record Session(int AccountId, string UserName, string DisplayName, Role Role, IReadOnlyList<int> GroupIds, IReadOnlySet<Permission>? Permissions = null)
{
    // Effective permissions resolved from the Owner-editable matrix at auth time; falls back to role defaults.
    private IReadOnlySet<Permission> Effective => Permissions ?? RolePermissions.Default(Role);
    public bool Has(Permission p) => Effective.Contains(p);
    public bool CanAccessGroup(int? groupId) => !RolePermissions.IsGroupScoped(Role) || (groupId.HasValue && GroupIds.Contains(groupId.Value));
}

/// <summary>Thrown for user-facing failures; message is Korean and returned to the console.</summary>
public sealed class GacException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed class AccountService(Db db, AuditLog audit, TimeProvider time, int iterations = PasswordHasher.Iterations)
{
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SessionIdle = TimeSpan.FromHours(8);

    private sealed class Row
    {
        public long id; public string user_name = ""; public string display_name = ""; public string role = "";
        public long disabled; public string password_hash = ""; public string password_salt = ""; public long iterations;
        public long failed_count; public long? locked_until; public long? last_login;
    }

    private sealed class SessionRow { public long account_id; public long last_seen; }

    public bool NeedsOwner()
    {
        using var c = db.Open();
        return c.ExecuteScalar<long>("SELECT COUNT(*) FROM accounts") == 0;
    }

    /// <summary>
    /// First-ever sign-up becomes the Owner (CHANGE-001 section 4). Requires the single-use setup code and runs in one
    /// transaction with the empty-table check, so two concurrent sign-ups cannot both succeed. The code is consumed.
    /// </summary>
    public AccountInfo CreateOwner(CreateOwnerRequest req)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM accounts", transaction: tx) != 0)
            throw new GacException("이미 계정이 있습니다. 회원가입은 처음 한 번만 할 수 있습니다.", 409);
        var setup = c.QuerySingleOrDefault<string>("SELECT value FROM settings WHERE key = 'setup_code'", transaction: tx);
        if (!string.IsNullOrEmpty(setup))
        {
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(NormalizeCode(req.EnrollCode)), Encoding.UTF8.GetBytes(NormalizeCode(setup))))
                throw new GacException("설치 코드가 올바르지 않습니다. 서버 설치 마지막 화면에 표시된 설치 코드를 입력하세요.", 403);
        }
        var id = Insert(c, tx, new SaveAccountRequest(req.UserName, req.DisplayName, Role.Owner, false, [], req.Password));
        c.Execute("DELETE FROM settings WHERE key = 'setup_code'", transaction: tx); // single-use: destroyed after first sign-up
        tx.Commit();
        audit.Write(req.UserName, "account.create_owner", req.UserName);
        return Get(id)!;
    }

    public bool SignUpOpen() => NeedsOwner();

    private static string NormalizeCode(string? s) => new((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>The setup code shown on the Admin PC until the first sign-up consumes it (null once used).</summary>
    public string? GetSetupCode()
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<string>("SELECT value FROM settings WHERE key = 'setup_code'");
    }

    /// <summary>Generates the single-use setup code if absent (called at install / first run while no account exists).</summary>
    public string EnsureSetupCode()
    {
        using var c = db.Open();
        var existing = c.QuerySingleOrDefault<string>("SELECT value FROM settings WHERE key = 'setup_code'");
        if (existing != null) return existing;
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM accounts") != 0) return "(이미 계정이 있어 설치 코드가 필요 없습니다)";
        var code = Gac.Shared.Security.EnrollmentProof.NewCode();
        c.Execute("INSERT INTO settings(key, value) VALUES('setup_code', @v) ON CONFLICT(key) DO NOTHING", new { v = code });
        return code;
    }

    /// <summary>Transfer ownership to another account; the old Owner becomes Admin. Always exactly one Owner.</summary>
    public void TransferOwnership(Session actor, int toAccountId)
    {
        if (actor.Role != Role.Owner) throw new GacException("최고 관리자만 소유권을 넘길 수 있습니다.", 403);
        if (toAccountId == actor.AccountId) throw new GacException("자기 자신에게 소유권을 넘길 수 없습니다.");
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var target = c.QuerySingleOrDefault<Row>("SELECT * FROM accounts WHERE id = @id", new { id = toAccountId }, tx) ?? throw new GacException("대상 계정을 찾을 수 없습니다.", 404);
        if (target.disabled != 0) throw new GacException("사용 중지된 계정에는 소유권을 넘길 수 없습니다.");
        c.Execute("UPDATE accounts SET role = @r WHERE id = @id", new { r = Role.Admin.ToString(), id = actor.AccountId }, tx);
        c.Execute("UPDATE accounts SET role = @r WHERE id = @id", new { r = Role.Owner.ToString(), id = toAccountId }, tx);
        tx.Commit();
        audit.Write(actor.UserName, "account.transfer_ownership", target.user_name);
    }

    public sealed record UnlockResult(bool Ok, string Message);

    /// <summary>Checks credentials for unlocking a locked PC (CHANGE-001 2.4). Requires the UnlockPc permission. Same lockout rule.</summary>
    public UnlockResult AuthorizeUnlock(string userName, string password)
    {
        var now = time.GetUtcNow();
        using var c = db.Open();
        var row = c.QuerySingleOrDefault<Row>("SELECT * FROM accounts WHERE user_name = @u", new { u = userName?.Trim() ?? "" });
        if (row == null)
        {
            PasswordHasher.Verify(password ?? "", Convert.ToBase64String(new byte[32]), Convert.ToBase64String(new byte[16]), iterations);
            return new UnlockResult(false, "아이디 또는 비밀번호가 올바르지 않습니다.");
        }
        if (row.locked_until is { } lu && lu > Time.Ms(now)) return new UnlockResult(false, "로그인 시도가 너무 많아 계정이 잠겼습니다. 잠시 후 다시 시도하세요.");
        if (row.disabled != 0) return new UnlockResult(false, "사용이 중지된 계정입니다.");
        if (!PasswordHasher.Verify(password ?? "", row.password_hash, row.password_salt, (int)row.iterations))
        {
            var failed = row.failed_count + 1;
            long? lockUntil = failed >= MaxFailedLogins ? Time.Ms(now + LockDuration) : null;
            c.Execute("UPDATE accounts SET failed_count = @f, locked_until = @l WHERE id = @id", new { f = lockUntil.HasValue ? 0 : failed, l = lockUntil, id = row.id });
            return new UnlockResult(false, lockUntil.HasValue ? "시도가 너무 많아 계정이 5분간 잠겼습니다." : "아이디 또는 비밀번호가 올바르지 않습니다.");
        }
        var role = Enum.Parse<Role>(row.role);
        if (!GetMatrix().Effective(role).Contains(Permission.UnlockPc)) return new UnlockResult(false, "이 계정은 PC 잠금을 해제할 권한이 없습니다.");
        c.Execute("UPDATE accounts SET failed_count = 0, locked_until = NULL WHERE id = @id", new { id = row.id });
        return new UnlockResult(true, "확인되었습니다.");
    }

    public void AuditUnlock(string userName, string periodId, string? ip) =>
        audit.Write(userName.Trim(), "pc.unlock", periodId, ip);

    public LoginResponse Login(LoginRequest req)
    {
        var now = time.GetUtcNow();
        using var c = db.Open();
        var row = c.QuerySingleOrDefault<Row>("SELECT * FROM accounts WHERE user_name = @u", new { u = req.UserName?.Trim() ?? "" });
        if (row == null)
        {
            // Spend comparable time so user names cannot be probed by timing.
            PasswordHasher.Verify(req.Password ?? "", Convert.ToBase64String(new byte[32]), Convert.ToBase64String(new byte[16]), iterations);
            audit.Write(req.UserName ?? "?", "login.failed", detail: "unknown user");
            throw new GacException("아이디 또는 비밀번호가 올바르지 않습니다.", 401);
        }
        if (row.locked_until is { } lu && lu > Time.Ms(now))
        {
            audit.Write(row.user_name, "login.locked");
            throw new GacException("로그인 시도가 너무 많아 계정이 5분간 잠겼습니다. 잠시 후 다시 시도하세요.", 423);
        }
        if (row.disabled != 0)
        {
            audit.Write(row.user_name, "login.disabled");
            throw new GacException("사용이 중지된 계정입니다. 최고 관리자에게 문의하세요.", 403);
        }
        if (!PasswordHasher.Verify(req.Password ?? "", row.password_hash, row.password_salt, (int)row.iterations))
        {
            var failed = row.failed_count + 1;
            long? lockUntil = failed >= MaxFailedLogins ? Time.Ms(now + LockDuration) : null;
            c.Execute("UPDATE accounts SET failed_count = @f, locked_until = @l WHERE id = @id",
                new { f = lockUntil.HasValue ? 0 : failed, l = lockUntil, id = row.id });
            audit.Write(row.user_name, lockUntil.HasValue ? "login.lockout" : "login.failed");
            throw new GacException(lockUntil.HasValue
                ? "로그인 시도가 너무 많아 계정이 5분간 잠겼습니다."
                : "아이디 또는 비밀번호가 올바르지 않습니다.", 401);
        }

        c.Execute("UPDATE accounts SET failed_count = 0, locked_until = NULL, last_login = @now WHERE id = @id", new { now = Time.Ms(now), id = row.id });
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        c.Execute("INSERT INTO sessions(token_hash, account_id, created_at, last_seen) VALUES(@h, @a, @n, @n)",
            new { h = HashToken(token), a = row.id, n = Time.Ms(now) });
        audit.Write(row.user_name, "login.success");
        return new LoginResponse(token, Get((int)row.id)!, now + SessionIdle);
    }

    public void Logout(string token)
    {
        using var c = db.Open();
        c.Execute("DELETE FROM sessions WHERE token_hash = @h", new { h = HashToken(token) });
    }

    /// <summary>Resolves a bearer token. Sliding 8-hour inactivity expiry.</summary>
    public Session? Authenticate(string token)
    {
        var now = time.GetUtcNow();
        using var c = db.Open();
        var hash = HashToken(token);
        var s = c.QuerySingleOrDefault<SessionRow>("SELECT account_id, last_seen FROM sessions WHERE token_hash = @hash", new { hash });
        if (s == null) return null;
        if (Time.Ms(now) - s.last_seen > (long)SessionIdle.TotalMilliseconds)
        {
            c.Execute("DELETE FROM sessions WHERE token_hash = @hash", new { hash });
            return null;
        }
        var acc = Get((int)s.account_id);
        if (acc == null || acc.Disabled) return null;
        c.Execute("UPDATE sessions SET last_seen = @n WHERE token_hash = @hash", new { n = Time.Ms(now), hash });
        // Effective permissions come from the current matrix, so Owner edits take effect on the next request.
        return new Session(acc.Id, acc.UserName, acc.DisplayName, acc.Role, acc.GroupIds, GetMatrix().Effective(acc.Role));
    }

    public PermissionMatrix GetMatrix()
    {
        using var c = db.Open();
        var json = c.QuerySingleOrDefault<string>("SELECT value FROM settings WHERE key = 'permission_matrix'");
        if (json == null) return PermissionMatrix.Default();
        try { return Gac.Shared.GacJson.Deserialize<PermissionMatrix>(json) ?? PermissionMatrix.Default(); }
        catch { return PermissionMatrix.Default(); }
    }

    public void SaveMatrix(Session actor, PermissionMatrix matrix)
    {
        using var c = db.Open();
        c.Execute("INSERT INTO settings(key, value) VALUES('permission_matrix', @v) ON CONFLICT(key) DO UPDATE SET value = @v",
            new { v = Gac.Shared.GacJson.Serialize(matrix) });
        audit.Write(actor.UserName, "accounts.permission_matrix", detail: Gac.Shared.GacJson.Serialize(matrix.Roles));
    }

    public IReadOnlyList<AccountInfo> List()
    {
        using var c = db.Open();
        var ids = c.Query<long>("SELECT id FROM accounts ORDER BY id").ToList();
        return ids.Select(i => Get((int)i)!).ToList();
    }

    public AccountInfo? Get(int id)
    {
        using var c = db.Open();
        var r = c.QuerySingleOrDefault<Row>("SELECT * FROM accounts WHERE id = @id", new { id });
        if (r == null) return null;
        var groups = c.Query<long>("SELECT group_id FROM account_groups WHERE account_id = @id", new { id }).Select(g => (int)g).ToList();
        var now = Time.Ms(time.GetUtcNow());
        return new AccountInfo((int)r.id, r.user_name, r.display_name, Enum.Parse<Role>(r.role), r.disabled != 0, groups,
            Time.At(r.last_login), r.locked_until is { } l && l > now ? Time.At(l) : null);
    }

    public AccountInfo Create(Session actor, SaveAccountRequest req)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        if (req.Role == Role.Owner) throw new GacException("최고 관리자는 한 명만 있을 수 있습니다.");
        var id = Insert(c, tx, req);
        tx.Commit();
        audit.Write(actor.UserName, "account.create", req.UserName, $"role={req.Role}");
        return Get(id)!;
    }

    public AccountInfo Update(Session actor, int id, SaveAccountRequest req)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var existing = c.QuerySingleOrDefault<Row>("SELECT * FROM accounts WHERE id = @id", new { id }, tx) ?? throw new GacException("계정을 찾을 수 없습니다.", 404);
        var oldRole = Enum.Parse<Role>(existing.role);
        if (oldRole == Role.Owner && (req.Role != Role.Owner || req.Disabled))
            throw new GacException("최고 관리자는 항상 한 명 있어야 합니다. 다른 계정에 최고 관리자를 넘길 수 없습니다.");
        if (oldRole != Role.Owner && req.Role == Role.Owner)
            throw new GacException("최고 관리자는 한 명만 있을 수 있습니다.");
        ValidateNames(req);
        c.Execute("UPDATE accounts SET user_name = @u, display_name = @d, role = @r, disabled = @dis WHERE id = @id",
            new { u = req.UserName.Trim(), d = req.DisplayName.Trim(), r = req.Role.ToString(), dis = req.Disabled ? 1 : 0, id }, tx);
        SetGroups(c, tx, id, req.GroupIds);
        if (!string.IsNullOrEmpty(req.Password)) SetPassword(c, tx, id, req.Password);
        if (req.Disabled) c.Execute("DELETE FROM sessions WHERE account_id = @id", new { id }, tx);
        tx.Commit();
        audit.Write(actor.UserName, "account.update", req.UserName, $"role={req.Role}; disabled={req.Disabled}");
        return Get(id)!;
    }

    public void Delete(Session actor, int id)
    {
        var acc = Get(id) ?? throw new GacException("계정을 찾을 수 없습니다.", 404);
        if (acc.Role == Role.Owner) throw new GacException("최고 관리자 계정은 삭제할 수 없습니다.");
        using var c = db.Open();
        c.Execute("DELETE FROM accounts WHERE id = @id", new { id });
        audit.Write(actor.UserName, "account.delete", acc.UserName);
    }

    public void ResetPassword(Session actor, int id, string newPassword)
    {
        var acc = Get(id) ?? throw new GacException("계정을 찾을 수 없습니다.", 404);
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        SetPassword(c, tx, id, newPassword);
        c.Execute("UPDATE accounts SET failed_count = 0, locked_until = NULL WHERE id = @id", new { id }, tx);
        c.Execute("DELETE FROM sessions WHERE account_id = @id", new { id }, tx);
        tx.Commit();
        audit.Write(actor.UserName, "account.reset_password", acc.UserName);
    }

    public void ChangeOwnPassword(Session actor, ChangePasswordRequest req)
    {
        using var c = db.Open();
        var row = c.QuerySingle<Row>("SELECT * FROM accounts WHERE id = @id", new { id = actor.AccountId });
        if (!PasswordHasher.Verify(req.CurrentPassword, row.password_hash, row.password_salt, (int)row.iterations))
            throw new GacException("현재 비밀번호가 올바르지 않습니다.");
        using var tx = c.BeginTransaction();
        SetPassword(c, tx, actor.AccountId, req.NewPassword);
        tx.Commit();
        audit.Write(actor.UserName, "account.change_password", actor.UserName);
    }

    private int Insert(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, SaveAccountRequest req)
    {
        ValidateNames(req);
        if (PasswordHasher.CheckStrength(req.Password) is { } err) throw new GacException(err);
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM accounts WHERE user_name = @u", new { u = req.UserName.Trim() }, tx) > 0)
            throw new GacException("이미 사용 중인 아이디입니다.", 409);
        var (hash, salt, iter) = PasswordHasher.Hash(req.Password!, iterations);
        var id = (int)c.ExecuteScalar<long>("""
            INSERT INTO accounts(user_name, display_name, role, disabled, password_hash, password_salt, iterations, created_at)
            VALUES(@u, @d, @r, @dis, @hash, @salt, @iter, @now); SELECT last_insert_rowid();
            """, new { u = req.UserName.Trim(), d = req.DisplayName.Trim(), r = req.Role.ToString(), dis = req.Disabled ? 1 : 0, hash, salt, iter, now = Time.Ms(time.GetUtcNow()) }, tx);
        SetGroups(c, tx, id, req.GroupIds);
        return id;
    }

    private void SetPassword(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, int id, string password)
    {
        if (PasswordHasher.CheckStrength(password) is { } err) throw new GacException(err);
        var (hash, salt, iter) = PasswordHasher.Hash(password, iterations);
        c.Execute("UPDATE accounts SET password_hash = @hash, password_salt = @salt, iterations = @iter WHERE id = @id", new { hash, salt, iter, id }, tx);
    }

    private static void SetGroups(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, int id, IReadOnlyList<int>? groups)
    {
        c.Execute("DELETE FROM account_groups WHERE account_id = @id", new { id }, tx);
        foreach (var g in (groups ?? []).Distinct())
            c.Execute("INSERT INTO account_groups(account_id, group_id) SELECT @id, id FROM pc_groups WHERE id = @g", new { id, g }, tx);
    }

    private static void ValidateNames(SaveAccountRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.UserName) || req.UserName.Trim().Length is < 3 or > 32 || !req.UserName.Trim().All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-'))
            throw new GacException("아이디는 3~32자의 영문, 숫자, '.', '_', '-'만 사용할 수 있습니다.");
        if (string.IsNullOrWhiteSpace(req.DisplayName) || req.DisplayName.Length > 40)
            throw new GacException("이름을 1~40자로 입력하세요.");
        if (!Enum.IsDefined(req.Role)) throw new GacException("역할 값이 올바르지 않습니다.");
    }

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
