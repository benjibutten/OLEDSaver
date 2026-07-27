using System.Windows;
using System.Windows.Input;
using OLEDSaver.Helpers;
using OLEDSaver.Services;

using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace OLEDSaver.Views;

/// <summary>
/// One true-black window covering one monitor.
///
/// The window is deliberately inert: it draws black, swallows clicks so nothing
/// invisible can be clicked by accident, and forwards key and button events to
/// the controller. It never decides for itself when to close — dismissal is
/// evaluated centrally, because the authoritative input stream is the global
/// raw-input sink on the main window, not these windows' focus.
/// </summary>
public partial class BlackoutWindow : Window
{
    public BlackoutWindow(string hintText, bool showHint)
    {
        InitializeComponent();

        HintText.Text = hintText;
        HintText.Visibility = showHint ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Raised for key events observed while this window has focus.</summary>
    public event EventHandler<BlackoutKeyEventArgs>? KeyObserved;

    /// <summary>Raised when a mouse button is pressed on the overlay.</summary>
    public event EventHandler? MouseButtonObserved;

    // Just enough of a window to sit unambiguously inside one monitor before
    // Windows takes over the sizing.
    private const int PlacementProbeSize = 200;

    /// <summary>
    /// Covers the given monitor exactly. Called after the window is shown, since
    /// the first step moves the native window.
    /// </summary>
    public void CoverDisplay(DisplayInfo display)
    {
        // Step 1: land on the target monitor in raw pixels. Picking the monitor
        // this way is exact on a mixed-DPI desktop, where WPF's own Left/Top are
        // scaled by whichever monitor the window happens to be on.
        NativeInterop.PlaceWindowAtDeviceBounds(this, display.X, display.Y, PlacementProbeSize, PlacementProbeSize);

        // Step 2: let Windows size it to that whole monitor. A borderless window
        // maximizes over the taskbar, and WPF lays the content out at the
        // monitor's own scale factor. Stretching the window with SetWindowPos
        // instead leaves WPF's layout at the previous size, and the overlay then
        // covers the screen while painting almost none of it.
        WindowState = WindowState.Maximized;
    }

    private void Blackout_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Alt-qualified presses arrive as Key.System with the real key in SystemKey.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        KeyObserved?.Invoke(this, new BlackoutKeyEventArgs((uint)KeyInterop.VirtualKeyFromKey(key), isKeyDown: true));

        // Swallowed so the overlay never passes a keystroke to whatever is
        // underneath it while the screen is black.
        e.Handled = true;
    }

    private void Blackout_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        KeyObserved?.Invoke(this, new BlackoutKeyEventArgs((uint)KeyInterop.VirtualKeyFromKey(key), isKeyDown: false));
        e.Handled = true;
    }

    private void Blackout_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        MouseButtonObserved?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}

public sealed class BlackoutKeyEventArgs : EventArgs
{
    public BlackoutKeyEventArgs(uint virtualKey, bool isKeyDown)
    {
        VirtualKey = virtualKey;
        IsKeyDown = isKeyDown;
    }

    public uint VirtualKey { get; }

    public bool IsKeyDown { get; }
}
