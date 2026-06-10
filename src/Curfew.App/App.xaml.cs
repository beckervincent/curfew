using Curfew.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Curfew.App;

/// <summary>
/// Application entry point.
/// - no args: the controller (countdown, mini overlay, lock screen)
/// - --setup: first-run wizard
/// - --settings: passcode-gated settings editor
/// Unhandled exceptions are written to %LOCALAPPDATA%\Curfew\crash.log.
/// </summary>
public partial class App : Application
{
    private AppController? _controller;
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => { LogCrash(e.Exception); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => LogCrash(e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Launch();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            throw;
        }
    }

    private void Launch()
    {
        var cmdline = Environment.GetCommandLineArgs();

        if (cmdline.Contains("--setup"))
        {
            _window = new SetupWindow(OpenSettings());
            WindowEffects.RoundCorners(_window);
            _window.Activate();
        }
        else if (cmdline.Contains("--settings"))
        {
            ShowSettingsGated();
        }
        else
        {
            _controller = new AppController(DispatcherQueue.GetForCurrentThread());
        }
    }

    /// <summary>Require the PIN before opening Settings (Start Menu / Search
    /// must not bypass it).</summary>
    private void ShowSettingsGated()
    {
        var settings = OpenSettings();

        // No PIN configured yet: run setup instead of exposing settings.
        if (!settings.HasPasscode)
        {
            _window = new SetupWindow(settings);
            WindowEffects.RoundCorners(_window);
            _window.Activate();
            return;
        }

        var prompt = new PasscodeWindow(settings);
        prompt.Result += verified =>
        {
            if (verified)
            {
                var win = new SettingsWindow(settings);
                WindowEffects.RoundCorners(win);
                _window = win;
                win.Activate();
            }
            else
            {
                Exit();
            }
        };
        _window = prompt;
        prompt.Activate();
    }

    private static SettingsStore OpenSettings() =>
        SettingsStore.Open(CurfewPaths.DatabaseFile, DateOnly.FromDateTime(DateTime.Now));

    private static void LogCrash(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Curfew");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"{DateTime.Now:o}\n{ex}\n\n");
        }
        catch
        {
            // Never let logging crash the crash handler.
        }
    }
}
