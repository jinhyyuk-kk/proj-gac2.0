using System.Security.Cryptography;
using System.Text;

namespace Gac.Server.Accounts;

/// <summary>PBKDF2-SHA256 with a per-user random salt (CLAUDE.md section 5).</summary>
public static class PasswordHasher
{
    public const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static (string Hash, string Salt, int Iterations) Hash(string password, int iterations = Iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt), iterations);
    }

    public static bool Verify(string password, string hash, string salt, int iterations)
    {
        var expected = Convert.FromBase64String(hash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(salt), iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Korean validation message or null when the password is acceptable.</summary>
    public static string? CheckStrength(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8) return "비밀번호는 8자 이상이어야 합니다.";
        if (password.Length > 128) return "비밀번호가 너무 깁니다.";
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit)) return "비밀번호에는 문자와 숫자가 모두 들어가야 합니다.";
        return null;
    }
}
