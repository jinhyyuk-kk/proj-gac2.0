using System.Security.Cryptography;
using System.Text;
using Gac.Shared.Contracts;

namespace Gac.Shared.Security;

/// <summary>ECDSA P-256 signing of console-distributed shared presets. The tray only holds the public key.</summary>
public static class PresetSigner
{
    private static byte[] Payload(string presetsJson, DateTimeOffset issuedAt) =>
        Encoding.UTF8.GetBytes(issuedAt.ToUnixTimeMilliseconds() + "\n" + presetsJson);

    public static SignedPresetBundle Sign(string presetsJson, DateTimeOffset issuedAt, ECDsa privateKey) =>
        new(presetsJson, issuedAt, Convert.ToBase64String(privateKey.SignData(Payload(presetsJson, issuedAt), HashAlgorithmName.SHA256)));

    public static bool Verify(SignedPresetBundle bundle, string publicKeyPem)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.VerifyData(Payload(bundle.PresetsJson, bundle.IssuedAt), Convert.FromBase64String(bundle.Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }
}
