using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Curfew.Core.Security;

/// <summary>
/// Offline unlock / bonus-time codes implemented as RFC 6238 TOTP (HMAC-SHA1,
/// 6 digits, 30-second step). The parent keeps the shared secret in a standard
/// authenticator app (Google Authenticator, etc.); when the child is locked out
/// and the parent is not present, the parent reads the current 6-digit code over
/// the phone and the child enters it to grant a bonus extension.
/// </summary>
/// <remarks>
/// Verification works entirely offline. It relies on the device clock being
/// trustworthy, which Time Manipulation Guarding already enforces. A small
/// validity window absorbs clock skew. Replay is prevented by the caller
/// recording the last accepted time-step counter (see <see cref="MatchedCounter"/>)
/// and refusing to reuse it.
/// </remarks>
public static class UnlockCode
{
    /// <summary>Number of digits in a code.</summary>
    public const int Digits = 6;

    /// <summary>Length of the time step, in seconds.</summary>
    public const int StepSeconds = 30;

    /// <summary>Bytes of entropy in a generated secret (160-bit, SHA-1 block sized).</summary>
    private const int SecretBytes = 20;

    /// <summary>Creates a new random base32 secret to enrol in an authenticator app.</summary>
    public static string GenerateSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>The current code for <paramref name="base32Secret"/> at <paramref name="unixSeconds"/>.</summary>
    public static string Generate(string base32Secret, long unixSeconds) =>
        Compute(Base32.Decode(base32Secret), unixSeconds / StepSeconds);

    /// <summary>
    /// Verifies <paramref name="code"/> against the secret, accepting the current
    /// step and <paramref name="window"/> steps on either side for clock skew.
    /// </summary>
    /// <param name="minCounter">
    /// Reject any step counter at or below this value (replay protection). Pass
    /// <c>long.MinValue</c> to disable.
    /// </param>
    /// <param name="matchedCounter">The accepted step counter, for the caller to persist.</param>
    public static bool Verify(
        string base32Secret,
        string? code,
        long unixSeconds,
        int window,
        long minCounter,
        out long matchedCounter)
    {
        matchedCounter = 0;
        if (string.IsNullOrWhiteSpace(base32Secret) || string.IsNullOrWhiteSpace(code))
            return false;

        var trimmed = code.Trim();
        if (trimmed.Length != Digits || !trimmed.All(char.IsAsciiDigit))
            return false;

        byte[] key;
        try { key = Base32.Decode(base32Secret); }
        catch (FormatException) { return false; }
        if (key.Length == 0) return false;

        var current = unixSeconds / StepSeconds;
        for (long offset = -window; offset <= window; offset++)
        {
            var counter = current + offset;
            if (counter <= minCounter) continue;
            if (FixedTimeEquals(Compute(key, counter), trimmed))
            {
                matchedCounter = counter;
                return true;
            }
        }
        return false;
    }

    /// <summary>Convenience overload without replay protection (e.g. for display checks).</summary>
    public static bool Verify(string base32Secret, string? code, long unixSeconds, int window = 1) =>
        Verify(base32Secret, code, unixSeconds, window, long.MinValue, out _);

    private static string Compute(byte[] key, long counter)
    {
        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, counter);

        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(key, message, hash);

        // RFC 4226 dynamic truncation.
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                   | (hash[offset + 1] << 16)
                   | (hash[offset + 2] << 8)
                   | hash[offset + 3];

        var modulo = (int)Math.Pow(10, Digits);
        return (binary % modulo).ToString().PadLeft(Digits, '0');
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
