using System.Security.Cryptography;
using System.Text;

namespace Curfew.Core.Security;

/// <summary>hash + verify parental passcode (any string: numeric PIN or full password, not fixed 4-digit). stored as salted PBKDF2-SHA256 so plaintext never hits disk</summary>
/// <remarks>stored form <c>pbkdf2$&lt;iterations&gt;$&lt;base64 salt&gt;$&lt;base64 hash&gt;</c>. for back-compat <see cref="Verify"/> also accepts legacy plaintext (anything without <c>pbkdf2$</c> prefix), so existing installs work until parent next sets passcode, then rewritten as hash</remarks>
public static class PasscodeHash
{
    private const string Prefix = "pbkdf2$";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 100_000;

    /// <summary>min passcode length (any chars). 8 so numeric PIN spans at least 10^8 keyspace: PBKDF2 hash stored in child-readable db, short PIN crackable offline in seconds regardless of iterations. longer/alphanumeric always accepted (up to 64-char UI cap)</summary>
    public const int MinLength = 8;

    /// <summary>salted PBKDF2 hash string for <paramref name="passcode"/></summary>
    public static string Hash(string passcode)
    {
        ArgumentNullException.ThrowIfNull(passcode);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(passcode, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Prefix}{Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>is <paramref name="stored"/> a PBKDF2 hash (vs legacy plaintext)</summary>
    public static bool IsHashed(string? stored) =>
        stored is not null && stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>verify <paramref name="passcode"/> against stored value; PBKDF2 hash + legacy plaintext</summary>
    public static bool Verify(string? passcode, string? stored)
    {
        if (passcode is null || string.IsNullOrEmpty(stored)) return false;

        if (!IsHashed(stored))
            return string.Equals(passcode, stored, StringComparison.Ordinal); // legacy plaintext

        var parts = stored[Prefix.Length..].Split('$');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations) || iterations < 1)
            return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }
        if (expected.Length == 0) return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passcode), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
