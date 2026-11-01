using System.Security.Cryptography;
using System.Text;

namespace Gac.Shared.Contracts;

/// <summary>
/// Canonical hardware fingerprint shared by the agent, the console and the server so the same physical machine always
/// maps to the same id (used for install-order and work-PC exemption matching).
/// </summary>
public static class HardwareId
{
    public static string Normalize(string mac) => new(mac.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());

    public static string Compute(IEnumerable<string> wiredMacs, string? manufacturer, string? model, string computerName)
    {
        var macs = wiredMacs.Select(Normalize).Where(m => m.Length > 0).Distinct().Order().ToList();
        var basis = macs.Count > 0 ? string.Join(",", macs) : $"{manufacturer}|{model}|{computerName}".ToUpperInvariant();
        if (basis.Length == 0) basis = computerName.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("gac-device|" + basis)));
    }

    public static string FromReport(DeviceReport report) => Compute(report.WiredMacs, report.Manufacturer, report.Model, report.ComputerName);
}
