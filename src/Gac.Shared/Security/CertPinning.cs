using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Gac.Shared.Security;

/// <summary>
/// Certificate pinning for the GAC Server's self-signed certificate. Validation is never disabled:
/// a connection is accepted only if the presented certificate's SHA-256 matches the pinned thumbprint.
/// </summary>
public static class CertPinning
{
    public static string Thumbprint(X509Certificate2 cert) => Convert.ToHexString(SHA256.HashData(cert.RawData));

    public static bool Matches(X509Certificate? cert, string pinned) =>
        cert != null && string.Equals(Thumbprint(new X509Certificate2(cert)), pinned, StringComparison.OrdinalIgnoreCase);

    /// <summary>HttpClient handler that only trusts the pinned certificate. Used after enrollment.</summary>
    public static SocketsHttpHandler CreatePinnedHandler(string pinnedThumbprint)
    {
        if (string.IsNullOrWhiteSpace(pinnedThumbprint)) throw new ArgumentException("Pinned thumbprint is required.", nameof(pinnedThumbprint));
        return new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) => Matches(cert, pinnedThumbprint),
            },
        };
    }

    /// <summary>
    /// Handler used only during enrollment: it records the presented certificate so the caller can verify the
    /// enrollment proof (HMAC over the thumbprint) before trusting it. No request data is sent before that check.
    /// </summary>
    public static SocketsHttpHandler CreateCaptureHandler(Action<string> onThumbprint) => new()
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, cert, _, _) =>
            {
                if (cert == null) return false;
                onThumbprint(Thumbprint(new X509Certificate2(cert)));
                return true;
            },
        },
    };
}
