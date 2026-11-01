using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Gac.Agent;
using Gac.Agent.Config;
using Gac.Agent.Net;
using Gac.Agent.Runtime;
using Gac.Server.Hosting;
using Gac.Server.Pcs;
using Gac.Shared;
using Gac.Shared.Auth;
using Gac.Shared.Contracts;
using Gac.Shared.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gac.Agent.Tests;

/// <summary>
/// Phase 1 "done when": a PC enrolls, appears in the console API, runs a signed command, and the result is visible.
/// Uses a real HTTPS GAC Server on 127.0.0.1 with its self-signed certificate and a real agent core.
/// </summary>
public sealed class EndToEndTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gac-e2e-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private int _port;
    private string _code = "";

    public async Task InitializeAsync()
    {
        _app = ServerApp.Build(new ServerOptions { DataDir = Path.Combine(_dir, "server"), Port = 0, LoopbackOnly = true, PasswordIterationsOverride = 1000 });
        await _app.StartAsync();
        _port = new Uri(_app.Urls.First()).Port;
        _code = _app.Services.GetRequiredService<PcService>().EnrollCode;
        _setupCode = _app.Services.GetRequiredService<Gac.Server.Accounts.AccountService>().EnsureSetupCode();
    }

    private string _setupCode = "";

    private static DeviceReport DesktopReport(string mac = "AABBCCDDEEFF") => new()
    {
        Probes = new HardwareProbes
        {
            PcSystemType = 1, ChassisTypesWmi = [3], ChassisTypesSmbios = [3],
            LidPresent = false, SystemBatteriesPresent = false, BatteryInstancePresent = false,
        },
        Markers = new InstallMarkers { AgentFirstInstall = DateTimeOffset.UtcNow },
        Manufacturer = "Dell", Model = "OptiPlex", WiredMacs = [mac], ComputerName = "TEST-PC-01", WiredLinkActive = true,
    };

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<HttpClient> ConsoleAsync(string user = "owner", string password = "Passw0rd!x", Role? createRole = null)
    {
        var thumb = await Enrollment.ProveServerAsync("127.0.0.1", _port, _code, default);
        var http = new HttpClient(CertPinning.CreatePinnedHandler(thumb)) { BaseAddress = new Uri($"https://127.0.0.1:{_port}") };
        var setup = await http.GetFromJsonAsync<SetupStatus>("/api/setup", GacJson.Options);
        if (setup!.NeedsOwner)
            (await http.PostAsJsonAsync("/api/setup/owner", new CreateOwnerRequest("owner", "교장", "Passw0rd!x", _setupCode), GacJson.Options)).EnsureSuccessStatusCode();
        var login = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest(user, password), GacJson.Options);
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>(GacJson.Options))!.Token;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private async Task<(AgentCore Core, PollWorker Worker, ServerClient Client)> EnrollAgentAsync()
    {
        var req = new EnrollRequest(_code, "TEST-PC-01", "Windows", "AABBCCDDEEFF", "2.0.0", Device: DesktopReport());
        var (resp, thumb) = await Enrollment.EnrollAsync("127.0.0.1", _port, _code, req, default);
        var store = new AgentStore(Path.Combine(_dir, "agent"));
        store.SaveIdentity(new AgentIdentity("127.0.0.1", _port, thumb, resp.AgentId, resp.AgentKeyBase64, resp.PresetPublicKeyPem));
        var runner = new ProcessRunner();
        var core = new AgentCore(store, new TrayPipeServer(NullLogger<TrayPipeServer>.Instance), runner, TimeProvider.System,
            NullLogger<AgentCore>.Instance, c => AgentCli.CreateModules(c, runner)) { DeviceReportOverride = () => DesktopReport() };
        var worker = new PollWorker(core, TimeProvider.System, NullLogger<PollWorker>.Instance);
        return (core, worker, core.Client!);
    }

    [Fact]
    public async Task Pc_enrolls_runs_signed_command_and_result_reaches_console()
    {
        using var console = await ConsoleAsync();
        var (core, worker, client) = await EnrollAgentAsync();

        await worker.PollOnceAsync(client, default, _ => { });
        var pcs = await console.GetFromJsonAsync<List<PcSummary>>("/api/pcs", GacJson.Options);
        var pc = Assert.Single(pcs!);
        Assert.Equal("TEST-PC-01", pc.Name);
        Assert.True(pc.Online);

        // Old GAC "run command" (deploy.run_script) and "error check" (errors.collect).
        var script = GacJson.Serialize(new RunScriptRequest(ScriptShell.Cmd, "echo hello-gac", 30));
        var send = await console.PostAsJsonAsync("/api/commands", new SendCommandRequest(ModuleIds.Deploy, ModuleActions.DeployRunScript, script, [pc.Id]), GacJson.Options);
        send.EnsureSuccessStatusCode();
        var batch = (await send.Content.ReadFromJsonAsync<SendCommandResponse>(GacJson.Options))!;
        var send2 = await console.PostAsJsonAsync("/api/commands", new SendCommandRequest(ModuleIds.Errors, ModuleActions.ErrorsCollect, "{}", [pc.Id]), GacJson.Options);
        var batch2 = (await send2.Content.ReadFromJsonAsync<SendCommandResponse>(GacJson.Options))!;

        await worker.PollOnceAsync(client, default, _ => { });
        await worker.WhenIdleAsync();
        await worker.PollOnceAsync(client, default, _ => { }); // flush anything still queued

        var rows = await console.GetFromJsonAsync<List<CommandRow>>($"/api/commands/{batch.BatchId}", GacJson.Options);
        var row = Assert.Single(rows!);
        Assert.Equal(CommandStatus.Succeeded, row.Status);
        Assert.Contains("hello-gac", GacJson.Deserialize<DeployResult>(row.DataJson!)!.StdOut);

        var rows2 = await console.GetFromJsonAsync<List<CommandRow>>($"/api/commands/{batch2.BatchId}", GacJson.Options);
        Assert.Equal(CommandStatus.Succeeded, Assert.Single(rows2!).Status);

        var audit = await console.GetFromJsonAsync<List<AuditEntry>>("/api/audit", GacJson.Options);
        Assert.Contains(audit!, a => a.Action == "command.deploy.run_script" && a.Actor == "owner" && a.Target == "TEST-PC-01");
    }

    [Fact]
    public async Task Unsigned_or_wrong_agent_requests_are_refused()
    {
        var (_, _, _) = await EnrollAgentAsync();
        var thumb = await Enrollment.ProveServerAsync("127.0.0.1", _port, _code, default);
        using var http = new HttpClient(CertPinning.CreatePinnedHandler(thumb)) { BaseAddress = new Uri($"https://127.0.0.1:{_port}") };
        var r = await http.PostAsJsonAsync("/api/agent/poll", new PollRequest("2.0.0", null, null, null, 0), GacJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Wrong_enrollment_code_is_refused_before_sending_anything()
    {
        await Assert.ThrowsAsync<EnrollmentException>(() =>
            Enrollment.EnrollAsync("127.0.0.1", _port, "AAAA-BBBB-CCCC", new EnrollRequest("x", "PC", null, null, "2.0.0"), default));
    }

    [Fact]
    public async Task Pinned_client_refuses_a_different_certificate()
    {
        using var http = new HttpClient(CertPinning.CreatePinnedHandler(new string('0', 64))) { BaseAddress = new Uri($"https://127.0.0.1:{_port}") };
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("/api/setup"));
    }

    [Fact]
    public async Task Owner_creation_requires_enrollment_code()
    {
        var thumb = await Enrollment.ProveServerAsync("127.0.0.1", _port, _code, default);
        using var http = new HttpClient(CertPinning.CreatePinnedHandler(thumb)) { BaseAddress = new Uri($"https://127.0.0.1:{_port}") };
        var r = await http.PostAsJsonAsync("/api/setup/owner", new CreateOwnerRequest("owner", "x", "Passw0rd!x", "WRNG-WRNG-WRNG"), GacJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Operator_cannot_run_scripts_and_viewer_cannot_send_commands()
    {
        using var owner = await ConsoleAsync();
        var (_, worker, client) = await EnrollAgentAsync();
        await worker.PollOnceAsync(client, default, _ => { });
        var pc = (await owner.GetFromJsonAsync<List<PcSummary>>("/api/pcs", GacJson.Options))!.Single();
        var group = await (await owner.PostAsJsonAsync("/api/groups", new SaveGroupRequest("1학년 1반"), GacJson.Options)).Content.ReadFromJsonAsync<PcGroup>(GacJson.Options);
        (await owner.PostAsJsonAsync("/api/pcs/move", new MovePcRequest([pc.Id], group!.Id), GacJson.Options)).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync("/api/accounts", new SaveAccountRequest("op1", "운영자", Role.Operator, false, [group.Id], "Passw0rd!1"), GacJson.Options)).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync("/api/accounts", new SaveAccountRequest("view1", "조회", Role.Viewer, false, [], "Passw0rd!1"), GacJson.Options)).EnsureSuccessStatusCode();

        using var op = await ConsoleAsync("op1", "Passw0rd!1");
        var script = GacJson.Serialize(new RunScriptRequest(ScriptShell.Cmd, "whoami", 30));
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/commands", new SendCommandRequest(ModuleIds.Deploy, ModuleActions.DeployRunScript, script, [pc.Id]), GacJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync("/api/commands", new SendCommandRequest(ModuleIds.Health, ModuleActions.HealthReport, "{}", [pc.Id]), GacJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/accounts")).StatusCode);

        using var viewer = await ConsoleAsync("view1", "Passw0rd!1");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/commands", new SendCommandRequest(ModuleIds.Health, ModuleActions.HealthReport, "{}", [pc.Id]), GacJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/pcs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/audit")).StatusCode);
    }

    [Fact]
    public async Task Package_install_payload_is_taken_from_server_record()
    {
        using var owner = await ConsoleAsync();
        var (_, worker, client) = await EnrollAgentAsync();
        await worker.PollOnceAsync(client, default, _ => { });
        var pc = (await owner.GetFromJsonAsync<List<PcSummary>>("/api/pcs", GacJson.Options))!.Single();
        using var content = new ByteArrayContent("MZfake installer"u8.ToArray());
        var up = await owner.PostAsync("/api/packages?name=Test&fileName=setup.exe&silentArgs=%2FS", content);
        up.EnsureSuccessStatusCode();
        var pkg = (await up.Content.ReadFromJsonAsync<DeployPackage>(GacJson.Options))!;

        var evil = GacJson.Serialize(new DeployPackageRequest(pkg.Id, "evil.exe", "00", "/S & calc", 600));
        var send = await owner.PostAsJsonAsync("/api/commands", new SendCommandRequest(ModuleIds.Deploy, ModuleActions.DeployInstall, evil, [pc.Id]), GacJson.Options);
        send.EnsureSuccessStatusCode();
        var cmds = await client.PollAsync(new PollRequest("2.0.0", null, null, null, 0), default);
        var delivered = GacJson.Deserialize<DeployPackageRequest>(Assert.Single(cmds.Commands).PayloadJson)!;
        Assert.Equal("setup.exe", delivered.FileName);
        Assert.Equal("/S", delivered.SilentArgs);
        Assert.Equal(pkg.Sha256, delivered.Sha256);
    }
}
