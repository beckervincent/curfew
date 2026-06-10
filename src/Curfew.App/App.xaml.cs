using Curfew.Core;
using Microsoft.UI.Xaml;

namespace Curfew.App;

/// <summary>Application entry point. Opens the settings store and shows the
/// mini countdown overlay. Tray icon, dialogs and lock screen follow in later
/// milestones.</summary>
public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var settings = SettingsStore.Open(CurfewPaths.DatabaseFile, today);

        var key = $"remaining_time_{today:yyyy-MM-dd}";
        int? saved = int.TryParse(settings.Get(key), out var s) ? s : null;
        var weekday = TimeMath.MondayBasedWeekday(today);
        var remaining = TimeKeeper.InitialRemaining(saved, settings.GetDailyLimit(weekday));

        _window = new MiniOverlayWindow(settings, remaining);
        _window.Activate();
    }
}
