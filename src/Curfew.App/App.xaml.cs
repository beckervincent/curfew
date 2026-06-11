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

    /// <summary>Launches the full-screen WinUI lock surface (driven by the overlay).</summary>
    private const string LockArgument = "--lock";

    /// <summary>Prefix for a passcode-gated tray command, e.g. <c>--tray=extend15</c>.</summary>
    private const string TrayArgumentPrefix = "--tray=";

    /// <summary>Tray commands accepted from the overlay (validated before being written through).</summary>
    private static readonly string[] AllowedTrayCommands = { "extend15", "extend45", "pause", "resume", "quit" };

    /// <summary>The single top-level window owned by this process, if any.</summary>
    private Window? _window;

    public App()
    {
        // Harden the DLL search path / image-load policy. Extension points stay
        // enabled here so WinUI text input (IMEs) keeps working.
        Curfew.Core.Security.ProcessHardening.Apply(disableExtensionPoints: false);

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

        if (args.Contains(LockArgument))
        {
            // The lock surface is launched by the overlay while a session is
            // blocked. It is intentionally NOT passcode-gated to open — the lock IS
            // the gate; the passcode is verified inside it to dismiss it.
            ShowLock();
        }
        else if (args.Contains(SetupArgument))
        {
            ShowSetupGated();
        }
        else if (args.Contains(SettingsArgument))
        {
            ShowSettingsGated();
        }
        else if (TrayCommand(args) is { } command)
        {
            RunTrayCommandGated(command);
        }
        else
        {
            // Launched with no recognised arguments: there is no foreground UI
            // to present, so shut down instead of leaving an invisible process.
            Exit();
        }
    }

    /// <summary>Builds and shows the full-screen WinUI lock surface.</summary>
    private void ShowLock()
    {
        var controller = new LockController(OpenSettings());
        _window = controller.Start();
    }

    /// <summary>Extracts a validated tray command from the command line, or null if none.</summary>
    private static string? TrayCommand(IEnumerable<string> args)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith(TrayArgumentPrefix, StringComparison.Ordinal));
        var command = arg?[TrayArgumentPrefix.Length..];
        return command is not null && AllowedTrayCommands.Contains(command) ? command : null;
    }

    /// <summary>
    /// Verifies the parent passcode, then records the tray command for the overlay
    /// to pick up and apply. The overlay has no passcode UI of its own, so this is
    /// where extend/pause/quit are authorised. Closes as soon as it is done.
    /// </summary>
    private void RunTrayCommandGated(string command)
    {
        var settings = OpenSettings();
        if (!settings.HasPasscode) { Exit(); return; }

        var prompt = new PasscodeWindow(settings);
        prompt.Result += verified =>
        {
            if (verified)
            {
                // Write the timestamp BEFORE the command. The overlay polls for
                // tray_command and then reads tray_command_at to reject stale ones;
                // if the command landed first the overlay could pair a fresh command
                // with a leftover old timestamp and silently drop it.
                settings.Set("tray_command_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
                settings.Set("tray_command", command);
            }
            Exit();
        };
        ShowWindow(prompt);
    }

    /// <summary>
    /// Opens the first-run wizard, but only behind the parental PIN once one
    /// exists. The wizard can rewrite limits, the schedule AND the passcode
    /// itself, so an unauthenticated <c>--setup</c> relaunch must never be able
    /// to reset parental controls. Only a genuine first run (no passcode set
    /// yet) is allowed straight through, so the very first passcode can be set.
    /// </summary>
    private void ShowSetupGated()
    {
        var settings = OpenSettings();

        if (!settings.HasPasscode)
        {
            ShowWindow(new SetupWindow(settings));
            return;
        }

        RequirePasscode(settings, () => ShowWindow(new SetupWindow(settings)));
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

        RequirePasscode(settings, () => ShowWindow(new SettingsWindow(settings)));
    }

    /// <summary>
    /// Shows the passcode prompt and runs <paramref name="onVerified"/> only on a
    /// correct passcode. A wrong or cancelled prompt closes the app without
    /// revealing anything. Shared by every passcode-gated entry point so none can
    /// drift out of sync and accidentally open unauthenticated.
    /// </summary>
    private void RequirePasscode(SettingsStore settings, Action onVerified)
    {
        var prompt = new PasscodeWindow(settings);
        prompt.Result += verified =>
        {
            if (verified)
            {
                onVerified();
            }
            else
            {
                // Wrong PIN or cancelled — close the app without revealing anything.
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
