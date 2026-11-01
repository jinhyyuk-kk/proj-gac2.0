using Gac.Server.Accounts;
using Gac.Server.Data;
using Gac.Shared.Auth;
using Gac.Shared.Contracts;

namespace Gac.Server.Tests;

/// <summary>CHANGE-001 section 4–5: sign-up/setup code, ownership transfer, editable permission matrix.</summary>
public sealed class AccountFlowTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "gac-acctflow-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly FakeTime _time = new();
    private readonly AccountService _svc;
    private readonly string _setup;

    public AccountFlowTests()
    {
        var db = new Db(_path);
        db.EnsureSchema();
        _svc = new AccountService(db, new AuditLog(db, _time), _time, iterations: 1000);
        _setup = _svc.EnsureSetupCode();
    }

    private Session Owner() { var o = _svc.Get(1)!; return new Session(o.Id, o.UserName, o.DisplayName, o.Role, [], _svc.GetMatrix().Effective(o.Role)); }

    [Fact]
    public void First_signup_becomes_owner_and_consumes_setup_code()
    {
        Assert.True(_svc.SignUpOpen());
        var owner = _svc.CreateOwner(new CreateOwnerRequest("owner", "교장", "Passw0rd!x", _setup));
        Assert.Equal(Role.Owner, owner.Role);
        Assert.False(_svc.SignUpOpen());
        Assert.Null(_svc.GetSetupCode()); // single-use: destroyed
    }

    [Fact]
    public void Signup_after_first_is_forbidden_even_with_valid_code()
    {
        _svc.CreateOwner(new CreateOwnerRequest("owner", "교장", "Passw0rd!x", _setup));
        var e = Assert.Throws<GacException>(() => _svc.CreateOwner(new CreateOwnerRequest("two", "x", "Passw0rd!x", _setup)));
        Assert.Equal(409, e.Status);
    }

    [Fact]
    public void Wrong_setup_code_is_rejected()
    {
        var e = Assert.Throws<GacException>(() => _svc.CreateOwner(new CreateOwnerRequest("owner", "교장", "Passw0rd!x", "WRNG-WRNG-WRNG")));
        Assert.Equal(403, e.Status);
    }

    [Fact]
    public void Concurrent_signups_yield_exactly_one_owner()
    {
        var ok = 0;
        Parallel.For(0, 8, i =>
        {
            try { _svc.CreateOwner(new CreateOwnerRequest("owner" + i, "x", "Passw0rd!x", _setup)); Interlocked.Increment(ref ok); }
            catch (GacException) { }
        });
        Assert.Equal(1, ok);
        Assert.Single(_svc.List().Where(a => a.Role == Role.Owner));
    }

    [Fact]
    public void Ownership_transfer_leaves_exactly_one_owner_and_demotes_old()
    {
        _svc.CreateOwner(new CreateOwnerRequest("owner", "교장", "Passw0rd!x", _setup));
        var admin = _svc.Create(Owner(), new SaveAccountRequest("vice", "교감", Role.Admin, false, [], "Passw0rd!1"));
        _svc.TransferOwnership(Owner(), admin.Id);
        var accounts = _svc.List();
        Assert.Single(accounts.Where(a => a.Role == Role.Owner));
        Assert.Equal("vice", accounts.Single(a => a.Role == Role.Owner).UserName);
        Assert.Equal(Role.Admin, accounts.Single(a => a.UserName == "owner").Role);
    }

    [Fact]
    public void Owner_cannot_transfer_to_self_or_demote_self()
    {
        _svc.CreateOwner(new CreateOwnerRequest("owner", "교장", "Passw0rd!x", _setup));
        Assert.Throws<GacException>(() => _svc.TransferOwnership(Owner(), 1));
        Assert.Throws<GacException>(() => _svc.Update(Owner(), 1, new SaveAccountRequest("owner", "교장", Role.Admin, false, [], null)));
        Assert.Throws<GacException>(() => _svc.Delete(Owner(), 1));
    }

    [Fact]
    public void Permission_matrix_edit_changes_effective_permissions_on_next_resolve()
    {
        _svc.CreateOwner(new CreateOwnerRequest("owner", "교장", "Passw0rd!x", _setup));
        Assert.False(_svc.GetMatrix().Effective(Role.Operator).Contains(Permission.RunDeployScripts));
        var m = _svc.GetMatrix();
        m.Set(Role.Operator, Permission.RunDeployScripts, true);
        _svc.SaveMatrix(Owner(), m);
        Assert.True(_svc.GetMatrix().Effective(Role.Operator).Contains(Permission.RunDeployScripts));
    }

    [Fact]
    public void Matrix_cannot_grant_owner_only_powers_to_other_roles()
    {
        _svc.CreateOwner(new CreateOwnerRequest("owner", "교장", "Passw0rd!x", _setup));
        var m = _svc.GetMatrix();
        m.Set(Role.Admin, Permission.ManageAccounts, true);
        Assert.False(m.Effective(Role.Admin).Contains(Permission.ManageAccounts));
    }

    [Fact]
    public void Unlock_requires_permission_and_locks_after_five_failures()
    {
        _svc.CreateOwner(new CreateOwnerRequest("owner", "교장", "Passw0rd!x", _setup));
        _svc.Create(Owner(), new SaveAccountRequest("viewer", "조회", Role.Viewer, false, [], "Passw0rd!1"));
        _svc.Create(Owner(), new SaveAccountRequest("oper", "운영", Role.Operator, false, [], "Passw0rd!1"));
        Assert.True(_svc.AuthorizeUnlock("oper", "Passw0rd!1").Ok);           // Operator has UnlockPc by default
        Assert.False(_svc.AuthorizeUnlock("viewer", "Passw0rd!1").Ok);      // Viewer does not
        for (var i = 0; i < 5; i++) Assert.False(_svc.AuthorizeUnlock("oper", "wrong").Ok);
        Assert.Contains("잠겼", _svc.AuthorizeUnlock("oper", "Passw0rd!1").Message);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) try { File.Delete(f); } catch (IOException) { }
    }
}
