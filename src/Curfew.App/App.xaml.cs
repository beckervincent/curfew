using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Curfew.App;

/// <summary>Application entry point. Hands off to the controller, which owns the
/// countdown, the mini overlay and the lock screen.</summary>
public partial class App : Application
{
    private AppController? _controller;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _controller = new AppController(DispatcherQueue.GetForCurrentThread());
    }
}
