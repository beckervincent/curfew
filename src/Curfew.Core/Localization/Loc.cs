using System.Globalization;

namespace Curfew.Core.Localization;

/// <summary>
/// Lightweight, dependency-free localization used by both the WinUI app and the
/// Win32 overlay. Strings are looked up by key from a per-language catalog and
/// fall back to English when a key or language is missing, so a partial
/// translation never blanks the UI.
/// </summary>
/// <remarks>
/// Adding a language is a single new entry in <see cref="Catalog"/> (e.g. a
/// machine translation of the English values); no other code changes are needed.
/// The active language defaults to the current UI culture and can be overridden
/// at runtime via <see cref="SetLanguage"/>.
/// </remarks>
public static class Loc
{
    private const string Fallback = "en";

    private static string _lang = Fallback;

    // Runs after the field initializers (notably Catalog), so Normalize can read it.
    static Loc() => _lang = Normalize(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

    /// <summary>The active two-letter language code (e.g. "en", "de").</summary>
    public static string Language => _lang;

    /// <summary>All languages the catalog can render.</summary>
    public static IReadOnlyCollection<string> AvailableLanguages => Catalog.Keys;

    /// <summary>
    /// Overrides the active language. A null/blank or unknown code resets to the
    /// current UI culture (still falling back to English per key).
    /// </summary>
    public static void SetLanguage(string? twoLetterCode) =>
        _lang = string.IsNullOrWhiteSpace(twoLetterCode)
            ? Normalize(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)
            : Normalize(twoLetterCode);

    /// <summary>Returns the localized string for <paramref name="key"/>.</summary>
    public static string T(string key)
    {
        if (Catalog.TryGetValue(_lang, out var table) && table.TryGetValue(key, out var value))
            return value;
        if (Catalog[Fallback].TryGetValue(key, out var english))
            return english;
        return key; // Last resort: the key itself, so a missing entry is visible, not blank.
    }

    /// <summary>Localized, then <see cref="string.Format(string, object[])"/>-formatted.</summary>
    public static string T(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(key), args);

    private static string Normalize(string code)
    {
        var lang = (code ?? Fallback).Trim().ToLowerInvariant();
        // Accept full culture tags like "de-DE" by taking the language part.
        var dash = lang.IndexOf('-');
        if (dash > 0) lang = lang[..dash];
        return Catalog.ContainsKey(lang) ? lang : Fallback;
    }

    private static readonly Dictionary<string, Dictionary<string, string>> Catalog = new()
    {
        [Fallback] = new()
        {
            // Lock screen (Win32 overlay)
            ["lock.title.budget"] = "Time's Up",
            ["lock.title.schedule"] = "Outside Allowed Hours",
            ["lock.extend.caption"] = "EXTEND TIME (REQUIRES PASSCODE)",
            ["lock.enter.caption"] = "ENTER PASSCODE TO UNLOCK",
            ["lock.incorrect"] = "Incorrect passcode — try again",
            ["lock.unlock"] = "Unlock",
            ["lock.shutdown"] = "Shut Down Computer",
            ["lock.shutdown.confirm.text"] = "Are you sure you want to shut down the computer?",
            ["lock.shutdown.confirm.title"] = "Confirm Shutdown",
            ["lock.shutdown.in.short"] = "Shutting down in {0}s",
            ["lock.shutdown.in.long"] = "Shutdown in {0}",
            ["lock.exceeded"] = "Time limit exceeded",
            ["lock.default.message"] = "Screen time limit reached for today.",
            ["lock.extend.minutes"] = "+{0} min",
        },
        ["de"] = new()
        {
            ["lock.title.budget"] = "Zeit ist um",
            ["lock.title.schedule"] = "Außerhalb der erlaubten Zeit",
            ["lock.extend.caption"] = "ZEIT VERLÄNGERN (PIN ERFORDERLICH)",
            ["lock.enter.caption"] = "PIN ZUM ENTSPERREN EINGEBEN",
            ["lock.incorrect"] = "Falscher PIN – erneut versuchen",
            ["lock.unlock"] = "Entsperren",
            ["lock.shutdown"] = "Computer herunterfahren",
            ["lock.shutdown.confirm.text"] = "Möchten Sie den Computer wirklich herunterfahren?",
            ["lock.shutdown.confirm.title"] = "Herunterfahren bestätigen",
            ["lock.shutdown.in.short"] = "Herunterfahren in {0}s",
            ["lock.shutdown.in.long"] = "Herunterfahren in {0}",
            ["lock.exceeded"] = "Zeitlimit überschritten",
            ["lock.default.message"] = "Bildschirmzeit für heute erreicht.",
            ["lock.extend.minutes"] = "+{0} Min",
        },
    };
}
