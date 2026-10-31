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

        app.MapPost("/api/enroll", (EnrollRequest req, PcService pcs, ServerIdentity id, PolicyService policies, EnrollThrottle throttle, HttpContext ctx) =>
        {
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            if (!throttle.Allow(ip)) return Error("등록 시도가 너무 많습니다. 잠시 후 다시 시도하세요.", 429);
            if (!pcs.CheckCode(req.Code)) { throttle.Fail(ip); return Error("등록 코드가 올바르지 않습니다.", 403); }
            var (agentId, key) = pcs.Enroll(req);
            return Results.Ok(new EnrollResponse(agentId, Convert.ToBase64String(key), id.PresetPublicKeyPem, policies.PollSeconds));
        });

        // ---------- agent channel (HMAC-authenticated) ----------
        var agent = app.MapGroup("/api/agent").AddEndpointFilter(AgentAuthFilter);

        agent.MapPost("/poll", (PollRequest req, HttpContext ctx, PcService pcs, CommandService commands, PolicyService policies) =>
        {
            var (agentId, key) = ((string, byte[]))ctx.Items[AgentKey]!;
            pcs.Touch(agentId, req);
            var (policy, ver) = policies.ForPc(pcs.GetGroupId(agentId));
            var cmds = commands.Deliver(agentId, key);
            return Results.Ok(new PollResponse(cmds, req.PolicyVersion == ver ? null : policy, ver, policies.PollSeconds));
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
        app.MapGet("/api/setup", (AccountService acc) => Results.Ok(new SetupStatus(acc.NeedsOwner(), Environment.MachineName, version)));
        app.MapPost("/api/setup/owner", (CreateOwnerRequest req, AccountService acc) => Results.Ok(acc.CreateOwner(req)));
        app.MapPost("/api/auth/login", (LoginRequest req, AccountService acc) => Results.Ok(acc.Login(req)));

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
        api.MapGet("/groups/{id:int}/policy", (int id, HttpContext ctx, PolicyService p) => Need(ctx, Permission.ViewPcs) ?? Results.Ok(p.GetGroupPolicy(id)));
        api.MapPut("/groups/{id:int}/policy", (int id, GroupPolicy req, HttpContext ctx, PolicyService p) =>
            Need(ctx, Permission.ManagePolicies) ?? Do(() => p.SaveGroupPolicy(S(ctx), req with { GroupId = id })));

        // commands & reports
        api.MapPost("/commands", (SendCommandRequest req, HttpContext ctx, CommandService c, PcService pcs) =>
            Need(ctx, Permission.RunModules) ?? Results.Ok(c.Create(S(ctx), req, pcs.GetGroupId)));
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

    private static async ValueTask<object?> AgentAuthFilter(EndpointFilterInvocationContext ic, EndpointFilterDelegate next)
    {
        var ctx = ic.HttpContext;
        var req = ctx.Request;
        var agentId = req.Headers[RequestSigner.AgentHeader].ToString();
        var ts = req.Headers[RequestSigner.TimestampHeader].ToString();
        var sig = req.Headers[RequestSigner.SignatureHeader].ToString();
        if (agentId.Length == 0 || ts.Length == 0 || sig.Length == 0) return Results.Unauthorized();
        var key = ctx.RequestServices.GetRequiredService<PcService>().GetAgentKey(agentId);
        if (key == null) return Results.Json(new ApiError("unknown agent"), GacJson.Options, statusCode: 410);

        req.EnableBuffering();
        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms, ctx.RequestAborted);
        req.Body.Position = 0;
        var time = ctx.RequestServices.GetRequiredService<TimeProvider>();
        if (!RequestSigner.Verify(key, agentId, ts, sig, req.Method, req.Path + req.QueryString, ms.ToArray(), time.GetUtcNow()))
            return Results.Unauthorized();
        ctx.Items[AgentKey] = (agentId, key);
        return await next(ic);
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
