using Curfew.Core;
using Microsoft.UI.Xaml;

namespace Curfew.App;

/// <summary>
/// Application entry point. This executable hosts only the configuration UI and
/// has no long-running background presence — it opens a single window, then the
/// process lives or dies with that window.
/// <para>
/// Activation is selected entirely by command line:
/// <list type="bullet">
///   <item><c>--setup</c>: the first-run wizard.</item>
///   <item><c>--settings</c>: the passcode-gated settings editor.</item>
///   <item>no arguments: nothing to show, so the process exits immediately.</item>
/// </list>
/// </para>
/// <para>
/// The countdown overlay and lock screen live in the separate Win32
/// <c>Curfew.Overlay</c> process; this app never draws them. Any unhandled
/// exception (UI thread, background thread, or unobserved task) is appended to
/// <c>%LOCALAPPDATA%\Curfew\crash.log</c> on a best-effort basis.
/// </para>
/// </summary>
public partial class App : Application
{
    /// <summary>Launches the first-run setup wizard.</summary>
    private const string SetupArgument = "--setup";

    /// <summary>Launches the passcode-gated settings editor.</summary>
    private const string SettingsArgument = "--settings";

    /// <summary>The single top-level window owned by this process, if any.</summary>
    private Window? _window;

    public App()
    {
        InitializeComponent();

        // Funnel every flavour of unhandled exception into the crash log so a
        // failure during configuration is at least diagnosable after the fact.
        UnhandledException += (_, e) => LogCrash(e.Exception);
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
            // Record the failure, then rethrow so the framework reports it and
            // the process terminates rather than lingering in a broken state.
            LogCrash(ex);
            throw;
        }
    }

    /// <summary>Routes startup to the correct UI based on the command line.</summary>
    private void Launch()
    {
        var args = Environment.GetCommandLineArgs();

        if (args.Contains(SetupArgument))
        {
            ShowWindow(new SetupWindow(OpenSettings()));
        }
        else if (args.Contains(SettingsArgument))
        {
            ShowSettingsGated();
        }
        else
        {
            // Launched with no recognised arguments: there is no foreground UI
            // to present, so shut down instead of leaving an invisible process.
            Exit();
        }
    }

    /// <summary>
    /// Opens Settings, but only behind the parental PIN. Launching the Start
    /// Menu / Search shortcut must never bypass the prompt. If no PIN has been
    /// configured yet, fall back to the setup wizard rather than exposing the
    /// (unprotected) settings editor.
    /// </summary>
    private void ShowSettingsGated()
    {
        var settings = OpenSettings();

        if (!settings.HasPasscode)
        {
            ShowWindow(new SetupWindow(settings));
            return;
        }

        var prompt = new PasscodeWindow(settings);
        prompt.Result += verified =>
        {
            if (verified)
            {
                ShowWindow(new SettingsWindow(settings));
            }
            else
            {
                // Wrong PIN or cancelled — close the app without revealing settings.
                Exit();
            }
        };
        ShowWindow(prompt);
    }

    /// <summary>
    /// Makes <paramref name="window"/> the process's top-level window and shows
    /// it. Each window applies its own chrome (rounded corners, Mica) in its
    /// constructor, so no additional styling is needed here.
    /// </summary>
    private void ShowWindow(Window window)
    {
        _window = window;
        window.Activate();
    }

    /// <summary>Opens the shared settings store, scoped to today's date so the
    /// per-day budget rows resolve correctly.</summary>
    private static SettingsStore OpenSettings() =>
        SettingsStore.Open(CurfewPaths.DatabaseFile, DateOnly.FromDateTime(DateTime.Now));

    /// <summary>
    /// Appends an exception to the crash log. Best-effort only: a failure while
    /// logging (e.g. the directory cannot be created) is swallowed so the crash
    /// handler can never itself crash.
    /// </summary>
    private static void LogCrash(Exception? ex)
    {
        if (ex is null) return;

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                CurfewPaths.AppFolderName);
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                $"{DateTime.Now:o}\n{ex}\n\n");
        }
        catch
        {
            // Never let logging crash the crash handler.
        }
    }
}
