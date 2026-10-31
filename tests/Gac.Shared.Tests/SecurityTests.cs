using System.Security.Cryptography;
using Gac.Shared;
using Gac.Shared.Auth;
using Gac.Shared.Contracts;
using Gac.Shared.Security;

namespace Gac.Shared.Tests;

public class CommandSignerTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    private static AgentCommand Cmd(DateTimeOffset? issued = null, string agent = "pc1") =>
        CommandSigner.Sign(new AgentCommand(Guid.NewGuid(), agent, ModuleIds.Errors, ModuleActions.ErrorsCollect, "{}", issued ?? Now), Key);

    [Fact]
    public void Valid_command_is_accepted_once()
    {
        var replay = new ReplayGuard();
        var c = Cmd();
        Assert.Equal(CommandVerifyResult.Ok, CommandSigner.Verify(c, Key, "pc1", Now, replay));
        Assert.Equal(CommandVerifyResult.Replayed, CommandSigner.Verify(c, Key, "pc1", Now, replay));
    }

    [Fact]
    public void Tampered_payload_is_rejected()
    {
        var c = Cmd() with { PayloadJson = "{\"x\":1}" };
        Assert.Equal(CommandVerifyResult.BadSignature, CommandSigner.Verify(c, Key, "pc1", Now, new ReplayGuard()));
    }

    [Fact]
    public void Unsigned_command_is_rejected()
    {
        var c = new AgentCommand(Guid.NewGuid(), "pc1", ModuleIds.Errors, ModuleActions.ErrorsCollect, "{}", Now);
        Assert.Equal(CommandVerifyResult.BadSignature, CommandSigner.Verify(c, Key, "pc1", Now, new ReplayGuard()));
    }

    [Fact]
    public void Wrong_key_is_rejected()
    {
        Assert.Equal(CommandVerifyResult.BadSignature, CommandSigner.Verify(Cmd(), RandomNumberGenerator.GetBytes(32), "pc1", Now, new ReplayGuard()));
    }

    [Fact]
    public void Command_older_than_five_minutes_is_rejected()
    {
        var c = Cmd(Now.AddMinutes(-5).AddSeconds(-1));
        Assert.Equal(CommandVerifyResult.Expired, CommandSigner.Verify(c, Key, "pc1", Now, new ReplayGuard()));
        Assert.Equal(CommandVerifyResult.Ok, CommandSigner.Verify(Cmd(Now.AddMinutes(-4)), Key, "pc1", Now, new ReplayGuard()));
    }

    [Fact]
    public void Command_for_other_agent_is_rejected()
    {
        Assert.Equal(CommandVerifyResult.WrongAgent, CommandSigner.Verify(Cmd(agent: "pc2"), Key, "pc1", Now, new ReplayGuard()));
    }

    [Fact]
    public void Unknown_module_action_is_rejected()
    {
        var c = CommandSigner.Sign(new AgentCommand(Guid.NewGuid(), "pc1", "evil", "run", "{}", Now), Key);
        Assert.Equal(CommandVerifyResult.UnknownAction, CommandSigner.Verify(c, Key, "pc1", Now, new ReplayGuard()));
    }

    [Fact]
    public void Replay_guard_survives_restart_from_snapshot()
    {
        var g = new ReplayGuard();
        var id = Guid.NewGuid();
        g.TryRegister(id);
        var restored = new ReplayGuard(2000, g.Snapshot());
        Assert.False(restored.TryRegister(id));
    }
}

public class RequestSignerTests
{
    [Fact]
    public void Signature_round_trips_and_detects_body_change()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var now = DateTimeOffset.UtcNow;
        var ms = now.ToUnixTimeMilliseconds();
        var sig = RequestSigner.Sign(key, "a1", ms, "POST", "/api/agent/poll", "{}"u8);
        Assert.True(RequestSigner.Verify(key, "a1", ms.ToString(), sig, "POST", "/api/agent/poll", "{}"u8, now));
        Assert.False(RequestSigner.Verify(key, "a1", ms.ToString(), sig, "POST", "/api/agent/poll", "{ }"u8, now));
        Assert.False(RequestSigner.Verify(key, "a1", ms.ToString(), sig, "POST", "/api/agent/poll", "{}"u8, now.AddMinutes(6)));
    }
}

public class EnrollmentProofTests
{
    [Fact]
    public void Proof_binds_code_thumbprint_and_nonce()
    {
        var code = EnrollmentProof.NewCode();
        var proof = EnrollmentProof.Compute(code, "AABB", "n1");
        Assert.True(EnrollmentProof.Verify(code.ToLowerInvariant().Replace("-", " "), "aabb", "n1", proof));
        Assert.False(EnrollmentProof.Verify(code, "AABC", "n1", proof)); // MITM certificate
        Assert.False(EnrollmentProof.Verify(code, "AABB", "n2", proof)); // replayed proof
        Assert.False(EnrollmentProof.Verify("WRNG-CODE-XXXX", "AABB", "n1", proof));
    }

    [Fact]
    public void Codes_use_unambiguous_alphabet()
    {
        for (var i = 0; i < 50; i++)
            Assert.Matches("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", EnrollmentProof.NewCode());
    }

    [Fact]
    public void Reenroll_proof_requires_old_key()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var proof = EnrollmentProof.ReenrollProof(key, "id1", "PC-01");
        Assert.True(EnrollmentProof.VerifyReenroll(key, "id1", "pc-01", proof));
        Assert.False(EnrollmentProof.VerifyReenroll(RandomNumberGenerator.GetBytes(32), "id1", "PC-01", proof));
        Assert.False(EnrollmentProof.VerifyReenroll(key, "id2", "PC-01", proof));
    }
}

public class PresetSignerTests
{
    [Fact]
    public void Signed_bundle_verifies_and_tampering_fails()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = k.ExportSubjectPublicKeyInfoPem();
        var b = PresetSigner.Sign("{\"presets\":[]}", DateTimeOffset.UtcNow, k);
        Assert.True(PresetSigner.Verify(b, pub));
        Assert.False(PresetSigner.Verify(b with { PresetsJson = "{\"presets\":[1]}" }, pub));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(PresetSigner.Verify(b, other.ExportSubjectPublicKeyInfoPem()));
        Assert.False(PresetSigner.Verify(b, "not a key"));
    }
}

public class RoleTests
{
    [Fact]
    public void Matrix_matches_spec()
    {
        Assert.All(Enum.GetValues<Permission>(), p => Assert.True(RolePermissions.Has(Role.Owner, p)));
        Assert.False(RolePermissions.Has(Role.Admin, Permission.ManageAccounts));
        Assert.False(RolePermissions.Has(Role.Admin, Permission.ManageServerSettings));
        Assert.True(RolePermissions.Has(Role.Admin, Permission.RunDeployScripts));
        Assert.True(RolePermissions.Has(Role.Operator, Permission.RunModules));
        Assert.False(RolePermissions.Has(Role.Operator, Permission.RunDeployScripts));
        Assert.False(RolePermissions.Has(Role.Operator, Permission.UninstallPrograms));
        Assert.False(RolePermissions.Has(Role.Viewer, Permission.RunModules));
        Assert.True(RolePermissions.Has(Role.Viewer, Permission.ViewReports));
        Assert.True(RolePermissions.IsGroupScoped(Role.Operator));
    }

    [Fact]
    public void Script_and_uninstall_need_special_permission()
    {
        Assert.Equal(Permission.RunDeployScripts, RolePermissions.RequiredFor(ModuleIds.Deploy, ModuleActions.DeployRunScript));
        Assert.Equal(Permission.UninstallPrograms, RolePermissions.RequiredFor(ModuleIds.Inspect, ModuleActions.InspectUninstall));
        Assert.Equal(Permission.RunModules, RolePermissions.RequiredFor(ModuleIds.Deploy, ModuleActions.DeployInstall));
    }
}
