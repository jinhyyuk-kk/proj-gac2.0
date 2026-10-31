using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Gac.Shared;
using Gac.Shared.Security;

namespace Gac.Server.Hosting;

public sealed class ServerOptions
{
    public const int DefaultPort = 8443;
    public string DataDir { get; init; } = DefaultDataDir;
    public int Port { get; init; } = DefaultPort;
    /// <summary>Tests bind to 127.0.0.1 only.</summary>
    public bool LoopbackOnly { get; init; }
    /// <summary>Lower PBKDF2 iterations for tests only. Production always uses 600,000.</summary>
    public int? PasswordIterationsOverride { get; init; }

    public static string DefaultDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GAC", "Server");
    public string DatabasePath => Path.Combine(DataDir, "gac.db");
    public string PackagesDir => Path.Combine(DataDir, "packages");
    public string LogsDir => Path.Combine(DataDir, "logs");
}

public sealed record ServerFileConfig(int Port);

/// <summary>Self-signed TLS certificate and ECDSA preset-signing key, created at install time.</summary>
public sealed class ServerIdentity : IDisposable
{
    public X509Certificate2 Certificate { get; }
    public ECDsa PresetKey { get; }
    public string Thumbprint { get; }
    public string PresetPublicKeyPem { get; }

    private ServerIdentity(X509Certificate2 cert, ECDsa presetKey)
    {
        Certificate = cert;
        PresetKey = presetKey;
        Thumbprint = CertPinning.Thumbprint(cert);
        PresetPublicKeyPem = presetKey.ExportSubjectPublicKeyInfoPem();
    }

    public static ServerIdentity LoadOrCreate(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var certPath = Path.Combine(dataDir, "server.pfx");
        var keyPath = Path.Combine(dataDir, "preset-signing.pem");

        if (!File.Exists(certPath))
        {
            File.WriteAllBytes(certPath, CreateCertificate().Export(X509ContentType.Pfx));
            RestrictToAdmins(certPath);
        }
        if (!File.Exists(keyPath))
        {
            using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(keyPath, k.ExportPkcs8PrivateKeyPem());
            RestrictToAdmins(keyPath);
        }

        var cert = X509CertificateLoader.LoadPkcs12FromFile(certPath, null, X509KeyStorageFlags.EphemeralKeySet);
        var presetKey = ECDsa.Create();
        presetKey.ImportFromPem(File.ReadAllText(keyPath));
        return new ServerIdentity(cert, presetKey);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var host = Dns.GetHostName();
        var req = new CertificateRequest($"CN={host}, O=GAC 2.0", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var ip in LocalIPv4Addresses()) san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        var now = DateTimeOffset.UtcNow;
        return req.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
    }

    public static IEnumerable<IPAddress> LocalIPv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address);

    /// <summary>Best effort: SYSTEM and Administrators only. Ignored when not elevated (development/tests).</summary>
    public static void RestrictToAdmins(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            var sec = new FileSecurity();
            sec.SetAccessRuleProtection(true, false);
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            if (!IsElevated())
                sec.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
            fi.SetAccessControl(sec);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or PrivilegeNotHeldException or IOException) { }
    }

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator) || id.IsSystem;
    }

    public static ServerFileConfig LoadConfig(string dataDir)
    {
        var p = Path.Combine(dataDir, "server.json");
        if (File.Exists(p))
        {
            try { return GacJson.Deserialize<ServerFileConfig>(File.ReadAllText(p)) ?? new ServerFileConfig(ServerOptions.DefaultPort); }
            catch { }
        }
        return new ServerFileConfig(ServerOptions.DefaultPort);
    }

    public static void SaveConfig(string dataDir, ServerFileConfig config)
    {
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "server.json"), GacJson.Serialize(config));
    }

    public void Dispose()
    {
        Certificate.Dispose();
        PresetKey.Dispose();
    }
}
