using Microsoft.UI.Xaml;

namespace Curfew.App;

/// <summary>Application entry point. Window wiring grows in later milestones.</summary>
public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
