using System.Text;

namespace Curfew.Core.Security;

/// <summary>
/// RFC 4648 base32 (the alphabet used by TOTP authenticator apps). Encoding is
/// used to present a freshly generated secret; decoding parses a secret a parent
/// has entered. Decoding is lenient: case-insensitive, and spaces and "="
/// padding are ignored.
/// </summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Encodes bytes as an unpadded base32 string.</summary>
    public static string Encode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0) return string.Empty;

        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                sb.Append(Alphabet[(buffer >> bitsLeft) & 0x1F]);
            }
        }
        if (bitsLeft > 0)
            sb.Append(Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        return sb.ToString();
    }

    /// <summary>Decodes a base32 string. Throws <see cref="FormatException"/> on an invalid character.</summary>
    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var bytes = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var raw in text)
        {
            if (raw is ' ' or '=' or '\t' or '\r' or '\n') continue;
            var c = char.ToUpperInvariant(raw);
            var value = Alphabet.IndexOf(c);
            if (value < 0) throw new FormatException($"Invalid base32 character '{raw}'.");

            buffer = (buffer << 5) | value;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                bytes.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }
        return bytes.ToArray();
    }
}
