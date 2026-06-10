using Curfew.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Curfew.App;

/// <summary>
/// Application entry point. With <c>--setup</c> it shows the first-run wizard;
/// otherwise it hands off to the controller, which owns the countdown, the mini
/// overlay and the lock screen.
/// </summary>
public partial class App : Application
{
    private AppController? _controller;
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var cmdline = Environment.GetCommandLineArgs();

        if (cmdline.Contains("--setup"))
        {
            _window = new SetupWindow(OpenSettings());
            _window.Activate();
        }
        else if (cmdline.Contains("--settings"))
        {
            _window = new SettingsWindow(OpenSettings());
            _window.Activate();
        }
        else
        {
            _controller = new AppController(DispatcherQueue.GetForCurrentThread());
        }
    }

    private static SettingsStore OpenSettings() =>
        SettingsStore.Open(CurfewPaths.DatabaseFile, DateOnly.FromDateTime(DateTime.Now));
}
