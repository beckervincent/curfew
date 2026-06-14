using System.Globalization;

namespace Curfew.Core;

/// <summary>Minimal three-part numeric version (<c>Major.Minor.Patch</c>) to decide whether a GitHub release is newer than the running build.</summary>
/// <remarks>
/// Only the SemVer subset the update check needs: three non-negative integer components compared most-significant first. Pre-release + build-metadata suffixes (e.g. <c>-rc.1</c> or <c>+build.5</c>) not interpreted; see <see cref="Parse"/> for exactly what's accepted.
/// </remarks>
public readonly record struct SemVer(int Major, int Minor, int Patch) : IComparable<SemVer>
{
    /// <summary>Parse a version like <c>"1.2.3"</c> or <c>"v1.2.3"</c>.</summary>
    /// <param name="text">Version text. Single leading <c>v</c>/<c>V</c> ignored, surrounding whitespace too. Each of the first three dot-separated components must be a non-negative base-10 integer; extra components (e.g. <c>4</c> in <c>"1.2.3.4"</c>) ignored. Signs, thousands separators, culture-specific formatting rejected.</param>
    /// <returns>Parsed <see cref="SemVer"/>, or <see langword="null"/> when <paramref name="text"/> malformed.</returns>
    public static SemVer? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = text.AsSpan().Trim();
        if (trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V'))
        {
            trimmed = trimmed[1..];
        }

        var parts = trimmed.ToString().Split('.');
        if (parts.Length < 3) return null;

        if (TryParseComponent(parts[0], out var major)
            && TryParseComponent(parts[1], out var minor)
            && TryParseComponent(parts[2], out var patch))
        {
            return new SemVer(major, minor, patch);
        }

        return null;
    }

    /// <summary>Parse one version component as a non-negative culture-invariant integer with no sign, separators, or surrounding whitespace.</summary>
    private static bool TryParseComponent(string part, out int value) =>
        int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    /// <summary>Compare this version with <paramref name="other"/>, ordering by <see cref="Major"/>, then <see cref="Minor"/>, then <see cref="Patch"/>.</summary>
    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        return Patch.CompareTo(other.Patch);
    }

    /// <summary>Canonical <c>Major.Minor.Patch</c> string.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <summary>Whether <paramref name="a"/> precedes <paramref name="b"/>.</summary>
    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;

    /// <summary>Whether <paramref name="a"/> follows <paramref name="b"/>.</summary>
    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;

    /// <summary>Whether <paramref name="a"/> precedes or equals <paramref name="b"/>.</summary>
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;

    /// <summary>Whether <paramref name="a"/> follows or equals <paramref name="b"/>.</summary>
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;
}
