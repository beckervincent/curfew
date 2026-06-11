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

            // System tray + warnings
            ["tray.settings"] = "Open Settings",
            ["tray.left"] = "Curfew · {0} left",
            ["tray.idle"] = "Curfew",
            ["warn.default"] = "Screen time is almost up.",

            // Setup wizard
            ["setup.title"] = "Set up Curfew",
            ["setup.subtitle"] = "Create an administrator PIN and choose how screen time is enforced. You can fine-tune everything later in Settings.",
            ["setup.pin.header"] = "Administrator PIN",
            ["setup.pin.desc"] = "Required to change settings or unlock the device",
            ["setup.pin.field"] = "PIN or password",
            ["setup.pin.confirm"] = "Confirm PIN",
            ["setup.limits.header"] = "Time limits",
            ["setup.limits.desc"] = "Control how long and when the device can be used",
            ["setup.daily.title"] = "Daily time limit",
            ["setup.daily.desc"] = "Cap total screen time each day",
            ["setup.hours"] = "Hours per day",
            ["setup.schedule.title"] = "Weekly schedule",
            ["setup.schedule.desc"] = "Only allow usage during painted time windows — set the grid in Settings",
            ["setup.filter.header"] = "Content filtering",
            ["setup.filter.desc"] = "Filter unwanted sites at the DNS level using Cloudflare",
            ["setup.filter.level"] = "Filter level",
            ["setup.protection.header"] = "Protection",
            ["setup.protection.desc"] = "Guard against attempts to bypass enforcement",
            ["setup.doh.title"] = "Block DNS-over-HTTPS bypass",
            ["setup.doh.desc"] = "Prevents apps from sidestepping the content filter",
            ["setup.timeguard.title"] = "Time Manipulation Guarding",
            ["setup.timeguard.desc"] = "Detects and reverses clock changes used to dodge limits",
            ["setup.continue"] = "Continue",
            ["setup.err.pinlen"] = "PIN or password must be at least {0} characters.",
            ["setup.err.pinmatch"] = "PINs do not match.",

            // Content-filter choices (shared by setup and settings)
            ["filter.none"] = "None",
            ["filter.malware"] = "Block malware (Cloudflare 1.1.1.2)",
            ["filter.family"] = "Block malware and adult content (Cloudflare 1.1.1.3)",

            // Weekday names (Monday-first, index 0..6)
            ["day.0"] = "Monday",
            ["day.1"] = "Tuesday",
            ["day.2"] = "Wednesday",
            ["day.3"] = "Thursday",
            ["day.4"] = "Friday",
            ["day.5"] = "Saturday",
            ["day.6"] = "Sunday",

            // Common
            ["common.on"] = "On",
            ["common.off"] = "Off",
            ["common.ok"] = "OK",
            ["common.cancel"] = "Cancel",
            ["common.save"] = "Save",

            // Passcode prompt
            ["passcode.title"] = "Enter passcode",
            ["passcode.subtitle"] = "Enter your PIN or password to continue.",
            ["passcode.error.title"] = "Incorrect passcode",
            ["passcode.error.msg"] = "Please check the PIN and try again.",

            // Settings
            ["settings.title"] = "Curfew Settings",
            ["settings.subtitle"] = "Configure screen-time limits, schedules, and protection for this device.",
            ["settings.daily.desc"] = "Maximum screen time allowed per day, in hours.",
            ["settings.schedule.desc"] = "Allow usage only within the painted time windows.",
            ["settings.schedule.hint"] = "Click and drag across the grid to allow (blue) or block usage for each day.",
            ["settings.warnings.title"] = "Warnings",
            ["settings.warnings.desc"] = "Show a heads-up before time runs out so it's not a surprise.",
            ["settings.warn.minutes"] = "Minutes before",
            ["settings.warn.message"] = "Message",
            ["settings.warn.first"] = "First warning",
            ["settings.warn.second"] = "Second warning",
            ["settings.lock.title"] = "Lock screen and message",
            ["settings.lock.desc"] = "What appears when time is up, and when the device pauses on its own.",
            ["settings.lock.blockmsg"] = "Blocking message",
            ["settings.lock.blockmsg.placeholder"] = "Shown on the lock screen when time runs out",
            ["settings.lock.shutdown"] = "Shutdown timeout (minutes)",
            ["settings.idle.toggle"] = "Auto-pause when idle",
            ["settings.idle.timeout"] = "Idle timeout (minutes)",
            ["settings.filter.desc"] = "Use Cloudflare DNS to filter unwanted or harmful content.",
            ["settings.doh"] = "Also block DNS-over-HTTPS bypass",
            ["settings.protection.title"] = "Protection and updates",
            ["settings.protection.desc"] = "Keep enforcement tamper-resistant and the app up to date.",
            ["settings.autoupdate"] = "Install updates automatically",
            ["settings.passcode.title"] = "Change passcode",
            ["settings.passcode.desc"] = "Leave all fields blank to keep your current passcode.",
            ["settings.passcode.current"] = "Current",
            ["settings.passcode.new"] = "New",
            ["settings.passcode.confirm"] = "Confirm",
            ["settings.passcode.help.new"] = "At least 4 characters",
            ["settings.err.currentwrong"] = "Current passcode is incorrect.",
            ["settings.err.newmatch"] = "New passcode and confirmation do not match.",
            ["settings.err.newlen"] = "Passcode must be at least {0} characters.",

            // Usage history
            ["settings.history.title"] = "Usage history",
            ["settings.history.desc"] = "Active screen time over the last 7 days.",
            ["settings.history.minutes"] = "{0} min",
            ["settings.history.hours"] = "{0} h {1} min",

            // Offline unlock codes
            ["settings.unlock.title"] = "Offline unlock codes",
            ["settings.unlock.desc"] = "Scan this QR code with an authenticator app. When the device is locked and you are away, read the current code to your child to grant bonus time — no internet needed.",
            ["settings.unlock.secret"] = "Secret key",
            ["settings.unlock.uri"] = "Setup link",
            ["settings.unlock.bonus"] = "Bonus minutes per code",
            ["settings.unlock.regenerate"] = "Generate new secret",
            ["settings.unlock.configure"] = "Configure",
            ["settings.unlock.scan"] = "Scan with an authenticator app",
            // Schedule grid (shared by Setup + Settings)
            ["schedule.tool"] = "Paint tool",
            ["schedule.allow"] = "Allow",
            ["schedule.block"] = "Block",
            ["schedule.allowed"] = "Allowed",
            ["schedule.blocked"] = "Blocked",
            ["schedule.quickfill"] = "Quick fill",
            ["schedule.preset.allowall"] = "Allow all",
            ["schedule.preset.blockall"] = "Block all",
            ["schedule.preset.weeknights"] = "Weeknights 4–8 PM",
            ["schedule.preset.weekends"] = "Weekends only",
            ["schedule.caption"] = "Each cell is a 30-minute slot. Hold and drag across the grid to paint quickly.",
            // Setup advanced options
            ["setup.advanced"] = "Advanced",
            ["setup.perday.hint"] = "Set a different limit for each day.",
            // Manual update check
            ["settings.update.check"] = "Check for updates",
            ["settings.update.now"] = "Update now",
            ["settings.update.checking"] = "Checking for updates…",
            ["settings.update.current"] = "Current version: {0}",
            ["settings.update.uptodate"] = "Up to date (version {0}).",
            ["settings.update.available"] = "Version {0} is available.",
            ["settings.update.downloading"] = "Downloading update…",
            ["settings.update.failed"] = "Update check failed. Try again later.",
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

            ["tray.settings"] = "Einstellungen öffnen",
            ["tray.left"] = "Curfew · {0} übrig",
            ["tray.idle"] = "Curfew",
            ["warn.default"] = "Die Bildschirmzeit ist fast aufgebraucht.",

            ["setup.title"] = "Curfew einrichten",
            ["setup.subtitle"] = "Erstelle eine Administrator-PIN und lege fest, wie die Bildschirmzeit geregelt wird. Alles lässt sich später in den Einstellungen anpassen.",
            ["setup.pin.header"] = "Administrator-PIN",
            ["setup.pin.desc"] = "Erforderlich, um Einstellungen zu ändern oder das Gerät zu entsperren",
            ["setup.pin.field"] = "PIN oder Passwort",
            ["setup.pin.confirm"] = "PIN bestätigen",
            ["setup.limits.header"] = "Zeitlimits",
            ["setup.limits.desc"] = "Steuere, wie lange und wann das Gerät genutzt werden darf",
            ["setup.daily.title"] = "Tägliches Zeitlimit",
            ["setup.daily.desc"] = "Begrenzt die gesamte Bildschirmzeit pro Tag",
            ["setup.hours"] = "Stunden pro Tag",
            ["setup.schedule.title"] = "Wochenplan",
            ["setup.schedule.desc"] = "Nutzung nur in den markierten Zeitfenstern erlauben – Raster in den Einstellungen",
            ["setup.filter.header"] = "Inhaltsfilter",
            ["setup.filter.desc"] = "Unerwünschte Seiten per DNS über Cloudflare filtern",
            ["setup.filter.level"] = "Filterstufe",
            ["setup.protection.header"] = "Schutz",
            ["setup.protection.desc"] = "Schützt vor Versuchen, die Beschränkungen zu umgehen",
            ["setup.doh.title"] = "DNS-über-HTTPS-Umgehung blockieren",
            ["setup.doh.desc"] = "Verhindert, dass Apps den Inhaltsfilter umgehen",
            ["setup.timeguard.title"] = "Schutz vor Zeitmanipulation",
            ["setup.timeguard.desc"] = "Erkennt und korrigiert Uhränderungen zum Umgehen der Limits",
            ["setup.continue"] = "Weiter",
            ["setup.err.pinlen"] = "PIN oder Passwort muss mindestens {0} Zeichen haben.",
            ["setup.err.pinmatch"] = "Die PINs stimmen nicht überein.",

            ["filter.none"] = "Keiner",
            ["filter.malware"] = "Schadsoftware blockieren (Cloudflare 1.1.1.2)",
            ["filter.family"] = "Schadsoftware und Inhalte für Erwachsene blockieren (Cloudflare 1.1.1.3)",

            ["day.0"] = "Montag",
            ["day.1"] = "Dienstag",
            ["day.2"] = "Mittwoch",
            ["day.3"] = "Donnerstag",
            ["day.4"] = "Freitag",
            ["day.5"] = "Samstag",
            ["day.6"] = "Sonntag",

            ["common.on"] = "Ein",
            ["common.off"] = "Aus",
            ["common.ok"] = "OK",
            ["common.cancel"] = "Abbrechen",
            ["common.save"] = "Speichern",

            ["passcode.title"] = "PIN eingeben",
            ["passcode.subtitle"] = "Gib deine PIN oder dein Passwort ein, um fortzufahren.",
            ["passcode.error.title"] = "Falsche PIN",
            ["passcode.error.msg"] = "Bitte PIN prüfen und erneut versuchen.",

            ["settings.title"] = "Curfew Einstellungen",
            ["settings.subtitle"] = "Bildschirmzeit-Limits, Zeitpläne und Schutz für dieses Gerät konfigurieren.",
            ["settings.daily.desc"] = "Maximale Bildschirmzeit pro Tag, in Stunden.",
            ["settings.schedule.desc"] = "Nutzung nur in den markierten Zeitfenstern erlauben.",
            ["settings.schedule.hint"] = "Im Raster ziehen, um Nutzung pro Tag zu erlauben (blau) oder zu blockieren.",
            ["settings.warnings.title"] = "Warnungen",
            ["settings.warnings.desc"] = "Vorwarnung anzeigen, bevor die Zeit abläuft.",
            ["settings.warn.minutes"] = "Minuten vorher",
            ["settings.warn.message"] = "Nachricht",
            ["settings.warn.first"] = "Erste Warnung",
            ["settings.warn.second"] = "Zweite Warnung",
            ["settings.lock.title"] = "Sperrbildschirm und Nachricht",
            ["settings.lock.desc"] = "Was erscheint, wenn die Zeit um ist und wann das Gerät selbst pausiert.",
            ["settings.lock.blockmsg"] = "Sperrnachricht",
            ["settings.lock.blockmsg.placeholder"] = "Wird beim Ablauf der Zeit auf dem Sperrbildschirm angezeigt",
            ["settings.lock.shutdown"] = "Zeit bis Herunterfahren (Minuten)",
            ["settings.idle.toggle"] = "Bei Inaktivität automatisch pausieren",
            ["settings.idle.timeout"] = "Inaktivitäts-Zeitlimit (Minuten)",
            ["settings.filter.desc"] = "Cloudflare-DNS nutzen, um unerwünschte oder schädliche Inhalte zu filtern.",
            ["settings.doh"] = "Auch DNS-über-HTTPS-Umgehung blockieren",
            ["settings.protection.title"] = "Schutz und Updates",
            ["settings.protection.desc"] = "Hält die Beschränkungen manipulationssicher und die App aktuell.",
            ["settings.autoupdate"] = "Updates automatisch installieren",
            ["settings.passcode.title"] = "PIN ändern",
            ["settings.passcode.desc"] = "Alle Felder leer lassen, um die aktuelle PIN zu behalten.",
            ["settings.passcode.current"] = "Aktuell",
            ["settings.passcode.new"] = "Neu",
            ["settings.passcode.confirm"] = "Bestätigen",
            ["settings.passcode.help.new"] = "Mindestens 4 Zeichen",
            ["settings.err.currentwrong"] = "Aktuelle PIN ist falsch.",
            ["settings.err.newmatch"] = "Neue PIN und Bestätigung stimmen nicht überein.",
            ["settings.err.newlen"] = "Passwort muss mindestens {0} Zeichen haben.",

            ["settings.history.title"] = "Nutzungsverlauf",
            ["settings.history.desc"] = "Aktive Bildschirmzeit der letzten 7 Tage.",
            ["settings.history.minutes"] = "{0} Min",
            ["settings.history.hours"] = "{0} Std {1} Min",

            ["settings.unlock.title"] = "Offline-Entsperrcodes",
            ["settings.unlock.desc"] = "Scanne diesen QR-Code mit einer Authenticator-App. Wenn das Gerät gesperrt ist und du nicht da bist, lies deinem Kind den aktuellen Code vor, um Bonuszeit zu gewähren – ohne Internet.",
            ["settings.unlock.secret"] = "Geheimer Schlüssel",
            ["settings.unlock.uri"] = "Einrichtungslink",
            ["settings.unlock.bonus"] = "Bonusminuten pro Code",
            ["settings.unlock.regenerate"] = "Neues Geheimnis erzeugen",
            ["settings.unlock.configure"] = "Einstellen",
            ["settings.unlock.scan"] = "Mit einer Authenticator-App scannen",
            // Schedule grid (shared by Setup + Settings)
            ["schedule.tool"] = "Malwerkzeug",
            ["schedule.allow"] = "Erlauben",
            ["schedule.block"] = "Blockieren",
            ["schedule.allowed"] = "Erlaubt",
            ["schedule.blocked"] = "Blockiert",
            ["schedule.quickfill"] = "Schnellauswahl",
            ["schedule.preset.allowall"] = "Alle erlauben",
            ["schedule.preset.blockall"] = "Alle blockieren",
            ["schedule.preset.weeknights"] = "Wochentags 16–20 Uhr",
            ["schedule.preset.weekends"] = "Nur Wochenende",
            ["schedule.caption"] = "Jede Zelle ist ein 30-Minuten-Block. Zum schnellen Malen über das Raster ziehen.",
            // Setup advanced options
            ["setup.advanced"] = "Erweitert",
            ["setup.perday.hint"] = "Lege für jeden Tag ein eigenes Limit fest.",
            // Manual update check
            ["settings.update.check"] = "Nach Updates suchen",
            ["settings.update.now"] = "Jetzt aktualisieren",
            ["settings.update.checking"] = "Suche nach Updates…",
            ["settings.update.current"] = "Aktuelle Version: {0}",
            ["settings.update.uptodate"] = "Aktuell (Version {0}).",
            ["settings.update.available"] = "Version {0} ist verfügbar.",
            ["settings.update.downloading"] = "Update wird heruntergeladen…",
            ["settings.update.failed"] = "Update-Prüfung fehlgeschlagen. Bitte später erneut versuchen.",
        },
    };
}
