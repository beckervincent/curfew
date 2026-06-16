using System.Runtime.InteropServices;
using Curfew.Core;
using Microsoft.UI.Xaml;

namespace Curfew.App;

/// <summary>app entry point. hosts only config UI, no background presence — opens single window, process dies with it</summary>
/// <remarks>
/// activation picked by command line:
/// <list type="bullet">
///   <item><c>--setup</c>: first-run wizard</item>
///   <item><c>--settings</c>: passcode-gated settings editor</item>
///   <item>no args: nothing to show, process exits immediately</item>
/// </list>
/// countdown overlay + lock screen live in separate Win32 <c>Curfew.Overlay</c> process; this app never draws them. any unhandled exception (UI thread, background thread, unobserved task) appended to <c>%LOCALAPPDATA%\Curfew\crash.log</c> best-effort
/// </remarks>
public partial class App : Application
{
    /// <summary>launch first-run setup wizard</summary>
    private const string SetupArgument = "--setup";

    /// <summary>launch passcode-gated settings editor</summary>
    private const string SettingsArgument = "--settings";

    /// <summary>launch full-screen WinUI lock surface (driven by overlay)</summary>
    private const string LockArgument = "--lock";

    /// <summary>prefix for passcode-gated tray command, e.g. <c>--tray=extend15</c></summary>
    private const string TrayArgumentPrefix = "--tray=";

    /// <summary>flags that print usage and exit (<c>--help</c>, <c>-h</c>, <c>/?</c>)</summary>
    private static readonly string[] HelpArguments = { "--help", "-h", "/?" };

    /// <summary>tray commands accepted from overlay (validated before write-through)</summary>
    private static readonly string[] AllowedTrayCommands = { "extend15", "extend45", "pause", "resume", "quit" };

    /// <summary>single top-level window owned by this process, if any</summary>
    private Window? _window;

    public App()
    {
        // harden DLL search path / image-load policy. extension points stay enabled so WinUI text input (IMEs) keeps working
        Curfew.Core.Security.ProcessHardening.Apply(disableExtensionPoints: false);

        InitializeComponent();

        // funnel every unhandled exception into crash log so config failure is diagnosable after the fact
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
            // record failure, then rethrow so framework reports it + process terminates instead of lingering broken
            LogCrash(ex);
            throw;
        }
    }

    /// <summary>route startup to correct UI based on command line</summary>
    private void Launch()
    {
        var args = Environment.GetCommandLineArgs();

        if (args.Any(a => HelpArguments.Contains(a, StringComparer.OrdinalIgnoreCase)))
        {
            ShowHelp();
        }
        else if (args.Contains(LockArgument))
        {
            // lock surface launched by overlay while session blocked. NOT passcode-gated to open — lock IS the gate; passcode verified inside it to dismiss
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
            // no recognised args: no foreground UI to present, so shut down instead of leaving invisible
            // process. hard-exit — Application.Exit() does not terminate a process that never activated a
            // window, which would leave exactly the invisible process this branch means to avoid
            Environment.Exit(0);
        }
    }

    /// <summary>print command-line usage to the parent console, then exit. as a GUI (WinExe) app this has
    /// no console of its own, so it attaches to the console that launched it; double-clicked or launched by
    /// the overlay there is none, so the writes are harmless no-ops and it just exits</summary>
    private void ShowHelp()
    {
        try
        {
            AttachConsole(AttachParentProcess);
            Console.WriteLine();
            Console.WriteLine("Curfew - parental screen-time controls");
            Console.WriteLine();
            Console.WriteLine("Usage: Curfew.App.exe [option]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --setup           Run the first-run setup wizard (gated by the parent passcode once set).");
            Console.WriteLine("  --settings        Open the passcode-gated settings editor.");
            Console.WriteLine("  --lock            Show the full-screen lock surface (normally launched by the overlay).");
            Console.WriteLine("  --tray=<command>  Run a passcode-gated tray action: extend15, extend45, pause, resume, quit.");
            Console.WriteLine("  --help, -h, /?    Show this help and exit.");
            Console.WriteLine();
            Console.WriteLine("To script settings non-interactively (PIN-gated), use the headless 'curfew-cli.exe'.");
            Console.WriteLine();
            Console.WriteLine("With no option the app exits immediately. Enforcement runs in the Curfew service and");
            Console.WriteLine("overlay; this executable is only the configuration UI.");
            Console.Out.Flush();
        }
        catch
        {
            // best effort: no parent console attached, or the write failed
        }
        finally
        {
            // hard-exit: Application.Exit() does not terminate a process that never
            // activated a window (it leaves the app running with no UI), so a console
            // flag like --help would hang. Environment.Exit ends it deterministically
            // after the usage text has flushed.
            Environment.Exit(0);
        }
    }

    /// <summary>ATTACH_PARENT_PROCESS: attach to the console of the launching process.</summary>
    private const uint AttachParentProcess = unchecked((uint)-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);

    /// <summary>build + show full-screen WinUI lock surface</summary>
    private void ShowLock()
    {
        var settings = OpenSettings();
        // scope to THIS session's user, exactly as the overlay does. per-SID config
        // (unlock_secret, blocking_message, ...) must resolve to the same user the
        // overlay reads, or the lock reads the unscoped/global value: an offline
        // unlock code verifies against a per-user secret in the overlay's redeem path
        // but the lock's own pre-check read the global one and rejected every code,
        // so a valid ticket never reached the overlay to be granted
        settings.UserSid = CurrentUserSid();
        var controller = new LockController(settings);
        _window = controller.Start();
    }

    /// <summary>SID of the Windows session this app runs in, or null on failure (no per-user scoping).</summary>
    private static string? CurrentUserSid()
    {
        try { return System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value; }
        catch { return null; }
    }

    /// <summary>extract validated tray command from command line, or null if none</summary>
    private static string? TrayCommand(IEnumerable<string> args)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith(TrayArgumentPrefix, StringComparison.Ordinal));
        var command = arg?[TrayArgumentPrefix.Length..];
        return command is not null && AllowedTrayCommands.Contains(command) ? command : null;
    }

    /// <summary>verify parent passcode, then record tray command for overlay to pick up + apply. overlay has no passcode UI of its own, so extend/pause/quit authorised here. closes as soon as done</summary>
    private void RunTrayCommandGated(string command)
    {
        var settings = OpenSettings();
        if (!settings.HasPasscode) { Exit(); return; }

        var prompt = new PasscodeWindow(settings);
        prompt.Result += verified =>
        {
            if (verified)
            {
                // write timestamp BEFORE command. overlay polls tray_command then reads tray_command_at to reject stale; if command landed first overlay could pair fresh command with leftover old timestamp + silently drop it
                settings.Set("tray_command_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
                settings.Set("tray_command", command);
            }
            Exit();
        };
        ShowWindow(prompt);
    }

    /// <summary>open first-run wizard, but only behind parental PIN once one exists. wizard can rewrite limits, schedule AND passcode itself, so unauthenticated <c>--setup</c> relaunch must never reset parental controls. only genuine first run (no passcode yet) passes straight through, so very first passcode can be set</summary>
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

    /// <summary>open Settings, but only behind parental PIN. Start Menu / Search shortcut must never bypass prompt. no PIN yet -&gt; fall back to setup wizard instead of exposing unprotected settings editor</summary>
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

    /// <summary>show passcode prompt + run <paramref name="onVerified"/> only on correct passcode. wrong/cancelled prompt closes app without revealing anything. shared by every passcode-gated entry point so none drift out of sync + accidentally open unauthenticated</summary>
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
                // wrong PIN or cancelled — close app without revealing anything
                Exit();
            }
        };
        ShowWindow(prompt);
    }

    /// <summary>make <paramref name="window"/> the process top-level window + show it. each window applies own chrome (rounded corners, Mica) in its ctor, so no extra styling needed here</summary>
    private void ShowWindow(Window window)
    {
        _window = window;
        window.Activate();
    }

    /// <summary>open shared settings store, scoped to today's date so per-day budget rows resolve correctly</summary>
    private static SettingsStore OpenSettings() =>
        CurfewPaths.OpenSettings(DateOnly.FromDateTime(DateTime.Now));

    /// <summary>append exception to crash log. best-effort only: logging failure (e.g. dir cant be created) swallowed so crash handler never itself crashes</summary>
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
            // never let logging crash the crash handler
        }
    }
}
