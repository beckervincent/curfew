using Curfew.Core;

namespace Curfew.App;

/// <summary>
/// Routes the app's CONFIG writes through the SYSTEM service over the config pipe,
/// since config.db is read-only for ordinary users. State writes are unaffected
/// (state.db stays child-writable). The verified parent passcode is captured when
/// a gate is passed (or set to the new PIN during first-run setup) and sent with
/// each write so the service can authorise it.
/// </summary>
internal static class ConfigBridge
{
    /// <summary>The verified parent passcode (or the new PIN during setup); null before any gate.</summary>
    public static string? Passcode;

    /// <summary>False if any config write since the last <see cref="ResetWriteStatus"/> failed.</summary>
    public static bool LastWriteOk { get; private set; } = true;

    /// <summary>Resets the write-status flag before a batch of saves.</summary>
    public static void ResetWriteStatus() => LastWriteOk = true;

    /// <summary>
    /// Makes <paramref name="settings"/> forward its config writes to the service.
    /// The write is always reported as handled (config.db is read-only, so there is
    /// no direct fallback); a pipe failure is recorded in <see cref="LastWriteOk"/>
    /// for the caller to surface rather than thrown.
    /// </summary>
    public static void Attach(SettingsStore settings) =>
        settings.ConfigWriter = (key, value) =>
        {
            if (!ConfigClient.SetConfig(key, value, Passcode)) LastWriteOk = false;
            return true;
        };
}
