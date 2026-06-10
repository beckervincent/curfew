using Curfew.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Curfew.App;

/// <summary>
/// First-run setup wizard (Fluent + Mica). The parent always makes explicit
/// choices here: an administrator PIN, whether a daily hour limit is enforced,
/// whether a weekly schedule is enforced, the content-filter level, DNS-over-HTTPS
/// blocking and Time Manipulation Guarding.
/// </summary>
/// <remarks>
/// Pressing <c>Continue</c> validates the PIN, writes every choice to the
/// <see cref="SettingsStore"/>, marks setup as complete and closes the window.
/// Nothing is persisted until validation passes, so cancelling (closing the
/// window) leaves the store untouched.
/// </remarks>
public sealed partial class SetupWindow : Window
{
    /// <summary>Administrator PINs are exactly this many decimal digits.</summary>
    private const int PinLength = 4;

    /// <summary>Hours-per-day applied when the <c>NumberBox</c> value is blank/NaN.</summary>
    private const double DefaultHoursPerDay = 2.0;

    /// <summary>Inclusive bounds for the daily hour budget (matches the XAML NumberBox).</summary>
    private const double MinHoursPerDay = 0.0;
    private const double MaxHoursPerDay = 24.0;

    private const int MinutesPerHour = 60;

    /// <summary>Initial client size of the wizard, in DIPs.</summary>
    private static readonly Windows.Graphics.SizeInt32 WindowSize = new(560, 720);

    private readonly SettingsStore _settings;

    /// <summary>Creates the wizard bound to the store its choices are written to.</summary>
    /// <param name="settings">Destination for every configured value. Never <c>null</c>.</param>
    public SetupWindow(SettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        InitializeComponent();
        ApplyWindowChrome();
    }

    /// <summary>Applies the title, Mica backdrop, custom title bar and rounded corners.</summary>
    private void ApplyWindowChrome()
    {
        AppWindow.Resize(WindowSize);
        WindowEffects.Apply(this, "Set up Curfew", TitleBar);
    }

    /// <summary>
    /// Handles the <c>Continue</c> button: validates input, then commits all
    /// settings and closes. Bound from XAML (<c>Click="OnContinue"</c>).
    /// </summary>
    private void OnContinue(object sender, RoutedEventArgs e)
    {
        if (!TryReadValidatedPin(out var pin))
            return;

        ClearError();
        PersistConfiguration(pin);
        Close();
    }

    /// <summary>
    /// Reads and validates the PIN pair. On failure it surfaces an inline error
    /// and returns <c>false</c>; on success <paramref name="pin"/> holds the
    /// confirmed 4-digit value.
    /// </summary>
    private bool TryReadValidatedPin(out string pin)
    {
        pin = PinBox.Password;

        if (pin.Length != PinLength || !pin.All(char.IsAsciiDigit))
        {
            ShowError($"PIN must be exactly {PinLength} digits.");
            return false;
        }

        if (pin != ConfirmBox.Password)
        {
            ShowError("PINs do not match.");
            return false;
        }

        return true;
    }

    /// <summary>Writes every wizard choice to the store and marks setup complete.</summary>
    private void PersistConfiguration(string pin)
    {
        _settings.Set("passcode", pin);
        _settings.Set("limit_enabled", ToFlag(LimitEnabled.IsOn));
        _settings.Set("schedule_enabled", ToFlag(ScheduleEnabled.IsOn));

        ApplyDailyLimitToEveryWeekday();

        _settings.Set("dns_filter_mode", ContentFilter.ToSetting(SelectedFilterMode()));
        _settings.Set("block_doh_bypass", ToFlag(BlockDoh.IsOn));
        _settings.Set("time_guard_enabled", ToFlag(TimeGuard.IsOn));
        _settings.Set("setup_complete", "1");
    }

    /// <summary>
    /// Applies the single chosen hours/day figure to every weekday. The parent
    /// can fine-tune individual days later in Settings.
    /// </summary>
    private void ApplyDailyLimitToEveryWeekday()
    {
        var minutes = ChosenDailyMinutes();
        var value = minutes.ToString();
        foreach (var key in SettingsStore.WeekdayKeys)
            _settings.Set(key, value);
    }

    /// <summary>The daily budget from the NumberBox, clamped to range and rounded to whole minutes.</summary>
    private int ChosenDailyMinutes()
    {
        var hours = HoursPerDay.Value;
        if (double.IsNaN(hours))
            hours = DefaultHoursPerDay;

        hours = Math.Clamp(hours, MinHoursPerDay, MaxHoursPerDay);
        return (int)Math.Round(hours * MinutesPerHour);
    }

    /// <summary>Maps the content-filter radio group to a <see cref="FilterMode"/>.</summary>
    private FilterMode SelectedFilterMode()
    {
        if (FilterMalware.IsChecked == true) return FilterMode.Malware;
        if (FilterFamily.IsChecked == true) return FilterMode.Family;
        return FilterMode.Off;
    }

    /// <summary>Renders a boolean as the store's "1"/"0" flag convention.</summary>
    private static string ToFlag(bool on) => on ? "1" : "0";

    /// <summary>Shows an inline validation message beneath the form.</summary>
    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>Hides any previously shown validation message.</summary>
    private void ClearError()
    {
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
    }
}
