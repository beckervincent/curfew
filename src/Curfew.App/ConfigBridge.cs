using Curfew.Core;

namespace Curfew.App;

/// <summary>route app CONFIG writes through SYSTEM service over config pipe, since config.db read-only for ordinary users. state writes unaffected (state.db stays child-writable). verified parent passcode captured when gate passed (or set to new PIN during first-run setup) + sent with each write so service can authorise it</summary>
internal static class ConfigBridge
{
    /// <summary>verified parent passcode (or new PIN during setup); null before any gate</summary>
    public static string? Passcode;

    /// <summary>False if any config write since last <see cref="ResetWriteStatus"/> failed</summary>
    public static bool LastWriteOk { get; private set; } = true;

    /// <summary>reset write-status flag before batch of saves</summary>
    public static void ResetWriteStatus() => LastWriteOk = true;

    /// <summary>make <paramref name="settings"/> forward config writes to service. write always reported handled (config.db read-only, no direct fallback); pipe failure recorded in <see cref="LastWriteOk"/> for caller to surface, not thrown</summary>
    public static void Attach(SettingsStore settings) =>
        settings.ConfigWriter = (key, value) =>
        {
            if (!ConfigClient.SetConfig(key, value, Passcode)) LastWriteOk = false;
            return true;
        };
}
