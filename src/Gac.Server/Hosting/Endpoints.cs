using System.Text;
using Gac.Server.Accounts;
using Gac.Server.Commands;
using Gac.Server.Packages;
using Gac.Server.Pcs;
using Gac.Server.Policies;
using Gac.Server.Data;
using Gac.Shared;
using Gac.Shared.Auth;
using Gac.Shared.Contracts;
using Gac.Shared.Security;

namespace Gac.Server.Hosting;

public static class Endpoints
{
    private const string SessionKey = "gac.session";
    private const string AgentKey = "gac.agent";

    public static void Map(WebApplication app)
    {
        var version = typeof(Endpoints).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";

        // ---------- enrollment (unauthenticated, proof-based) ----------
        app.MapGet("/api/enroll/hello", (string nonce, PcService pcs, ServerIdentity id) =>
        {
            if (string.IsNullOrEmpty(nonce) || nonce.Length is < 16 or > 128) return Results.BadRequest();
            return Results.Ok(new EnrollHelloResponse(EnrollmentProof.Compute(pcs.EnrollCode, id.Thumbprint, nonce), Environment.MachineName, version));
        });

        app.MapPost("/api/enroll", (EnrollRequest req, PcService pcs, ServerIdentity id, PolicyService policies, EnrollThrottle throttle, Devices.DeviceService devices, HttpContext ctx) =>
        {
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            if (!throttle.Allow(ip)) return Error("등록 시도가 너무 많습니다. 잠시 후 다시 시도하세요.", 429);
            if (!pcs.CheckCode(req.Code)) { throttle.Fail(ip); return Error("등록 코드가 올바르지 않습니다.", 403); }
            // Desktop-only + install-order are enforced here on the server from the raw probe values (CHANGE-001 1.2).
            if (req.Device is null) return Error(Devices.DeviceGuard.LaptopMessage, 403); // fail closed: no probe data
            var (decision, fp) = devices.Evaluate(req.Device, ip);
            if (decision.NeedsReview)
                return Results.Ok(new EnrollResponse("", "", "", policies.PollSeconds, EnrollStatus.PendingReview, "최고 관리자 승인을 기다리는 중입니다."));
            if (!decision.Accepted) return Error(decision.Reason, 403);
            var (agentId, key) = pcs.Enroll(req, fp);
            devices.BindAgent(fp, agentId);
            return Results.Ok(new EnrollResponse(agentId, Convert.ToBase64String(key), id.PresetPublicKeyPem, policies.PollSeconds));
        });

        // ---------- agent channel (HMAC-authenticated) ----------
        // Agent requests are HMAC-verified in middleware, before model binding consumes the body.
        app.UseWhen(c => c.Request.Path.StartsWithSegments("/api/agent"), b => b.Use(AgentAuthMiddleware));
        var agent = app.MapGroup("/api/agent");

        agent.MapPost("/poll", (PollRequest req, HttpContext ctx, PcService pcs, CommandService commands, PolicyService policies, Devices.DeviceService devices, Periods.PeriodService periodsSvc, Calendar.ExemptionService exemptions) =>
        {
            var (agentId, key) = ((string, byte[]))ctx.Items[AgentKey]!;
            var ip = ctx.Connection.RemoteIpAddress?.ToString();
            // Re-check desktop-only / revocation on every heartbeat (CHANGE-001 1.2 — three choke points).
            var revoke = devices.CheckHeartbeat(agentId, req.Device, ip);
            if (revoke != null) return Results.Ok(new PollResponse([], null, null, policies.PollSeconds, Revoked: true, RevokedReason: revoke));
            pcs.Touch(agentId, req);
            var (policy, ver) = policies.ForPc(pcs.GetGroupId(agentId));
            var cmds = commands.Deliver(agentId, key);
            var periods = periodsSvc.ActiveForPc(agentId, workPc: devices.IsWorkPc(agentId));
            // Server time + the next 60 days of lock exemption dates on every poll (CHANGE-002 §2.3).
            return Results.Ok(new PollResponse(cmds, req.PolicyVersion == ver ? null : policy, ver, policies.PollSeconds, Periods: periods, Calendar: exemptions.ForAgent()));
        });

        agent.MapPost("/reports", (ReportUpload up, HttpContext ctx, CommandService commands) =>
        {
            var (agentId, _) = ((string, byte[]))ctx.Items[AgentKey]!;
            commands.StoreReports(agentId, up.Reports ?? []);
            return Results.Ok();
        });

        agent.MapPost("/rejections", (RejectionUpload up, HttpContext ctx, CommandService commands) =>
        {
            var (agentId, _) = ((string, byte[]))ctx.Items[AgentKey]!;
            commands.StoreRejections(agentId, up.Rejections ?? []);
            return Results.Ok();
        });

        agent.MapGet("/packages/{id}", (string id, PackageStore store) =>
        {
            var path = store.FilePath(id);
            return path == null ? Results.NotFound() : Results.File(path, "application/octet-stream", enableRangeProcessing: true);
        });

        // ---------- console: setup & auth ----------
        app.MapGet("/api/setup", (AccountService acc) => Results.Ok(new SetupStatus(acc.SignUpOpen(), Environment.MachineName, version)));
        // First-ever sign-up (= Owner) requires the single-use setup code shown at the end of server installation.
        // The code is validated and consumed atomically inside CreateOwner, so two concurrent sign-ups cannot both win.
        app.MapPost("/api/setup/owner", (CreateOwnerRequest req, AccountService acc, EnrollThrottle throttle, HttpContext ctx) =>
        {
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            if (!throttle.Allow(ip)) return Error("시도가 너무 많습니다. 잠시 후 다시 시도하세요.", 429);
            try { return Results.Ok(acc.CreateOwner(req)); }
            catch (GacException e) when (e.Status == 403) { throttle.Fail(ip); throw; }
        });
        app.MapPost("/api/auth/login", (LoginRequest req, AccountService acc, Devices.DeviceService devices) =>
        {
            var resp = acc.Login(req);
            // Work-PC exemption is only granted for logins by staff accounts (Owner/Admin), never a student's Viewer/Operator.
            if (!string.IsNullOrWhiteSpace(req.MachineFingerprint))
                devices.RecordConsoleLogin(req.MachineFingerprint!, staffAccount: resp.Account.Role is Role.Owner or Role.Admin);
            return Results.Ok(resp);
        });

        // A locked client PC sends the entered Console credentials here; unlock is authorized on the server (CHANGE-001 2.4).
        // The request arrives over the agent channel, so the PC is the authenticated agent (ctx AgentKey).
        app.MapPost("/api/agent/unlock", (UnlockRequest req, HttpContext ctx, AccountService acc, Periods.PeriodService periods, PcService pcs, EnrollThrottle throttle) =>
        {
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            if (!throttle.Allow(ip)) return Results.Ok(new UnlockResponse(false, "시도가 너무 많습니다. 잠시 후 다시 시도하세요."));
            var agentId = ctx.Items[AgentKey] is ValueTuple<string, byte[]> t ? t.Item1 : null;
            if (agentId == null) return Results.Unauthorized();
            // The period must be real, running and targeting this PC — a client cannot unlock with an arbitrary id.
            if (!periods.IsActiveForPc(req.PeriodId, agentId)) return Results.Ok(new UnlockResponse(false, "이 PC에 적용 중인 기간이 아닙니다."));
            var r = acc.AuthorizeUnlock(req.UserName, req.Password);
            if (!r.Ok) { throttle.Fail(ip); return Results.Ok(new UnlockResponse(false, r.Message)); }
            // Operators may only unlock PCs in their assigned groups (same scope rule as command dispatch).
            var session = acc.SessionFor(req.UserName);
            if (session == null || !session.CanAccessGroup(pcs.GetGroupId(agentId)))
                return Results.Ok(new UnlockResponse(false, "담당 그룹이 아닌 PC는 잠금 해제할 수 없습니다."));
            acc.AuditUnlock(req.UserName, req.PeriodId, ip);
            return Results.Ok(new UnlockResponse(true, "잠금을 해제했습니다."));
        });

        var api = app.MapGroup("/api").AddEndpointFilter(SessionFilter);
        api.MapPost("/auth/logout", (HttpContext ctx, AccountService acc) => { acc.Logout(Token(ctx)!); return Results.Ok(); });
        api.MapGet("/auth/me", (HttpContext ctx, AccountService acc) => Results.Ok(acc.Get(S(ctx).AccountId)));
        api.MapPost("/auth/password", (ChangePasswordRequest req, HttpContext ctx, AccountService acc) => { acc.ChangeOwnPassword(S(ctx), req); return Results.Ok(); });

        // accounts (Owner only)
        api.MapGet("/accounts", (HttpContext ctx, AccountService acc) => Need(ctx, Permission.ManageAccounts) ?? Results.Ok(acc.List()));
        api.MapPost("/accounts", (SaveAccountRequest req, HttpContext ctx, AccountService acc) => Need(ctx, Permission.ManageAccounts) ?? Results.Ok(acc.Create(S(ctx), req)));
        api.MapPut("/accounts/{id:int}", (int id, SaveAccountRequest req, HttpContext ctx, AccountService acc) => Need(ctx, Permission.ManageAccounts) ?? Results.Ok(acc.Update(S(ctx), id, req)));
        api.MapDelete("/accounts/{id:int}", (int id, HttpContext ctx, AccountService acc) => Need(ctx, Permission.ManageAccounts) ?? Do(() => acc.Delete(S(ctx), id)));
        api.MapPost("/accounts/{id:int}/password", (int id, ResetPasswordRequest req, HttpContext ctx, AccountService acc) =>
            Need(ctx, Permission.ManageAccounts) ?? Do(() => acc.ResetPassword(S(ctx), id, req.NewPassword)));

        // PCs & groups
        api.MapGet("/pcs", (HttpContext ctx, PcService pcs) => Need(ctx, Permission.ViewPcs) ?? Results.Ok(pcs.List(S(ctx))));
        api.MapPost("/pcs/move", (MovePcRequest req, HttpContext ctx, PcService pcs) => Need(ctx, Permission.ManageGroups) ?? Do(() => pcs.Move(S(ctx), req)));
        api.MapDelete("/pcs/{id}", (string id, HttpContext ctx, PcService pcs) => Need(ctx, Permission.ManageGroups) ?? Do(() => pcs.Delete(S(ctx), id)));
        api.MapGet("/groups", (HttpContext ctx, PcService pcs) => Need(ctx, Permission.ViewPcs) ?? Results.Ok(pcs.Groups(S(ctx))));
        api.MapPost("/groups", (SaveGroupRequest req, HttpContext ctx, PcService pcs) => Need(ctx, Permission.ManageGroups) ?? Results.Ok(pcs.CreateGroup(S(ctx), req.Name)));
        api.MapPut("/groups/{id:int}", (int id, SaveGroupRequest req, HttpContext ctx, PcService pcs) => Need(ctx, Permission.ManageGroups) ?? Do(() => pcs.RenameGroup(S(ctx), id, req.Name)));
        api.MapDelete("/groups/{id:int}", (int id, HttpContext ctx, PcService pcs) => Need(ctx, Permission.ManageGroups) ?? Do(() => pcs.DeleteGroup(S(ctx), id)));
        api.MapGet("/groups/{id:int}/policy", (int id, HttpContext ctx, PolicyService p) =>
            Need(ctx, Permission.ViewPcs) ?? (S(ctx).CanAccessGroup(id) ? Results.Ok(p.GetGroupPolicy(id)) : Error("담당 그룹이 아닙니다.", 403)));
        api.MapPut("/groups/{id:int}/policy", (int id, GroupPolicy req, HttpContext ctx, PolicyService p) =>
            Need(ctx, Permission.ManagePolicies) ?? Do(() => p.SaveGroupPolicy(S(ctx), req with { GroupId = id })));

        // commands & reports
        api.MapPost("/commands", (SendCommandRequest req, HttpContext ctx, CommandService c, PcService pcs, PackageStore store) =>
            Need(ctx, Permission.RunModules) ?? Results.Ok(c.Create(S(ctx), req, pcs.GetGroupId, store.Get, pcs.IsRevoked)));
        api.MapGet("/commands", (HttpContext ctx, CommandService c, int? limit) => Need(ctx, Permission.ViewReports) ?? Results.Ok(c.Batches(S(ctx), Math.Clamp(limit ?? 100, 1, 1000))));
        api.MapGet("/commands/{batchId:guid}", (Guid batchId, HttpContext ctx, CommandService c, PcService pcs) =>
            Need(ctx, Permission.ViewReports) ?? Results.Ok(c.BatchCommands(S(ctx), batchId).Where(r => S(ctx).CanAccessGroup(pcs.GetGroupId(r.PcId))).ToList()));
        api.MapGet("/reports", (HttpContext ctx, CommandService c, PcService pcs, string? module, string? pcId, DateTimeOffset? since, int? limit) =>
            Need(ctx, Permission.ViewReports) ?? Results.Ok(c.Reports(S(ctx), module, pcId, since, Math.Clamp(limit ?? 200, 1, 5000), pcs.GetGroupId)));
        api.MapGet("/reports/latest/{module}", (string module, HttpContext ctx, CommandService c, PcService pcs) =>
            Need(ctx, Permission.ViewReports) ?? Results.Ok(c.Latest(S(ctx), module, pcs.GetGroupId)));

        // presets
        api.MapGet("/presets/shared", (HttpContext ctx, PolicyService p) => Need(ctx, Permission.ViewPcs) ?? Results.Ok(p.GetSharedPresets()));
        api.MapPut("/presets/shared", (SaveSharedPresetsRequest req, HttpContext ctx, PolicyService p) =>
            Need(ctx, Permission.ManagePresets) ?? Do(() => p.SaveSharedPresets(S(ctx), req.PresetsJson)));

        // packages
        api.MapGet("/packages", (HttpContext ctx, PackageStore s) => Need(ctx, Permission.ViewReports) ?? Results.Ok(s.List()));
        api.MapPost("/packages", async (HttpContext ctx, PackageStore s, string name, string fileName, string? silentArgs) =>
        {
            if (Need(ctx, Permission.ManagePackages) is { } denied) return denied;
            var feature = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = PackageStore.MaxPackageBytes;
            return Results.Ok(await s.AddAsync(S(ctx), name, fileName, silentArgs ?? "", ctx.Request.Body, ctx.RequestAborted));
        });
        api.MapDelete("/packages/{id}", (string id, HttpContext ctx, PackageStore s) => Need(ctx, Permission.ManagePackages) ?? Do(() => s.Delete(S(ctx), id)));

        // audit & settings
        api.MapGet("/audit", (HttpContext ctx, AuditLog a, DateTimeOffset? from, DateTimeOffset? to, int? limit) =>
            Need(ctx, Permission.ViewAudit) ?? Results.Ok(a.Query(from, to, Math.Clamp(limit ?? 1000, 1, 20000))));
        api.MapGet("/settings", (HttpContext ctx, PolicyService p) => Need(ctx, Permission.ViewPcs) ?? Results.Ok(p.GetSettings()));
        api.MapPut("/settings", (ServerSettings req, HttpContext ctx, PolicyService p) => Need(ctx, Permission.ManageServerSettings) ?? Do(() => p.SaveSettings(S(ctx), req)));
        api.MapGet("/enrollment", (HttpContext ctx, PcService pcs, ServerIdentity id, ServerOptions o) =>
            Need(ctx, Permission.ManageServerSettings) ?? Results.Ok(new EnrollmentInfo(pcs.EnrollCode, Environment.MachineName, o.Port, id.Thumbprint)));
        api.MapPost("/enrollment/regenerate", (HttpContext ctx, PcService pcs, ServerIdentity id, ServerOptions o) =>
            Need(ctx, Permission.ManageServerSettings) ?? Results.Ok(new EnrollmentInfo(pcs.RegenerateEnrollCode(S(ctx)), Environment.MachineName, o.Port, id.Thumbprint)));

        // accounts: permission matrix + ownership transfer (Owner only)
        api.MapGet("/accounts/matrix", (HttpContext ctx, AccountService acc) => Need(ctx, Permission.ManageAccounts) ?? Results.Ok(acc.GetMatrix()));
        api.MapPut("/accounts/matrix", (PermissionMatrix req, HttpContext ctx, AccountService acc) => Need(ctx, Permission.ManageAccounts) ?? Do(() => acc.SaveMatrix(S(ctx), req)));
        api.MapPost("/accounts/{id:int}/transfer-ownership", (int id, HttpContext ctx, AccountService acc) => Need(ctx, Permission.ManageAccounts) ?? Do(() => acc.TransferOwnership(S(ctx), id)));

        // PC alias (CHANGE-001 section 3): stored on the server, never renames Windows.
        api.MapPut("/pcs/{id}/alias", (string id, SetAliasRequest req, HttpContext ctx, PcService pcs) => Need(ctx, Permission.ManageGroups) ?? Do(() => pcs.SetAlias(S(ctx), id, req.Alias)));

        // devices: blocked list, pending approvals, revoke, clear console-first, work-PC exemption (CHANGE-001 1.2/1.4/2.4)
        api.MapGet("/devices/blocked", (HttpContext ctx, Devices.DeviceService d) => Need(ctx, Permission.ViewPcs) ?? Results.Ok(d.Blocked()));
        api.MapGet("/devices/pending", (HttpContext ctx, Devices.DeviceService d) => Need(ctx, Permission.ManageDevices) ?? Results.Ok(d.Pending()));
        api.MapPost("/devices/{fp}/approve", (string fp, HttpContext ctx, Devices.DeviceService d) => Need(ctx, Permission.ManageDevices) ?? Do(() => d.Approve(S(ctx), fp)));
        api.MapPost("/devices/{fp}/clear-console-first", (string fp, HttpContext ctx, Devices.DeviceService d) => Need(ctx, Permission.ManageDevices) ?? Do(() => d.ClearConsoleFirst(S(ctx), fp)));
        api.MapPost("/pcs/{id}/revoke", (string id, HttpContext ctx, Devices.DeviceService d) => Need(ctx, Permission.ManageDevices) ?? Do(() => d.Revoke(S(ctx), id)));
        api.MapPost("/pcs/{id}/remove-work-exemption", (string id, HttpContext ctx, Devices.DeviceService d) => Need(ctx, Permission.ManageDevices) ?? Do(() => d.RemoveWorkPc(S(ctx), id)));

        // period presets (CHANGE-001 2.3)
        api.MapGet("/periods", (HttpContext ctx, Periods.PeriodService p) => Need(ctx, Permission.ViewReports) ?? Results.Ok(p.List()));
        api.MapPost("/periods", (CreatePeriodRequest req, HttpContext ctx, Periods.PeriodService p, PcService pcs) =>
            Need(ctx, Permission.ManagePresets) ?? Results.Ok(p.Create(S(ctx), req, groups => pcs.PcIdsInGroups(S(ctx), groups), pc => S(ctx).CanAccessGroup(pcs.GetGroupId(pc)))));
        api.MapPost("/periods/{id}/stop", (string id, HttpContext ctx, Periods.PeriodService p) => Need(ctx, Permission.ManagePresets) ?? Do(() => p.Stop(S(ctx), id)));

        // calendar: lock exemption days (CHANGE-002 §2). Everyone with Console access can view; changes need 캘린더 관리.
        api.MapGet("/calendar/exemptions", (Calendar.ExemptionService e) => Results.Ok(e.List()));
        api.MapPost("/calendar/exemptions", (SaveExemptionDayRequest req, HttpContext ctx, Calendar.ExemptionService e) =>
            Need(ctx, Permission.ManageCalendar) ?? Results.Ok(e.Save(S(ctx), null, req)));
        api.MapPut("/calendar/exemptions/{id}", (string id, SaveExemptionDayRequest req, HttpContext ctx, Calendar.ExemptionService e) =>
            Need(ctx, Permission.ManageCalendar) ?? Results.Ok(e.Save(S(ctx), id, req)));
        api.MapDelete("/calendar/exemptions/{id}", (string id, HttpContext ctx, Calendar.ExemptionService e) =>
            Need(ctx, Permission.ManageCalendar) ?? Do(() => e.Delete(S(ctx), id)));
        api.MapPost("/calendar/exemptions/{id}/skip", (string id, SkipOccurrenceRequest req, HttpContext ctx, Calendar.ExemptionService e) =>
            Need(ctx, Permission.ManageCalendar) ?? Do(() => e.Skip(S(ctx), id, req.Date)));
    }

    private static Session S(HttpContext ctx) => (Session)ctx.Items[SessionKey]!;

    private static IResult? Need(HttpContext ctx, Permission p) =>
        S(ctx).Has(p) ? null : Error("이 작업을 할 권한이 없습니다.", 403);

    private static IResult Do(Action a) { a(); return Results.Ok(); }

    public static IResult Error(string message, int status) => Results.Json(new ApiError(message), GacJson.Options, statusCode: status);

    private static string? Token(HttpContext ctx)
    {
        var h = ctx.Request.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.Ordinal) ? h[7..].Trim() : null;
    }

    private static async ValueTask<object?> SessionFilter(EndpointFilterInvocationContext ic, EndpointFilterDelegate next)
    {
        var ctx = ic.HttpContext;
        var token = Token(ctx);
        var session = token == null ? null : ctx.RequestServices.GetRequiredService<AccountService>().Authenticate(token);
        if (session == null) return Error("로그인이 만료되었습니다. 다시 로그인하세요.", 401);
        ctx.Items[SessionKey] = session;
        return await next(ic);
    }

    private static async Task AgentAuthMiddleware(HttpContext ctx, RequestDelegate next)
    {
        var req = ctx.Request;
        var agentId = req.Headers[RequestSigner.AgentHeader].ToString();
        var ts = req.Headers[RequestSigner.TimestampHeader].ToString();
        var sig = req.Headers[RequestSigner.SignatureHeader].ToString();
        if (agentId.Length == 0 || ts.Length == 0 || sig.Length == 0) { ctx.Response.StatusCode = 401; return; }
        var key = ctx.RequestServices.GetRequiredService<PcService>().GetAgentKey(agentId);
        if (key == null) { ctx.Response.StatusCode = 410; return; }

        if (req.ContentLength > 8 * 1024 * 1024) { ctx.Response.StatusCode = 413; return; }
        req.EnableBuffering();
        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms, ctx.RequestAborted);
        req.Body.Position = 0;
        var time = ctx.RequestServices.GetRequiredService<TimeProvider>();
        if (!RequestSigner.Verify(key, agentId, ts, sig, req.Method, req.Path + req.QueryString, ms.ToArray(), time.GetUtcNow()))
        { ctx.Response.StatusCode = 401; return; }
        ctx.Items[AgentKey] = (agentId, key);
        await next(ctx);
    }
}

public sealed record ApiError(string Message);

/// <summary>Slows down enrollment-code guessing: 10 failures per IP lock that IP for 10 minutes.</summary>
public sealed class EnrollThrottle(TimeProvider time)
{
    private readonly Dictionary<string, (int Fails, DateTimeOffset Until)> _state = new();
    private readonly Lock _lock = new();

    public bool Allow(string ip)
    {
        lock (_lock) return !_state.TryGetValue(ip, out var s) || s.Until <= time.GetUtcNow() || s.Fails < 10;
    }

    public void Fail(string ip)
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            var s = _state.TryGetValue(ip, out var x) && x.Until > now ? x : (0, now);
            _state[ip] = (s.Item1 + 1, now.AddMinutes(10));
        }
    }
}
