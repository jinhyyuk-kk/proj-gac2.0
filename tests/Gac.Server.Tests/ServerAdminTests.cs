using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using Gac.Server.Accounts;
using Gac.Server.Admin;
using Gac.Server.Data;
using Gac.Server.Hosting;
using Gac.Shared;
using Gac.Shared.Auth;
using Gac.Shared.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Gac.Server.Tests;

/// <summary>Local time = UTC so schedule times are deterministic.</summary>
internal sealed class UtcClock : TimeProvider
{
    public DateTimeOffset Now { get; set; }
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

internal sealed class FakePower : IServerPowerAction { public int Calls; public void Shutdown() => Calls++; }

/// <summary>CHANGE-002 §4.2: Admin PC auto shutdown.</summary>
public sealed class ServerShutdownTests : IDisposable
{
    private static readonly DateTimeOffset Monday = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
    private readonly string _path = Path.Combine(Path.GetTempPath(), "gac-sd-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly Db _db;
    private readonly UtcClock _time = new() { Now = Monday.AddHours(17) };
    private readonly FakePower _power = new();
    private readonly SettingsStore _settings;

    public ServerShutdownTests()
    {
        _db = new Db(_path);
        _db.EnsureSchema();
        _settings = new SettingsStore(_db);
        _settings.SetJson(ServerShutdownService.ScheduleKey, ServerShutdownSettings.Default() with { Enabled = true }); // Mon–Fri 18:00
    }

    private ServerShutdownService Service() => new(_settings, new AuditLog(_db, _time), _db, _time, _power);

    private void At(int hour, int minute, int day = 0) => _time.Now = Monday.AddDays(day).AddHours(hour).AddMinutes(minute);

    [Fact]
    public void Fires_at_the_scheduled_time_after_a_ten_minute_warning()
    {
        var svc = Service();
        At(17, 49); svc.Tick();
        Assert.False(svc.Status().Warning);
        At(17, 51); svc.Tick();
        Assert.True(svc.Status().Warning);
        Assert.Equal(0, _power.Calls);
        At(18, 0); svc.Tick();
        Assert.Equal(1, _power.Calls);
        At(18, 1); svc.Tick();
        Assert.Equal(1, _power.Calls); // once per day
    }

    [Fact]
    public void Waits_for_a_running_deploy_job_at_most_thirty_minutes()
    {
        using (var c = _db.Open())
        {
            c.Execute("INSERT INTO pcs(id, name, agent_key, enrolled_at) VALUES('pc1','LAB-01','k',0)");
            c.Execute("INSERT INTO batches(id, module, action, created_by, created_at) VALUES('b1','deploy','install','owner',0)");
            c.Execute("INSERT INTO commands(id, batch_id, pc_id, module, action, payload, status, created_at) VALUES('c1','b1','pc1','deploy','install','{}','Delivered',0)");
        }
        var svc = Service();
        At(18, 0); svc.Tick();
        Assert.Equal(0, _power.Calls);
        Assert.NotNull(svc.Status().WaitingFor);
        At(18, 29); svc.Tick();
        Assert.Equal(0, _power.Calls);
        At(18, 31); svc.Tick();
        Assert.Equal(1, _power.Calls); // gave up waiting after 30 minutes
        Assert.Contains(new AuditLog(_db, _time).Query(null, null, 50), a => a.Action == "server.shutdown.delayed");
    }

    [Fact]
    public void Missed_run_is_skipped_at_next_boot()
    {
        At(18, 5); // the service (PC) starts after the due time
        var svc = Service();
        svc.Tick();
        At(18, 6); svc.Tick();
        Assert.Equal(0, _power.Calls);
        Assert.Contains("건너뜀", svc.Status().LastEvent);
    }

    [Fact]
    public void Skip_today_cancels_only_today_and_extend_adds_thirty_minutes()
    {
        var svc = Service();
        svc.SkipToday("admin1");
        At(18, 0); svc.Tick();
        Assert.Equal(0, _power.Calls);
        Assert.True(svc.Status().SkippedToday);
        At(18, 0, day: 1); svc.Tick(); // Tuesday
        Assert.Equal(1, _power.Calls);

        At(17, 55, day: 2); // Wednesday
        svc.Extend("admin1");
        At(18, 0, day: 2); svc.Tick();
        Assert.Equal(1, _power.Calls);
        At(18, 30, day: 2); svc.Tick();
        Assert.Equal(2, _power.Calls);
        var audit = new AuditLog(_db, _time).Query(null, null, 50);
        Assert.Contains(audit, a => a.Action == "server.shutdown.skip_today" && a.Actor == "admin1");
        Assert.Contains(audit, a => a.Action == "server.shutdown.extend" && a.Actor == "admin1");
    }

    [Fact]
    public void Exception_date_and_weekend_do_not_shut_down()
    {
        _settings.SetJson(ServerShutdownService.ScheduleKey, ServerShutdownSettings.Default() with { Enabled = true, ExceptionDates = [DateOnly.FromDateTime(Monday.DateTime)] });
        var svc = Service();
        At(18, 0); svc.Tick();
        At(18, 0, day: 5); svc.Tick(); // Saturday
        Assert.Equal(0, _power.Calls);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
    }
}

/// <summary>CHANGE-002 §4.3: overload blocking.</summary>
public sealed class OverloadGuardTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "gac-ov-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly Db _db;
    private readonly FakeTime _time = new();
    private readonly OverloadGuard _guard;

    public OverloadGuardTests()
    {
        _db = new Db(_path);
        _db.EnsureSchema();
        using (var c = _db.Open()) c.Execute("INSERT INTO pcs(id, name, agent_key, enrolled_at, alias, fingerprint) VALUES('pc1','LAB-07','k',0,'컴퓨터실 7번','FP1')");
        _guard = new OverloadGuard(_db, new SettingsStore(_db), new AuditLog(_db, _time), _time);
    }

    private void Flood(int n = 61) { for (var i = 0; i < n; i++) _guard.CountAgentRequest("pc1", "10.0.0.7"); }

    [Fact]
    public void Agent_over_the_limit_is_blocked_for_the_duration_and_an_alert_is_queued()
    {
        Flood(60);
        Assert.False(_guard.IsBlocked("pc1", "10.0.0.7"));
        Flood(1);
        Assert.True(_guard.IsBlocked("pc1", "10.0.0.7"));
        Assert.True(_guard.IsBlocked(null, "10.0.0.7"));     // the source address too
        Assert.True(_guard.IsBlocked("pc1", "10.0.0.99"));   // and the agent from elsewhere
        var alert = Assert.Single(new AlertStore(_db, _time).List());
        Assert.Equal("컴퓨터실 7번", alert.PcAlias);
        Assert.Equal("LAB-07", alert.WindowsName);
        Assert.Equal(OverloadLimit.Requests, alert.Limit);
        Assert.False(alert.Read);
        Assert.Equal(_time.Now.AddMinutes(10), alert.BlockedUntil);
        _time.Now = _time.Now.AddMinutes(11);
        Assert.False(_guard.IsBlocked("pc1", "10.0.0.7"));
    }

    [Fact]
    public void Repeat_offence_within_24_hours_doubles_the_block()
    {
        Flood();
        _time.Now = _time.Now.AddMinutes(11);
        Flood();
        var b = Assert.Single(_guard.Blocks());
        Assert.Equal(2, b.Offences);
        Assert.Equal(_time.Now.AddMinutes(20), b.BlockedUntil);
        Assert.Equal("FP1", b.Fingerprint);
    }

    [Fact]
    public void Unblock_and_permanent_block()
    {
        Flood();
        var key = _guard.Blocks()[0].Key;
        _guard.Unblock("owner", key);
        Assert.False(_guard.IsBlocked("pc1", "10.0.0.7"));
        for (var i = 0; i < 11; i++) _guard.CountAuthFailure("10.0.0.50");
        var ipOnly = _guard.Blocks().Single(b => b.Ip == "10.0.0.50");
        Assert.Throws<GacException>(() => _guard.TakeForPermanentBlock(ipOnly.Key)); // no PC to block permanently
        _time.Now = _time.Now.AddMinutes(2);
        Flood();
        Assert.Equal("pc1", _guard.TakeForPermanentBlock(_guard.Blocks().Single(b => b.Ip == "10.0.0.7").Key));
        Assert.Contains(new AuditLog(_db, _time).Query(null, null, 50), a => a.Action == "overload.unblock");
    }

    [Fact]
    public void Console_login_is_only_affected_by_the_failed_authentication_limit()
    {
        Flood();
        Assert.False(_guard.IsLoginBlocked("10.0.0.7"));
        for (var i = 0; i < 11; i++) _guard.CountAuthFailure("10.0.0.8");
        Assert.True(_guard.IsLoginBlocked("10.0.0.8"));
        for (var i = 0; i < 6; i++) _guard.CountEnrollment("10.0.0.9");
        Assert.True(_guard.IsBlocked(null, "10.0.0.9"));
        Assert.False(_guard.IsLoginBlocked("10.0.0.9"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
    }
}

/// <summary>CHANGE-002 §4: the endpoints over real HTTPS — permissions and 429.</summary>
public sealed class ServerAdminHttpTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gac-sah-" + Guid.NewGuid().ToString("N"));
    private Microsoft.AspNetCore.Builder.WebApplication _app = null!;
    private Uri _base = null!;

    public async Task InitializeAsync()
    {
        _app = ServerApp.Build(new ServerOptions { DataDir = _dir, Port = 0, LoopbackOnly = true, PasswordIterationsOverride = 1000, PowerAction = new FakePower() });
        await _app.StartAsync();
        _base = new Uri(_app.Urls.First());
        var setup = _app.Services.GetRequiredService<AccountService>().EnsureSetupCode();
        using var http = Client();
        (await http.PostAsJsonAsync("/api/setup/owner", new CreateOwnerRequest("owner", "교장", "Passw0rd!x", setup), GacJson.Options)).EnsureSuccessStatusCode();
        var owner = await LoginAsync("owner");
        foreach (var (user, role) in new[] { ("admin1", Role.Admin), ("op1", Role.Operator), ("view1", Role.Viewer) })
            (await owner.PostAsJsonAsync("/api/accounts", new SaveAccountRequest(user, user, role, false, [], "Passw0rd!x"), GacJson.Options)).EnsureSuccessStatusCode();
    }

    private HttpClient Client() =>
        new(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { BaseAddress = _base }; // test only

    private async Task<HttpClient> LoginAsync(string user)
    {
        var http = Client();
        var r = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest(user, "Passw0rd!x"), GacJson.Options);
        r.EnsureSuccessStatusCode();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await r.Content.ReadFromJsonAsync<LoginResponse>(GacJson.Options))!.Token);
        return http;
    }

    [Theory]
    [InlineData("owner", HttpStatusCode.OK)]
    [InlineData("admin1", HttpStatusCode.OK)]
    [InlineData("op1", HttpStatusCode.Forbidden)]
    [InlineData("view1", HttpStatusCode.Forbidden)]
    public async Task Admin_can_change_the_server_shutdown_schedule_operator_and_viewer_cannot(string user, HttpStatusCode expected)
    {
        using var http = await LoginAsync(user);
        var put = await http.PutAsJsonAsync("/api/server/shutdown", ServerShutdownSettings.Default() with { Enabled = true }, GacJson.Options);
        Assert.Equal(expected, put.StatusCode);
        Assert.Equal(expected, (await http.PostAsync("/api/server/shutdown/extend", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/server/shutdown/status")).StatusCode); // everyone sees the notice
    }

    [Theory]
    [InlineData("owner", HttpStatusCode.OK)]
    [InlineData("admin1", HttpStatusCode.Forbidden)] // 서버 설정 is Owner only
    public async Task Overload_limits_change_needs_server_settings(string user, HttpStatusCode expected)
    {
        using var http = await LoginAsync(user);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/server/overload")).StatusCode); // Owner/Admin can view
        Assert.Equal(expected, (await http.PutAsJsonAsync("/api/server/overload", new OverloadSettings(), GacJson.Options)).StatusCode);
    }

    [Fact]
    public async Task Failed_agent_authentication_flood_gets_429_but_console_api_keeps_working()
    {
        using var http = Client();
        for (var i = 0; i < 11; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/agent/poll");
            req.Headers.Add(Gac.Shared.Security.RequestSigner.AgentHeader, "bogus");
            req.Headers.Add(Gac.Shared.Security.RequestSigner.TimestampHeader, "1");
            req.Headers.Add(Gac.Shared.Security.RequestSigner.SignatureHeader, "x");
            await http.SendAsync(req);
        }
        var blocked = await http.PostAsync("/api/agent/poll", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal(0, (await blocked.Content.ReadAsByteArrayAsync()).Length); // no detail
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/setup")).StatusCode);
        // the failed-authentication limit does apply to console logins from that source
        Assert.Equal(HttpStatusCode.TooManyRequests, (await http.PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", "Passw0rd!x"), GacJson.Options)).StatusCode);
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
