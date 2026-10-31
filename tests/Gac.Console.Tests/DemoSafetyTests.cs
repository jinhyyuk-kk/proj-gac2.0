using System.Reflection;
using Gac.Demo;
using Gac.Shared;
using Gac.Shared.ClassMode;
using Gac.Shared.Contracts;

namespace Gac.Console.Tests;

/// <summary>
/// CLAUDE.md section 7 hard safety rule: nothing in the demo build may reach a real module implementation, the agent or
/// the tray app. This test fails if GAC-Demo (or the Console it is built from) references any of them, directly or
/// transitively.
/// </summary>
public class DemoSafetyTests
{
    private static readonly string[] Forbidden = ["Gac.Agent", "Gac.Tray", "Gac.Server"];

    private static HashSet<string> ReferenceClosure(Assembly root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<AssemblyName>([root.GetName()]);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!seen.Add(name.Name!)) continue;
            if (!name.Name!.StartsWith("Gac", StringComparison.OrdinalIgnoreCase) && name.Name != root.GetName().Name) continue; // framework: stop
            Assembly asm;
            try { asm = Assembly.Load(name); } catch (FileNotFoundException) { continue; }
            foreach (var r in asm.GetReferencedAssemblies()) queue.Enqueue(r);
        }
        return seen;
    }

    [Fact]
    public void Demo_build_cannot_reach_real_modules_agent_server_or_tray()
    {
        var closure = ReferenceClosure(typeof(DemoServerClient).Assembly);
        Assert.Contains("Gac.Console", closure);
        Assert.DoesNotContain(closure, n => n.StartsWith("Gac.Modules.", StringComparison.OrdinalIgnoreCase));
        foreach (var f in Forbidden) Assert.DoesNotContain(f, closure);
        Assert.Contains("Gac.Tray.UI", closure); // UI-only library is allowed: it holds no real implementations
    }

    [Fact]
    public void Console_build_cannot_reach_real_modules()
    {
        var closure = ReferenceClosure(typeof(Gac.Console.ConsoleApp).Assembly);
        Assert.DoesNotContain(closure, n => n.StartsWith("Gac.Modules.", StringComparison.OrdinalIgnoreCase));
        foreach (var f in Forbidden) Assert.DoesNotContain(f, closure);
    }

    [Fact]
    public void Ui_library_has_no_real_implementations()
    {
        var closure = ReferenceClosure(typeof(Gac.Tray.UI.PresetEditorForm).Assembly);
        Assert.DoesNotContain(closure, n => n.StartsWith("Gac.Modules.", StringComparison.OrdinalIgnoreCase));
        foreach (var f in Forbidden) Assert.DoesNotContain(f, closure);
    }

    [Fact]
    public void Demo_uses_the_fake_client_and_never_saves_files()
    {
        using var client = new DemoServerClient(delayScale: 0);
        Assert.True(client.IsDemo);
        var options = DemoProgram.CreateOptions(false);
        Assert.False(options.AllowFileExport);
        Assert.Equal("체험 모드 · 실제 PC에는 아무 변화도 없습니다", options.Banner);
        // The demo client type must not hold anything that could talk to the network.
        var fields = typeof(DemoServerClient).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.DoesNotContain(fields, f => typeof(HttpClient).IsAssignableFrom(f.FieldType) || f.FieldType.Namespace?.StartsWith("System.Net") == true);
    }

    [Fact]
    public async Task Demo_upload_does_not_read_the_file()
    {
        using var client = new DemoServerClient(delayScale: 0);
        var pkg = await client.UploadPackageAsync("x", @"Z:\does\not\exist\setup.exe", "/S", null, default);
        Assert.Equal("setup.exe", pkg.FileName);
    }
}

public class DemoModuleTests
{
    [Fact]
    public void School_has_about_twenty_pcs_in_named_groups_with_varied_states()
    {
        using var c = new DemoServerClient(delayScale: 0);
        var pcs = c.GetPcsAsync().Result;
        var groups = c.GetGroupsAsync().Result;
        Assert.InRange(pcs.Count, 18, 24);
        Assert.Contains(groups, g => g.Name == "1학년 1반");
        Assert.Contains(groups, g => g.Name == "컴퓨터실");
        Assert.Contains(groups, g => g.Name == "교무실");
        Assert.Contains(pcs, p => !p.Online);
        Assert.Contains(pcs, p => p.MinFreePercent < 10);
        Assert.Contains(pcs, p => p.PendingUpdates > 0);
        Assert.Contains(pcs, p => p.RecentErrors > 0);
        var accounts = c.GetAccountsAsync().Result;
        Assert.Equal(4, accounts.Select(a => a.Role).Distinct().Count());
        Assert.Equal(Gac.Shared.Auth.Role.Owner, c.Owner.Role);
    }

    public static IEnumerable<object[]> AllActions() =>
        ModuleActions.ByModule.SelectMany(kv => kv.Value.Select(a => new object[] { kv.Key, a }));

    [Theory]
    [MemberData(nameof(AllActions))]
    public async Task Every_module_action_can_be_tried(string module, string action)
    {
        using var c = new DemoServerClient(delayScale: 0);
        var pc = (await c.GetPcsAsync()).First(p => p.Online && p.Name.StartsWith("컴퓨터실"));
        var payload = (module, action) switch
        {
            (ModuleIds.Deploy, ModuleActions.DeployInstall) => GacJson.Serialize(new DeployPackageRequest("pkg1", "7z.msi", "AA", "", 600)),
            (ModuleIds.Deploy, ModuleActions.DeployRunScript) => GacJson.Serialize(new RunScriptRequest(ScriptShell.Cmd, "echo hi\nhostname", 60)),
            (ModuleIds.Inspect, ModuleActions.InspectUninstall) => GacJson.Serialize(new UninstallRequest(["HKLM64|{X}"])),
            (ModuleIds.Clean, _) => GacJson.Serialize(new CleanRequest(true)),
            (ModuleIds.Shutdown, ModuleActions.ShutdownNow) => GacJson.Serialize(new ShutdownNowRequest(5)),
            (ModuleIds.Inspect, _) => GacJson.Serialize(new InspectRequest()),
            _ => "{}",
        };
        var sent = await c.SendCommandAsync(new SendCommandRequest(module, action, payload, [pc.Id]));
        IReadOnlyList<CommandRow> rows = [];
        for (var i = 0; i < 100; i++)
        {
            rows = await c.GetBatchAsync(sent.BatchId);
            if (rows.All(r => r.FinishedAt != null)) break;
            await Task.Delay(20);
        }
        var row = Assert.Single(rows);
        Assert.NotNull(row.FinishedAt);
        Assert.False(string.IsNullOrWhiteSpace(row.Summary));
        Assert.Matches("[가-힣]", row.Summary!);
        Assert.False(string.IsNullOrWhiteSpace(Gac.Console.UI.ReportFormatter.Format(module, row.DataJson)));
    }

    [Fact]
    public async Task Offline_pc_command_stays_pending()
    {
        using var c = new DemoServerClient(delayScale: 0);
        var pc = (await c.GetPcsAsync()).First(p => !p.Online);
        var sent = await c.SendCommandAsync(new SendCommandRequest(ModuleIds.Health, ModuleActions.HealthReport, "{}", [pc.Id]));
        await Task.Delay(50);
        Assert.Equal(CommandStatus.Pending, Assert.Single(await c.GetBatchAsync(sent.BatchId)).Status);
    }

    [Fact]
    public async Task Demo_rejects_invalid_shared_presets()
    {
        using var c = new DemoServerClient(delayScale: 0);
        var bad = GacJson.Serialize(new PresetFile { Presets = [new ClassPreset { Name = "x", Actions = [new() { Type = "set_volume", Value = 500 }] }] });
        await Assert.ThrowsAsync<Gac.Console.Services.GacClientException>(() => c.SaveSharedPresetsAsync(bad));
    }

    [Fact]
    public void Csv_export_neutralizes_formulas()
    {
        var csv = Gac.Console.UI.ReportFormatter.ToCsv([["=cmd|' /C calc'!A0", "정상", "-1+1", "@SUM(A1)"]]);
        Assert.DoesNotContain("\"=", csv);
        Assert.Contains("\"'=cmd", csv);
        Assert.Contains("\"'-1+1\"", csv);
        Assert.Contains("\"'@SUM", csv);
    }
}
