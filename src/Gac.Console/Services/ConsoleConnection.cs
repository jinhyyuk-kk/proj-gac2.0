using System.Security.Cryptography;
using System.Text;
using Gac.Shared;
using Gac.Shared.Contracts;
using Gac.Shared.Security;

namespace Gac.Console.Services;

/// <summary>Server address and pinned certificate thumbprint, saved in %APPDATA%\GAC\console.json (not a secret).</summary>
public sealed record ConsoleConnection(string Host, int Port, string Thumbprint)
{
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GAC", "console.json");

    public static ConsoleConnection? Load()
    {
        try { return File.Exists(FilePath) ? GacJson.Deserialize<ConsoleConnection>(File.ReadAllText(FilePath)) : null; }
        catch { return null; }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, GacJson.Serialize(this));
    }

    public static void Forget()
    {
        try { File.Delete(FilePath); } catch (IOException) { }
    }

    public static (string Host, int Port) ParseAddress(string s)
    {
        s = s.Trim();
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s[8..];
        s = s.TrimEnd('/');
        var idx = s.LastIndexOf(':');
        if (idx > 0 && int.TryParse(s[(idx + 1)..], out var port) && port is > 0 and < 65536) return (s[..idx], port);
        return (s, 8443);
    }

    /// <summary>
    /// First connection: verify the server knows the enrollment code (HMAC over the certificate thumbprint) before
    /// trusting and pinning its self-signed certificate. Nothing secret is sent before this check.
    /// </summary>
    public static async Task<ConsoleConnection> EstablishAsync(string address, string code, CancellationToken ct)
    {
        var (host, port) = ParseAddress(address);
        string? seen = null;
        using var http = new HttpClient(CertPinning.CreateCaptureHandler(t => seen = t)) { BaseAddress = new UriBuilder("https", host, port).Uri, Timeout = TimeSpan.FromSeconds(15) };
        var nonce = EnrollmentProof.NewNonce();
        EnrollHelloResponse? hello;
        try { hello = GacJson.Deserialize<EnrollHelloResponse>(await http.GetStringAsync("/api/enroll/hello?nonce=" + nonce, ct)); }
        catch (HttpRequestException) { throw new GacClientException("서버에 연결할 수 없습니다. 주소와 포트를 확인하세요."); }
        catch (TaskCanceledException) { throw new GacClientException("서버 응답이 없습니다. 주소와 방화벽을 확인하세요."); }
        if (seen == null || hello == null || !EnrollmentProof.Verify(code, seen, nonce, hello.Proof))
            throw new GacClientException("등록 코드가 맞지 않거나 다른 서버입니다. 관리 PC에 표시된 등록 코드를 확인하세요.");
        return new ConsoleConnection(host, port, seen);
    }
}
