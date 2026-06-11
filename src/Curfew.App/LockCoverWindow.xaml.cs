using Microsoft.UI.Xaml;

namespace Curfew.App;

/// <summary>
/// Full-screen cover shown on every non-primary monitor while the lock is up: a
/// large lock glyph and a single "Move lock here" button. The button is a
/// failover — if Windows reports the wrong primary display, the parent can pull
/// the interactive lock card onto whichever monitor they can actually see.
/// </summary>
public sealed partial class LockCoverWindow : Window
{
    /// <summary>Raised when the user asks for the primary lock card to move to this display.</summary>
    public event Action? MoveHereRequested;

    public LockCoverWindow() => InitializeComponent();

    private void OnMoveHere(object sender, RoutedEventArgs e) => MoveHereRequested?.Invoke();
}
