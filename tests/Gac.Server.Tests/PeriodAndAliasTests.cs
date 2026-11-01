using Gac.Server.Accounts;
using Gac.Server.Data;
using Gac.Server.Hosting;
using Gac.Server.Periods;
using Gac.Server.Pcs;
using Gac.Shared;
using Gac.Shared.Auth;
using Gac.Shared.ClassMode;
using Gac.Shared.Contracts;
using Gac.Shared.Security;

namespace Gac.Server.Tests;

public sealed class PeriodAndAliasTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gac-pa-" + Guid.NewGuid().ToString("N"));
    private readonly Db _db;
    private readonly FakeTime _time = new();
    private readonly PcService _pcs;
    private readonly PeriodService _periods;
    private readonly ServerIdentity _identity;
    private readonly Session _owner = new(1, "owner", "교장", Role.Owner, [], RolePermissions.Default(Role.Owner));

    public PeriodAndAliasTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new Db(Path.Combine(_dir, "gac.db"));
        _db.EnsureSchema();
        var audit = new AuditLog(_db, _time);
        _pcs = new PcService(_db, new SettingsStore(_db), audit, _time);
        _identity = ServerIdentity.LoadOrCreate(_dir);
        _periods = new PeriodService(_db, audit, _identity, _time);
    }

    private string EnrollPc(string name = "LAB-01")
    {
        var (id, _) = _pcs.Enroll(new EnrollRequest("c", name, "Win", "AABBCCDDEEFF", "2.0.0"), "FP" + name);
        return id;
    }

    [Fact]
    public void Alias_changes_display_but_never_the_windows_name()
    {
        var id = EnrollPc("LAB-01");
        _pcs.SetAlias(_owner, id, "1학년 1반 교사 PC");
        var pc = _pcs.List(_owner).Single(p => p.Id == id);
        Assert.Equal("1학년 1반 교사 PC", pc.Name);   // shown large
        Assert.Equal("LAB-01", pc.WindowsName);        // real Windows name unchanged
        Assert.Equal("1학년 1반 교사 PC", pc.Alias);

        _pcs.SetAlias(_owner, id, "   ");               // clearing falls back to the Windows name
        pc = _pcs.List(_owner).Single(p => p.Id == id);
        Assert.Equal("LAB-01", pc.Name);
        Assert.Null(pc.Alias);
    }

    private static string LockPresetJson() => GacJson.Serialize(new ClassPreset
    {
        Name = "기말고사", Trigger = PresetTrigger.Login,
        Actions = [new() { Type = PresetActionTypes.Mute }, new() { Type = PresetActionTypes.LockPc }],
    });

    [Fact]
    public void Period_with_lock_delivers_to_normal_pc_and_strips_lock_for_work_pc()
    {
        var id = EnrollPc("LAB-02");
        _periods.Create(_owner, new CreatePeriodRequest("기말고사 기간", LockPresetJson(), _time.GetUtcNow(), 7, [id], []),
            _ => [], _ => true);

        var normal = _periods.ActiveForPc(id, workPc: false);
        var p = Assert.Single(normal);
        Assert.Contains(PresetActionTypes.LockPc, GacJson.Deserialize<ClassPreset>(p.PresetJson)!.Actions.Select(a => a.Type));
        Assert.True(PresetSigner.Verify(new SignedPresetBundle(p.PresetJson, p.StartAt, p.Signature), _identity.PresetPublicKeyPem));

        var work = _periods.ActiveForPc(id, workPc: true);
        var wp = Assert.Single(work);
        Assert.DoesNotContain(PresetActionTypes.LockPc, GacJson.Deserialize<ClassPreset>(wp.PresetJson)!.Actions.Select(a => a.Type));
        Assert.True(PresetSigner.Verify(new SignedPresetBundle(wp.PresetJson, wp.StartAt, wp.Signature), _identity.PresetPublicKeyPem));
    }

    [Fact]
    public void Period_is_inactive_before_start_and_after_stop()
    {
        var id = EnrollPc("LAB-03");
        var info = _periods.Create(_owner, new CreatePeriodRequest("방학 기간", LockPresetJson(), _time.GetUtcNow().AddDays(1), 3, [id], []),
            _ => [], _ => true);
        Assert.Empty(_periods.ActiveForPc(id, false)); // starts tomorrow
        _time.Now = _time.Now.AddDays(2);              // now within the window
        Assert.Single(_periods.ActiveForPc(id, false));
        _periods.Stop(_owner, info.Id);
        Assert.Empty(_periods.ActiveForPc(id, false));
    }

    [Fact]
    public void Operator_cannot_target_a_pc_outside_their_groups()
    {
        var id = EnrollPc("LAB-04");
        var op = new Session(2, "op", "운영", Role.Operator, [], RolePermissions.Default(Role.Operator));
        Assert.Throws<GacException>(() =>
            _periods.Create(op, new CreatePeriodRequest("x", LockPresetJson(), _time.GetUtcNow(), 1, [id], []), _ => [], _ => false));
    }

    public void Dispose()
    {
        _identity.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
