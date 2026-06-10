using Curfew.Core;
using Microsoft.UI.Dispatching;

namespace Curfew.App;

/// <summary>
/// Owns the per-second countdown and coordinates the mini overlay and the lock
/// screen. When time runs out the lock screen appears; unlocking (optionally
/// with an extension) hides it and resumes the overlay.
/// </summary>
public sealed class AppController
{
    private readonly SettingsStore _settings;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly MiniOverlayWindow _overlay;

    private LockScreenWindow? _lock;
    private int _remaining;

    public AppController(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;

        var today = DateOnly.FromDateTime(DateTime.Now);
        _settings = SettingsStore.Open(CurfewPaths.DatabaseFile, today);

        int? saved = int.TryParse(_settings.Get(RemainingKey(today)), out var s) ? s : null;
        var weekday = TimeMath.MondayBasedWeekday(today);
        _remaining = TimeKeeper.InitialRemaining(saved, _settings.GetDailyLimit(weekday));

        _overlay = new MiniOverlayWindow();
        _overlay.Activate();
        _overlay.ShowTime(_remaining);

        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => OnSecond();
        _timer.Start();

        if (TimeKeeper.IsExhausted(_remaining)) ShowLock();
    }

    private void OnSecond()
    {
        if (_lock is not null) return; // paused while locked

        _remaining = TimeKeeper.Tick(_remaining);
        if (TimeKeeper.ShouldPersist(_remaining)) Persist();
        _overlay.ShowTime(_remaining);

        if (TimeKeeper.IsExhausted(_remaining)) ShowLock();
    }

    private void ShowLock()
    {
        if (_lock is not null) return;

        Persist();
        var message = _settings.Get("blocking_message") ?? "Your screen time limit has been reached.";
        _lock = new LockScreenWindow(_settings, message);
        _lock.Unlocked += OnUnlocked;
        _lock.Activate();
    }

    private void OnUnlocked(int extendMinutes)
    {
        _lock = null;

        if (extendMinutes > 0)
        {
            _remaining = TimeKeeper.Extend(Math.Max(0, _remaining), extendMinutes);
        }
        Persist();
        _overlay.ShowTime(_remaining);
    }

    private void Persist() =>
        _settings.Set(RemainingKey(DateOnly.FromDateTime(DateTime.Now)), _remaining.ToString());

    private static string RemainingKey(DateOnly date) => $"remaining_time_{date:yyyy-MM-dd}";
}
