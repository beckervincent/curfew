using Curfew.Core;

namespace Curfew.Overlay;

/// <summary>Shared mutable state between the mini overlay and the lock screen
/// (one process, one thread).</summary>
internal static class OverlayState
{
    public static SettingsStore Settings = null!;
    public static int Remaining;
    public static IntPtr MiniHwnd;
    public static bool Locked;

    public static void Persist() =>
        Settings.Set($"remaining_time_{DateTime.Now:yyyy-MM-dd}", Remaining.ToString());
}
