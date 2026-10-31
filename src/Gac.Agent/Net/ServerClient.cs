using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Gac.Agent.Config;
using Gac.Shared;
using Gac.Shared.Contracts;
using Gac.Shared.Security;

namespace Gac.Agent.Net;

public sealed class AgentUnknownException() : Exception("The server does not know this agent (deleted or re-installed). Re-enroll required.");

/// <summary>Agent → GAC Server over the LAN. TLS with the pinned certificate; every request HMAC-signed.</summary>
public sealed class ServerClient : IDisposable
{
    private readonly AgentIdentity _id;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    public ServerClient(AgentIdentity id, TimeProvider time)
    {
        _id = id;
        _time = time;
        _http = new HttpClient(CertPinning.CreatePinnedHandler(id.Thumbprint)) { BaseAddress = id.BaseUri, Timeout = TimeSpan.FromSeconds(60) };
    }

    public Task<PollResponse> PollAsync(PollRequest req, CancellationToken ct) => PostAsync<PollResponse>("/api/agent/poll", req, ct)!;

    public Task UploadReportsAsync(IReadOnlyList<ModuleReport> reports, CancellationToken ct) => PostAsync<object>("/api/agent/reports", new ReportUpload(reports), ct);

    public Task UploadRejectionsAsync(IReadOnlyList<CommandRejection> r, CancellationToken ct) => PostAsync<object>("/api/agent/rejections", new RejectionUpload(r), ct);

    private async Task<T?> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(GacJson.Serialize(body));
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(bytes) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        Sign(req, bytes);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.Gone) throw new AgentUnknownException();
        resp.EnsureSuccessStatusCode();
        if (typeof(T) == typeof(object)) return default;
        var json = await resp.Content.ReadAsStringAsync(ct);
        return GacJson.Deserialize<T>(json);
    }

    /// <summary>Downloads a package to <paramref name="target"/> and verifies its SHA-256. Throws <see cref="InvalidDataException"/> on mismatch.</summary>
    public async Task DownloadPackageAsync(string packageId, string sha256, string target, CancellationToken ct)
    {
        var path = "/api/agent/packages/" + Uri.EscapeDataString(packageId);
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        Sign(req, []);
        using var http = new HttpClient(CertPinning.CreatePinnedHandler(_id.Thumbprint)) { BaseAddress = _id.BaseUri, Timeout = TimeSpan.FromHours(2) };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + ".part";
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    sha.AppendData(buf, 0, n);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
            var actual = Convert.ToHexString(sha.GetHashAndReset());
            if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tmp);
                throw new InvalidDataException($"SHA-256 mismatch: expected {sha256}, got {actual}");
            }
        }
        File.Move(tmp, target, overwrite: true);
    }

    private void Sign(HttpRequestMessage req, byte[] body)
    {
        var ts = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var pathAndQuery = req.RequestUri!.IsAbsoluteUri ? req.RequestUri.PathAndQuery : req.RequestUri.OriginalString;
        req.Headers.Add(RequestSigner.AgentHeader, _id.AgentId);
        req.Headers.Add(RequestSigner.TimestampHeader, ts.ToString(System.Globalization.CultureInfo.InvariantCulture));
        req.Headers.Add(RequestSigner.SignatureHeader, RequestSigner.Sign(_id.Key, _id.AgentId, ts, req.Method.Method, pathAndQuery, body));
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Enrollment handshake (see docs/DECISIONS.md): verify the server's proof before sending the code, then pin.</summary>
public static class Enrollment
{
    public static async Task<(EnrollResponse Response, string Thumbprint)> EnrollAsync(string host, int port, string code, EnrollRequest baseRequest, CancellationToken ct)
    {
        string? seen = null;
        using var http = new HttpClient(CertPinning.CreateCaptureHandler(t => seen = t)) { BaseAddress = new UriBuilder("https", host, port).Uri, Timeout = TimeSpan.FromSeconds(30) };
        var nonce = EnrollmentProof.NewNonce();
        var hello = GacJson.Deserialize<EnrollHelloResponse>(await http.GetStringAsync("/api/enroll/hello?nonce=" + nonce, ct))
                    ?? throw new InvalidOperationException("Empty hello response");
        if (seen == null || !EnrollmentProof.Verify(code, seen, nonce, hello.Proof))
            throw new EnrollmentException("서버 확인에 실패했습니다. 등록 코드가 틀렸거나 다른 서버에 연결되었습니다.");

        // The certificate is now proven to belong to the server that knows the code; pin it for the actual enrollment.
        using var pinned = new HttpClient(CertPinning.CreatePinnedHandler(seen)) { BaseAddress = http.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
        using var content = new StringContent(GacJson.Serialize(baseRequest with { Code = code }), Encoding.UTF8, "application/json");
        using var resp = await pinned.PostAsync("/api/enroll", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var msg = TryMessage(body) ?? $"등록 실패 (HTTP {(int)resp.StatusCode})";
            throw new EnrollmentException(msg);
        }
        return (GacJson.Deserialize<EnrollResponse>(body)!, seen);
    }

    /// <summary>Console-side variant: only proves and returns the thumbprint to pin.</summary>
    public static async Task<string> ProveServerAsync(string host, int port, string code, CancellationToken ct)
    {
        string? seen = null;
        using var http = new HttpClient(CertPinning.CreateCaptureHandler(t => seen = t)) { BaseAddress = new UriBuilder("https", host, port).Uri, Timeout = TimeSpan.FromSeconds(15) };
        var nonce = EnrollmentProof.NewNonce();
        var hello = GacJson.Deserialize<EnrollHelloResponse>(await http.GetStringAsync("/api/enroll/hello?nonce=" + nonce, ct));
        if (seen == null || hello == null || !EnrollmentProof.Verify(code, seen, nonce, hello.Proof))
            throw new EnrollmentException("서버 확인에 실패했습니다.");
        return seen;
    }

    private static string? TryMessage(string body)
    {
        try { return GacJson.Deserialize<ErrorBody>(body)?.Message; } catch { return null; }
    }

    private sealed record ErrorBody(string Message);
}

public sealed class EnrollmentException(string message) : Exception(message);
