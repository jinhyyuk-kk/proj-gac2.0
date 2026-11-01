using System.Net;
using System.Net.Http.Json;
using Gac.Server.Hosting;
using Gac.Shared;
using Gac.Shared.Contracts;
using Microsoft.AspNetCore.Builder;

namespace Gac.Server.Tests;

/// <summary>CHANGE-002 §3: the install is complete only after the firewall rule exists and was verified.</summary>
public sealed class InstallStepTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gac-install-" + Guid.NewGuid().ToString("N"));

    public InstallStepTests() => Directory.CreateDirectory(_dir);

    private static FirewallRuleInfo Rule(string remote = "10.20.0.0/255.255.252.0", string port = "8443", int profiles = 0x7FFFFFFF) =>
        new(true, 1, 1, 6, port, remote, profiles);

    [Fact]
    public void Verify_accepts_exactly_the_range_and_port_on_all_profiles()
    {
        Assert.Null(Firewall.Verify(Rule(), 8443, ["10.20.1.5/22"]));
        Assert.NotNull(Firewall.Verify(null, 8443, ["10.20.0.0/22"]));
        Assert.NotNull(Firewall.Verify(Rule(port: "8444"), 8443, ["10.20.0.0/22"]));
        Assert.NotNull(Firewall.Verify(Rule(remote: "*"), 8443, ["10.20.0.0/22"]));                        // the world
        Assert.NotNull(Firewall.Verify(Rule(remote: "10.0.0.0/255.0.0.0"), 8443, ["10.20.0.0/22"]));       // wider range
        Assert.NotNull(Firewall.Verify(Rule(profiles: 2), 8443, ["10.20.0.0/22"]));                         // private only
        Assert.NotNull(Firewall.Verify(Rule() with { Direction = 2 }, 8443, ["10.20.0.0/22"]));
        Assert.Equal("10.20.0.0/22", Firewall.TryNormalize("10.20.0.0/255.255.252.0"));
        Assert.Null(Firewall.TryNormalize("10.20.0.0/255.0.255.0")); // non-contiguous mask
    }

    [Fact]
    public void Install_stays_incomplete_and_hides_setup_code_until_the_firewall_step()
    {
        ServerIdentity.SaveConfig(_dir, new ServerFileConfig(8443, [], InstallComplete: false));
        ServerCli.WriteEnrollmentFile(_dir);
        Assert.DoesNotContain("설치 코드(최초 회원가입용)", File.ReadAllText(Path.Combine(_dir, "enrollment.txt")));
        Assert.False(new InstallState(_dir).IsComplete);

        // invalid range, failed creation, and a rule that reads back wrong all keep the install incomplete
        Assert.NotNull(ServerCli.OpenFirewall(_dir, "not-a-cidr", null, (_, _) => null, () => Rule()));
        Assert.NotNull(ServerCli.OpenFirewall(_dir, "10.20.0.0/22", null, (_, _) => "netsh failed", () => Rule()));
        Assert.NotNull(ServerCli.OpenFirewall(_dir, "10.20.0.0/22", null, (_, _) => null, () => null));
        Assert.False(ServerIdentity.LoadConfig(_dir).IsInstallComplete);

        var applied = new List<(int, IReadOnlyList<string>)>();
        Assert.Null(ServerCli.OpenFirewall(_dir, "10.20.1.9/22", null, (p, r) => { applied.Add((p, r)); return null; }, () => Rule()));
        Assert.Equal(8443, applied[0].Item1);
        Assert.Equal(["10.20.0.0/22"], applied[0].Item2); // normalized network address, that range only
        var cfg = ServerIdentity.LoadConfig(_dir);
        Assert.True(cfg.IsInstallComplete);
        Assert.Equal(["10.20.0.0/22"], cfg.Ranges!);
        Assert.Contains("설치 코드(최초 회원가입용)", File.ReadAllText(Path.Combine(_dir, "enrollment.txt")));
    }

    [Fact]
    public void Changing_the_port_in_the_firewall_step_restarts_the_service()
    {
        ServerIdentity.SaveConfig(_dir, new ServerFileConfig(8443, [], InstallComplete: false));
        var verbs = new List<string>();
        Assert.Null(ServerCli.OpenFirewall(_dir, "10.20.0.0/22", 9443, (_, _) => null, () => Rule(port: "9443"), verbs.Add));
        Assert.Equal(["stop", "start"], verbs);
        Assert.Equal(9443, ServerIdentity.LoadConfig(_dir).Port);
    }

    [Fact]
    public void Config_written_before_change_002_counts_as_complete()
    {
        File.WriteAllText(Path.Combine(_dir, "server.json"), "{\"port\":8443,\"ranges\":[\"10.0.0.0/16\"]}");
        Assert.True(new InstallState(_dir).IsComplete);
    }

    [Fact]
    public async Task Incomplete_server_refuses_enrollment_agent_traffic_and_sign_up()
    {
        ServerIdentity.SaveConfig(_dir, new ServerFileConfig(0, [], InstallComplete: false));
        var app = ServerApp.Build(new ServerOptions { DataDir = _dir, Port = 0, LoopbackOnly = true, PasswordIterationsOverride = 1000 });
        await app.StartAsync();
        try
        {
            using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) // test only
            { BaseAddress = new Uri(app.Urls.First()) };
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("/api/enroll/hello?nonce=" + new string('a', 32))).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.PostAsync("/api/agent/poll", null)).StatusCode);
            var signUp = await http.PostAsJsonAsync("/api/setup/owner", new CreateOwnerRequest("owner", "교장", "Passw0rd!x", "x"), GacJson.Options);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, signUp.StatusCode);
            Assert.Contains("방화벽", await signUp.Content.ReadAsStringAsync());

            // the firewall step finishes while the service runs: it notices without a restart
            var cfg = ServerIdentity.LoadConfig(_dir);
            ServerIdentity.SaveConfig(_dir, cfg with { InstallComplete = true });
            await Task.Delay(3500);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/enroll/hello?nonce=" + new string('a', 32))).StatusCode);
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
