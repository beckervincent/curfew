using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Curfew.Core.Security;

/// <summary>Offline unlock/bonus-time codes = RFC 6238 TOTP (HMAC-SHA1, 6 digits, 30s step); parent reads code from authenticator app over phone, child types it for bonus.</summary>
/// <remarks>
/// Offline. Trust device clock (Time Manipulation Guarding enforces). Small window absorbs skew. Replay blocked: caller records last counter (see <see cref="MatchedCounter"/>), no reuse.
/// </remarks>
public static class UnlockCode
{
    /// <summary>Code digit count.</summary>
    public const int Digits = 6;

    /// <summary>Time step length, seconds.</summary>
    public const int StepSeconds = 30;

    /// <summary>Secret entropy bytes (160-bit, SHA-1 block sized).</summary>
    private const int SecretBytes = 20;

    /// <summary>New random base32 secret for authenticator app.</summary>
    public static string GenerateSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>Current code for <paramref name="base32Secret"/> at <paramref name="unixSeconds"/>.</summary>
    public static string Generate(string base32Secret, long unixSeconds) =>
        Compute(Base32.Decode(base32Secret), unixSeconds / StepSeconds);

    /// <summary>Verify <paramref name="code"/> against secret; accept current step plus <paramref name="window"/> steps either side for skew.</summary>
    /// <param name="minCounter">Reject any step counter at or below this (replay). <c>long.MinValue</c> disables.</param>
    /// <param name="matchedCounter">Accepted step counter, caller persists.</param>
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

    /// <summary>Overload without replay protection (e.g. display checks).</summary>
    public static bool Verify(string base32Secret, string? code, long unixSeconds, int window = 1) =>
        Verify(base32Secret, code, unixSeconds, window, long.MinValue, out _);

    private static string Compute(byte[] key, long counter)
    {
        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, counter);

        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(key, message, hash);

        // RFC 4226 dynamic truncation
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
