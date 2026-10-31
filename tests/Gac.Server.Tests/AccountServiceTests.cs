using Gac.Server.Accounts;
using Gac.Server.Data;
using Gac.Shared.Auth;
using Gac.Shared.Contracts;

namespace Gac.Server.Tests;

internal sealed class FakeTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class AccountServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "gac-acc-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly FakeTime _time = new();
    private readonly AccountService _svc;
    private readonly Session _owner;

    public AccountServiceTests()
    {
        var db = new Db(_path);
        db.EnsureSchema();
        _svc = new AccountService(db, new AuditLog(db, _time), _time, iterations: 1000);
        var o = _svc.CreateOwner(new CreateOwnerRequest("owner", "교장", "Passw0rd!x", "code"));
        _owner = new Session(o.Id, o.UserName, o.DisplayName, o.Role, []);
    }

    [Fact]
    public void Production_hasher_uses_600k_iterations_and_salt()
    {
        Assert.True(PasswordHasher.Iterations >= 600_000);
        var a = PasswordHasher.Hash("Secret123", 1000);
        var b = PasswordHasher.Hash("Secret123", 1000);
        Assert.NotEqual(a.Salt, b.Salt);
        Assert.NotEqual(a.Hash, b.Hash);
        Assert.True(PasswordHasher.Verify("Secret123", a.Hash, a.Salt, a.Iterations));
        Assert.False(PasswordHasher.Verify("secret123", a.Hash, a.Salt, a.Iterations));
    }

    [Fact]
    public void Owner_can_only_be_created_once()
    {
        var e = Assert.Throws<GacException>(() => _svc.CreateOwner(new CreateOwnerRequest("x", "x", "Passw0rd!x", "code")));
        Assert.Equal(409, e.Status);
    }

    [Fact]
    public void Lockout_after_five_failures_for_five_minutes()
    {
        for (var i = 0; i < 5; i++) Assert.Throws<GacException>(() => _svc.Login(new LoginRequest("owner", "wrong")));
        var locked = Assert.Throws<GacException>(() => _svc.Login(new LoginRequest("owner", "Passw0rd!x")));
        Assert.Equal(423, locked.Status);
        _time.Now += TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1);
        Assert.NotNull(_svc.Login(new LoginRequest("owner", "Passw0rd!x")).Token);
    }

    [Fact]
    public void Session_expires_after_eight_idle_hours_and_slides_on_use()
    {
        var token = _svc.Login(new LoginRequest("owner", "Passw0rd!x")).Token;
        _time.Now += TimeSpan.FromHours(7);
        Assert.NotNull(_svc.Authenticate(token));
        _time.Now += TimeSpan.FromHours(7); // 7h since last use
        Assert.NotNull(_svc.Authenticate(token));
        _time.Now += TimeSpan.FromHours(8) + TimeSpan.FromMinutes(1);
        Assert.Null(_svc.Authenticate(token));
    }

    [Fact]
    public void Exactly_one_owner_is_enforced()
    {
        Assert.Throws<GacException>(() => _svc.Create(_owner, new SaveAccountRequest("o2", "o2", Role.Owner, false, [], "Passw0rd!x")));
        Assert.Throws<GacException>(() => _svc.Update(_owner, _owner.AccountId, new SaveAccountRequest("owner", "교장", Role.Admin, false, [], null)));
        Assert.Throws<GacException>(() => _svc.Update(_owner, _owner.AccountId, new SaveAccountRequest("owner", "교장", Role.Owner, true, [], null)));
        Assert.Throws<GacException>(() => _svc.Delete(_owner, _owner.AccountId));
    }

    [Fact]
    public void Disabled_account_cannot_login_and_loses_sessions()
    {
        var a = _svc.Create(_owner, new SaveAccountRequest("teacher1", "선생님", Role.Viewer, false, [], "Passw0rd!1"));
        var token = _svc.Login(new LoginRequest("teacher1", "Passw0rd!1")).Token;
        _svc.Update(_owner, a.Id, new SaveAccountRequest("teacher1", "선생님", Role.Viewer, true, [], null));
        Assert.Null(_svc.Authenticate(token));
        Assert.Equal(403, Assert.Throws<GacException>(() => _svc.Login(new LoginRequest("teacher1", "Passw0rd!1"))).Status);
    }

    [Fact]
    public void Weak_passwords_are_rejected()
    {
        Assert.Throws<GacException>(() => _svc.Create(_owner, new SaveAccountRequest("weak", "w", Role.Viewer, false, [], "short")));
        Assert.Throws<GacException>(() => _svc.Create(_owner, new SaveAccountRequest("weak", "w", Role.Viewer, false, [], "onlyletters")));
    }

    [Fact]
    public void Password_reset_clears_lock_and_sessions()
    {
        var a = _svc.Create(_owner, new SaveAccountRequest("teach2", "t2", Role.Viewer, false, [], "Passw0rd!1"));
        var token = _svc.Login(new LoginRequest("teach2", "Passw0rd!1")).Token;
        for (var i = 0; i < 5; i++) Assert.Throws<GacException>(() => _svc.Login(new LoginRequest("teach2", "bad")));
        _svc.ResetPassword(_owner, a.Id, "NewPassw0rd");
        Assert.Null(_svc.Authenticate(token));
        Assert.NotNull(_svc.Login(new LoginRequest("teach2", "NewPassw0rd")).Token);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) try { File.Delete(f); } catch (IOException) { }
    }
}
