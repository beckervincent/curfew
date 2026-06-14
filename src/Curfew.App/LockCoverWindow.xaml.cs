using Microsoft.UI.Xaml;

namespace Curfew.App;

/// <summary>full-screen cover on every non-primary monitor while lock up: large lock glyph + single "Move lock here" button. button is failover — if Windows reports wrong primary display, parent can pull interactive lock card onto whichever monitor they can actually see</summary>
public sealed partial class LockCoverWindow : Window
{
    /// <summary>raised when user asks for primary lock card to move to this display</summary>
    public event Action? MoveHereRequested;

    public LockCoverWindow() => InitializeComponent();

    private void OnMoveHere(object sender, RoutedEventArgs e) => MoveHereRequested?.Invoke();
}
