using System.Security.Cryptography;
using System.Text;

namespace Gac.Server.Admin;

/// <summary>
/// Lets the elevated GAC 서버 관리 app on the Admin PC reach live service state (overload blocks) over loopback. The key
/// lives in the server data folder, which only SYSTEM and Administrators can read, so only a Windows administrator on
/// this PC can present it. Requests must also come from a loopback address.
/// </summary>
public static class LocalAdmin
{
    public const string Header = "X-Gac-Local-Key";
    public const string FileName = "local-admin.key";

    public static string LoadOrCreate(string dataDir)
    {
        var path = Path.Combine(dataDir, FileName);
        try { if (File.Exists(path)) { var k = File.ReadAllText(path).Trim(); if (k.Length >= 32) return k; } }
        catch (IOException) { }
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(path, key);
        return key;
    }

    public static bool Check(string expected, string? presented) =>
        presented != null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(presented));
}

/// <summary>Non-secret values the unelevated tray icon needs to reach the local service (HKLM is readable by users).</summary>
public static class LocalRegistry
{
    public const string KeyPath = @"SOFTWARE\GAC 2.0\Server";

    public static void Write(int port, string thumbprint)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(KeyPath);
            k.SetValue("Port", port, Microsoft.Win32.RegistryValueKind.DWord);
            k.SetValue("Thumbprint", thumbprint);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { } // not elevated (dev/tests)
    }

    public static (int Port, string Thumbprint)? Read()
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(KeyPath);
        return k?.GetValue("Port") is int p && k.GetValue("Thumbprint") is string t ? (p, t) : null;
    }

    public static void Delete()
    {
        try { Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false); }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
    }
}
