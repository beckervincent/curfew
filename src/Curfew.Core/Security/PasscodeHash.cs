using System.Security.Cryptography;
using System.Text;

namespace Curfew.Core.Security;

/// <summary>
/// Hashing and verification for the parental passcode, which may now be any
/// string (a numeric PIN or a full password) rather than a fixed 4-digit PIN.
/// Stored as a salted PBKDF2-SHA256 hash so the plaintext never touches disk.
/// </summary>
/// <remarks>
/// The stored form is <c>pbkdf2$&lt;iterations&gt;$&lt;base64 salt&gt;$&lt;base64 hash&gt;</c>.
/// For backward compatibility, <see cref="Verify"/> also accepts a legacy
/// plaintext value (anything without the <c>pbkdf2$</c> prefix), so existing
/// installs keep working until the parent next sets the passcode, at which point
/// it is rewritten as a hash.
/// </remarks>
public static class PasscodeHash
{
    private const string Prefix = "pbkdf2$";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 100_000;

    /// <summary>Minimum length of a passcode (any characters are allowed).</summary>
    public const int MinLength = 4;

    /// <summary>Produces a salted PBKDF2 hash string for <paramref name="passcode"/>.</summary>
    public static string Hash(string passcode)
    {
        ArgumentNullException.ThrowIfNull(passcode);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(passcode, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Prefix}{Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Whether <paramref name="stored"/> is a PBKDF2 hash (vs legacy plaintext).</summary>
    public static bool IsHashed(string? stored) =>
        stored is not null && stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Verifies <paramref name="passcode"/> against the stored value, supporting
    /// both the PBKDF2 hash form and legacy plaintext.
    /// </summary>
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
