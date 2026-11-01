using System.Net;
using Gac.Server.Accounts;
using Gac.Server.Commands;
using Gac.Server.Data;
using Gac.Server.Packages;
using Gac.Server.Pcs;
using Gac.Server.Policies;
using Gac.Shared;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Gac.Server.Hosting;

/// <summary>Builds the GAC Server web application. Used by the service entry point and by integration tests.</summary>
public static class ServerApp
{
    public static WebApplication Build(ServerOptions options, string[]? args = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = args ?? [],
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Host.UseWindowsService(o => o.ServiceName = ServerCli.ServiceName);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        if (OperatingSystem.IsWindows() && !options.LoopbackOnly) builder.Logging.AddEventLog(s => s.SourceName = "GAC Server");
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        Directory.CreateDirectory(options.DataDir);
        var identity = ServerIdentity.LoadOrCreate(options.DataDir);
        var db = new Db(options.DatabasePath);
        db.EnsureSchema();

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 8 * 1024 * 1024;
            k.Limits.MaxConcurrentConnections = 2000;
            void Https(ListenOptions lo) { lo.Protocols = HttpProtocols.Http1AndHttp2; lo.UseHttps(identity.Certificate); }
            if (options.LoopbackOnly) k.Listen(IPAddress.Loopback, options.Port, Https);
            else k.ListenAnyIP(options.Port, Https);
        });

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = GacJson.Options.PropertyNamingPolicy;
            o.SerializerOptions.DefaultIgnoreCondition = GacJson.Options.DefaultIgnoreCondition;
            o.SerializerOptions.Encoder = GacJson.Options.Encoder;
            foreach (var c in GacJson.Options.Converters) o.SerializerOptions.Converters.Add(c);
        });

        var time = TimeProvider.System;
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new InstallState(options.DataDir));
        builder.Services.AddSingleton(time);
        builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton<SettingsStore>();
        builder.Services.AddSingleton<AuditLog>();
        builder.Services.AddSingleton(sp => new AccountService(db, sp.GetRequiredService<AuditLog>(), time, options.PasswordIterationsOverride ?? PasswordHasher.Iterations));
        builder.Services.AddSingleton<PcService>();
        builder.Services.AddSingleton<CommandService>();
        builder.Services.AddSingleton<PolicyService>();
        builder.Services.AddSingleton<EnrollThrottle>();
        builder.Services.AddSingleton(sp => new PackageStore(db, sp.GetRequiredService<AuditLog>(), time, options.PackagesDir));
        // Known official release hashes (one SHA-256 per line) enable the integrity check; absent file = check skipped.
        var hashFile = Path.Combine(options.DataDir, "release-hashes.txt");
        var knownHashes = File.Exists(hashFile)
            ? File.ReadAllLines(hashFile).Select(l => l.Trim().ToUpperInvariant()).Where(l => l.Length == 64).ToHashSet()
            : new HashSet<string>();
        builder.Services.AddSingleton(new Devices.DeviceGuard(knownHashes));
        builder.Services.AddSingleton(sp => new Devices.DeviceService(db, sp.GetRequiredService<AuditLog>(), sp.GetRequiredService<Devices.DeviceGuard>(), time));
        builder.Services.AddSingleton(sp => new Periods.PeriodService(db, sp.GetRequiredService<AuditLog>(), identity, time));
        builder.Services.AddSingleton(sp => new Calendar.ExemptionService(db, sp.GetRequiredService<AuditLog>(), time));
        // Provision the single-use setup code at startup while no account exists, so sign-up can fail closed on its absence.
        new AccountService(db, new AuditLog(db, time), time).EnsureSetupCode();
        builder.Services.AddHostedService<MaintenanceService>();

        var app = builder.Build();
        app.UseExceptionHandler(e => e.Run(async ctx =>
        {
            var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
            var (status, message) = ex switch
            {
                GacException g => (g.Status, g.Message),
                Microsoft.AspNetCore.Http.BadHttpRequestException b => (b.StatusCode, "요청 형식이 올바르지 않습니다."),
                _ => (500, "서버 내부 오류가 발생했습니다."),
            };
            if (status == 500) app.Logger.LogError(ex, "Unhandled server error");
            ctx.Response.StatusCode = status;
            await ctx.Response.WriteAsJsonAsync(new ApiError(message), GacJson.Options);
        }));
        // CHANGE-002 §3.2: until the firewall step is done the server accepts no enrollment, no agent traffic and no sign-up.
        app.Use(async (ctx, next) =>
        {
            var p = ctx.Request.Path;
            if ((p.StartsWithSegments("/api/enroll") || p.StartsWithSegments("/api/agent") || p.StartsWithSegments("/api/setup/owner"))
                && !ctx.RequestServices.GetRequiredService<InstallState>().IsComplete)
            {
                ctx.Response.StatusCode = 503;
                await ctx.Response.WriteAsJsonAsync(new ApiError(InstallState.IncompleteMessage), GacJson.Options);
                return;
            }
            await next(ctx);
        });
        Endpoints.Map(app);
        return app;
    }
}

/// <summary>Background housekeeping: trims old sessions, reports and audit rows so the DB stays small.</summary>
public sealed class MaintenanceService(Db db, TimeProvider time, ILogger<MaintenanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = time.GetUtcNow();
                using var c = db.Open();
                Dapper.SqlMapper.Execute(c, "DELETE FROM sessions WHERE last_seen < @t", new { t = Time.Ms(now - AccountService.SessionIdle) });
                Dapper.SqlMapper.Execute(c, "DELETE FROM reports WHERE finished_at < @t", new { t = Time.Ms(now.AddDays(-180)) });
                Dapper.SqlMapper.Execute(c, "DELETE FROM batches WHERE created_at < @t", new { t = Time.Ms(now.AddDays(-180)) });
                Dapper.SqlMapper.Execute(c, "DELETE FROM audit WHERE at < @t", new { t = Time.Ms(now.AddYears(-3)) });
            }
            catch (Exception e) { log.LogWarning(e, "Maintenance failed"); }
            try { await Task.Delay(TimeSpan.FromHours(1), time, stoppingToken); } catch (OperationCanceledException) { }
        }
    }
}
