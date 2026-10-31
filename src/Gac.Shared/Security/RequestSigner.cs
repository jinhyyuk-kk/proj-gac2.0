using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Gac.Shared.Security;

/// <summary>Authenticates agent → server HTTP requests with the per-agent HMAC key.</summary>
public static class RequestSigner
{
    public const string AgentHeader = "X-Gac-Agent";
    public const string TimestampHeader = "X-Gac-Timestamp";
    public const string SignatureHeader = "X-Gac-Signature";
    public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    public static string Canonical(string agentId, long unixMs, string method, string pathAndQuery, ReadOnlySpan<byte> body) =>
        string.Join('\n', agentId, unixMs.ToString(CultureInfo.InvariantCulture), method.ToUpperInvariant(), pathAndQuery,
            Convert.ToHexString(SHA256.HashData(body)));

    public static string Sign(byte[] key, string agentId, long unixMs, string method, string pathAndQuery, ReadOnlySpan<byte> body) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Canonical(agentId, unixMs, method, pathAndQuery, body))));

    public static bool Verify(byte[] key, string agentId, string timestamp, string signature, string method, string pathAndQuery,
        ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        if (!long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)) return false;
        var ts = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        if ((now - ts).Duration() > MaxSkew) return false;
        byte[] given;
        try { given = Convert.FromBase64String(signature); } catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Canonical(agentId, ms, method, pathAndQuery, body)));
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }
}

/// <summary>Enrollment handshake proof binding the enrollment code to the server certificate (see docs/DECISIONS.md).</summary>
public static class EnrollmentProof
{
    public static string Compute(string code, string thumbprint, string nonce) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(NormalizeCode(code)),
            Encoding.UTF8.GetBytes(thumbprint.ToUpperInvariant() + "|" + nonce)));

    public static bool Verify(string code, string thumbprint, string nonce, string proof)
    {
        byte[] given;
        try { given = Convert.FromBase64String(proof); } catch (FormatException) { return false; }
        var expected = Convert.FromBase64String(Compute(code, thumbprint, nonce));
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }

    public static string NormalizeCode(string code) => new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    /// <summary>Generates a code like "K7QM-2XPA-9RTD" from an unambiguous alphabet.</summary>
    public static string NewCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[12];
        for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        var s = new string(chars);
        return $"{s[..4]}-{s[4..8]}-{s[8..]}";
    }

    public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public static string ReenrollMessage(string previousAgentId, string machineName) => $"reenroll|{previousAgentId}|{machineName.ToUpperInvariant()}";

    public static string ReenrollProof(byte[] oldKey, string previousAgentId, string machineName) =>
        Convert.ToBase64String(HMACSHA256.HashData(oldKey, Encoding.UTF8.GetBytes(ReenrollMessage(previousAgentId, machineName))));

    public static bool VerifyReenroll(byte[] oldKey, string previousAgentId, string machineName, string proof)
    {
        byte[] given;
        try { given = Convert.FromBase64String(proof); } catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(given, Convert.FromBase64String(ReenrollProof(oldKey, previousAgentId, machineName)));
    }
}
