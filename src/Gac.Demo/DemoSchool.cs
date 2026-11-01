using Gac.Shared;
using Gac.Shared.Auth;
using Gac.Shared.ClassMode;
using Gac.Shared.Contracts;

namespace Gac.Demo;

/// <summary>Mutable state of one virtual PC.</summary>
public sealed class DemoPc
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public required string Name { get; init; }
    public int? GroupId { get; set; }
    public bool Online { get; set; }
    public required string Ip { get; init; }
    public string? User { get; set; }
    public double FreePercent { get; set; }
    public long DiskTotal { get; init; } = 256L * 1024 * 1024 * 1024;
    public int PendingUpdates { get; set; }
    public int RecentErrors { get; set; }
    public bool RebootRequired { get; set; }
    public bool DnsBroken { get; set; }
    public string Edition { get; init; } = "Windows 11 Education";
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.Now;
    public List<string> ExtraPrograms { get; } = [];
    public string? Alias { get; set; }
    public bool WorkPc { get; set; }
    public bool Revoked { get; set; }

    public PcSummary ToSummary(IReadOnlyList<PcGroup> groups) =>
        new(Id, string.IsNullOrWhiteSpace(Alias) ? Name : Alias!, GroupId, groups.FirstOrDefault(g => g.Id == GroupId)?.Name,
            Online, Online ? DateTimeOffset.Now.AddSeconds(-new Random(Name.GetHashCode()).Next(3, 40)) : LastSeen,
            Ip, "Windows 10.0.22631", User, "2.0.0", Math.Round(FreePercent, 1), PendingUpdates, RecentErrors, RebootRequired,
            Name, WorkPc, Revoked, string.IsNullOrWhiteSpace(Alias) ? null : Alias);
}

/// <summary>A believable fake school: about 20 PCs with varied states.</summary>
public static class DemoSchool
{
    public static void Populate(DemoServerClient s, Random rng)
    {
        string[] groups = ["1학년 1반", "1학년 2반", "2학년 1반", "컴퓨터실", "교무실"];
        foreach (var g in groups) s.AddGroup(g);

        var pcs = new List<DemoPc>
        {
            new() { Name = "1-1-교사", GroupId = 1, Online = true, Ip = "10.10.1.11", User = "teacher11", FreePercent = 41, PendingUpdates = 0 },
            new() { Name = "1-1-전자칠판", GroupId = 1, Online = true, Ip = "10.10.1.12", User = "student", FreePercent = 7.4, PendingUpdates = 3, RecentErrors = 2 },
            new() { Name = "1-2-교사", GroupId = 2, Online = true, Ip = "10.10.1.21", User = "teacher12", FreePercent = 55, RebootRequired = true, PendingUpdates = 1 },
            new() { Name = "1-2-전자칠판", GroupId = 2, Online = false, Ip = "10.10.1.22", FreePercent = 33, LastSeen = DateTimeOffset.Now.AddDays(-2) },
            new() { Name = "2-1-교사", GroupId = 3, Online = true, Ip = "10.10.2.11", User = "teacher21", FreePercent = 62, DnsBroken = true },
            new() { Name = "2-1-전자칠판", GroupId = 3, Online = true, Ip = "10.10.2.12", User = "student", FreePercent = 28, RecentErrors = 5, Edition = "Windows 10 Pro" },
        };
        for (var i = 1; i <= 10; i++)
        {
            var online = i is not (4 or 9);
            pcs.Add(new DemoPc
            {
                Name = $"컴퓨터실-{i:00}", GroupId = 4, Online = online, Ip = $"10.10.9.{100 + i}", User = online ? $"student{i:00}" : null,
                FreePercent = i == 7 ? 4.2 : 20 + rng.Next(0, 60), PendingUpdates = i % 3 == 0 ? rng.Next(1, 6) : 0, RecentErrors = i == 5 ? 3 : 0,
                LastSeen = online ? DateTimeOffset.Now : DateTimeOffset.Now.AddHours(-rng.Next(5, 60)),
            });
        }
        pcs[^4].ExtraPrograms.AddRange(["무료 게임 런처", "SpeedUp PC Pro"]);
        pcs[^2].ExtraPrograms.Add("브라우저 툴바 도우미");
        pcs.Add(new DemoPc { Name = "교무실-교감", GroupId = 5, Online = true, Ip = "10.10.0.21", User = "vice", FreePercent = 48 });
        pcs.Add(new DemoPc { Name = "교무실-정보부", GroupId = 5, Online = true, Ip = "10.10.0.22", User = "info", FreePercent = 36, PendingUpdates = 2 });
        pcs.Add(new DemoPc { Name = "교무실-행정실", GroupId = 5, Online = true, Ip = "10.10.0.23", User = "admin", FreePercent = 12, RecentErrors = 1 });
        pcs.Add(new DemoPc { Name = "교무실-보건실", GroupId = 5, Online = false, Ip = "10.10.0.24", FreePercent = 70, LastSeen = DateTimeOffset.Now.AddHours(-20) });
        foreach (var p in pcs) s.AddPc(p);

        s.AddAccount(new AccountInfo(1, "owner", "정보부장", Role.Owner, false, [], DateTimeOffset.Now, null));
        s.AddAccount(new AccountInfo(2, "admin1", "교감", Role.Admin, false, [], DateTimeOffset.Now.AddDays(-1), null));
        s.AddAccount(new AccountInfo(3, "lab", "컴퓨터실 담당", Role.Operator, false, [4], DateTimeOffset.Now.AddDays(-2), null));
        s.AddAccount(new AccountInfo(4, "viewer", "행정실", Role.Viewer, false, [], null, null));

        s.AddPackage(new DeployPackage("pkg1", "7-Zip 24", "7z2408-x64.msi", "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90", 1_900_000, "", DateTimeOffset.Now.AddDays(-10), "owner"));
        s.AddPackage(new DeployPackage("pkg2", "한컴오피스 보안 업데이트", "HOffice_Update.exe", "0F1E2D3C4B5A69788796A5B4C3D2E1F00F1E2D3C4B5A69788796A5B4C3D2E1F0", 86_000_000, "/S", DateTimeOffset.Now.AddDays(-4), "admin1"));

        var sim = s.Simulator;
        var settings = new ServerSettings(null, null, @"C:\Windows\Web\Wallpaper\Windows\img0.jpg", ["Microsoft", "한컴", "Google Chrome", "NVIDIA", "Intel", "Realtek"], 30);
        foreach (var p in pcs)
        {
            var t = p.Online ? DateTimeOffset.Now.AddMinutes(-rng.Next(5, 300)) : p.LastSeen;
            s.SeedReport(p, ModuleIds.Health, ModuleActions.HealthReport, sim.Run(p, ModuleIds.Health, ModuleActions.HealthReport, "{}", null, settings), t);
            s.SeedReport(p, ModuleIds.Errors, ModuleActions.ErrorsCollect, sim.Run(p, ModuleIds.Errors, ModuleActions.ErrorsCollect, "{}", null, settings), t);
            s.SeedReport(p, ModuleIds.Update, ModuleActions.UpdateScan, sim.Run(p, ModuleIds.Update, ModuleActions.UpdateScan, "{}", null, settings), t);
        }
        var now = DateTimeOffset.Now;
        s.SeedAudit(now.AddDays(-3), "owner", "login.success", null, null);
        s.SeedAudit(now.AddDays(-3), "owner", "presets.shared.save", null, "2 presets");
        s.SeedAudit(now.AddDays(-2), "admin1", "group.policy", "컴퓨터실", "자동 종료 17:00");
        s.SeedAudit(now.AddDays(-1), "lab", "command.clean.run", "컴퓨터실-01, 컴퓨터실-02, 컴퓨터실-03", "{\"dryRun\":true}");
        s.SeedAudit(now.AddHours(-2), "owner", "login.success", null, null);
    }

    public static PresetFile SharedPresets() => new()
    {
        Presets =
        [
            new ClassPreset
            {
                Name = "컴퓨터실 수업 시작", Trigger = PresetTrigger.Manual,
                Actions = [new() { Type = PresetActionTypes.SetVolume, Value = 30 }, new() { Type = PresetActionTypes.OpenUrl, Url = "https://www.edunet.net" }, new() { Type = PresetActionTypes.CloseApp, ProcessName = "game" }],
            },
            new ClassPreset
            {
                Name = "전자칠판 화면 복제", Trigger = PresetTrigger.Login,
                Actions = [new() { Type = PresetActionTypes.DisplayMode, Mode = "clone" }, new() { Type = PresetActionTypes.Unmute }],
            },
        ],
    };
}
