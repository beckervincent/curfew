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
    private SetupWindow? _setup;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Environment.GetCommandLineArgs().Contains("--setup"))
        {
            var settings = SettingsStore.Open(CurfewPaths.DatabaseFile, DateOnly.FromDateTime(DateTime.Now));
            _setup = new SetupWindow(settings);
            _setup.Activate();
        }
        else
        {
            _controller = new AppController(DispatcherQueue.GetForCurrentThread());
        }
    }
}
