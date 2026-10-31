using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Gac.Shared;
using Gac.Shared.Contracts;
using Gac.Shared.Modules;

namespace Gac.Modules.Health;

public interface IHealthSource
{
    IReadOnlyList<VolumeHealth> Volumes();
    IReadOnlyList<DiskHealth> Disks();
    (long Total, long Used) Memory();
    TimeSpan Uptime();
    double? LastBootDurationSeconds();
}

public sealed class WindowsHealthSource : IHealthSource
{
    public IReadOnlyList<VolumeHealth> Volumes() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady && d.TotalSize > 0)
            .Select(d => new VolumeHealth(d.Name.TrimEnd('\\'), string.IsNullOrEmpty(d.VolumeLabel) ? null : d.VolumeLabel, d.TotalSize, d.TotalFreeSpace,
                Math.Round(d.TotalFreeSpace * 100.0 / d.TotalSize, 1)))
            .ToList();

    public IReadOnlyList<DiskHealth> Disks()
    {
        var list = new List<DiskHealth>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"));
            foreach (ManagementObject disk in searcher.Get())
            {
                using (disk)
                {
                    var model = disk["FriendlyName"]?.ToString() ?? "디스크";
                    var health = Convert.ToUInt16(disk["HealthStatus"] ?? 5) switch { 0 => "정상", 1 => "주의", 2 => "위험", _ => "알 수 없음" };
                    long? hours = null; int? temp = null; long? readErr = null; int? wear = null;
                    try
                    {
                        foreach (ManagementObject rc in disk.GetRelated("MSFT_StorageReliabilityCounter"))
                        {
                            using (rc)
                            {
                                hours = ToLong(rc["PowerOnHours"]);
                                temp = (int?)ToLong(rc["Temperature"]);
                                readErr = ToLong(rc["ReadErrorsTotal"]);
                                wear = (int?)ToLong(rc["Wear"]);
                            }
                        }
                    }
                    catch (ManagementException) { }
                    list.Add(new DiskHealth(model, health, hours, temp is 0 ? null : temp, readErr, wear));
                }
            }
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException) { }
        return list;
    }

    private static long? ToLong(object? o) => o == null ? null : Convert.ToInt64(o);

    public (long Total, long Used) Memory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0);
        return ((long)m.ullTotalPhys, (long)(m.ullTotalPhys - m.ullAvailPhys));
    }

    public TimeSpan Uptime() => TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>Boot duration from Diagnostics-Performance event 100 (BootTime in ms). Requires admin; null when unavailable.</summary>
    public double? LastBootDurationSeconds()
    {
        try
        {
            var q = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName, "*[System[EventID=100]]") { ReverseDirection = true };
            using var reader = new EventLogReader(q);
            using var e = reader.ReadEvent();
            if (e == null) return null;
            var x = XDocument.Parse(e.ToXml());
            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            var v = x.Descendants(ns + "Data").FirstOrDefault(d => (string?)d.Attribute("Name") == "BootTime")?.Value;
            return long.TryParse(v, out var ms) ? Math.Round(ms / 1000.0, 1) : null;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or System.Xml.XmlException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}

public sealed class HealthModule(IHealthSource source, TimeProvider time) : IAgentModule
{
    public string Id => ModuleIds.Health;

    public Task<ModuleOutcome> ExecuteAsync(ModuleRequest request, CancellationToken ct) => Task.Run(() =>
    {
        if (request.Action != ModuleActions.HealthReport) return ModuleOutcome.Fail("알 수 없는 명령입니다.");
        var report = Build(request.Policy.Health);
        var vol = report.Volumes.Count == 0 ? "" : string.Join(", ", report.Volumes.Select(v => $"{v.Name} {v.FreePercent:0}% 남음"));
        var ram = report.TotalRamBytes == 0 ? "" : $"메모리 {report.UsedRamBytes * 100 / report.TotalRamBytes}% 사용";
        var summary = string.Join(" · ", new[] { vol, ram, $"가동 {report.UptimeHours:0.#}시간" }.Where(s => s.Length > 0));
        if (report.Warnings.Count > 0) summary = "⚠ " + string.Join(", ", report.Warnings) + " · " + summary;
        return ModuleOutcome.Ok(summary, report);
    }, ct);

    public HealthReport Build(HealthPolicy policy)
    {
        var volumes = source.Volumes();
        var disks = source.Disks();
        var (total, used) = source.Memory();
        var uptime = source.Uptime();
        var warnings = new List<string>();
        foreach (var v in volumes.Where(v => v.FreePercent < policy.FreeSpaceWarnPercent))
            warnings.Add($"{v.Name} 남은 공간 {v.FreePercent:0.#}% ({policy.FreeSpaceWarnPercent}% 미만)");
        foreach (var d in disks.Where(d => d.HealthStatus is "주의" or "위험"))
            warnings.Add($"디스크 '{d.Model}' 상태 {d.HealthStatus}");
        if (total > 0 && used * 100 / total >= 95) warnings.Add("메모리 사용률 95% 이상");
        return new HealthReport(volumes, disks, total, used, Math.Round(uptime.TotalHours, 1), time.GetLocalNow() - uptime, source.LastBootDurationSeconds(), warnings);
    }
}
