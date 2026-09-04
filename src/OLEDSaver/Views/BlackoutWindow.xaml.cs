using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
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
///
/// It also outlives a single blackout. <see cref="BlackoutController"/> parks it
/// off-screen and hides it instead of closing it, so the next blackout is a
/// ShowWindow call on a window that already exists, already has its render
/// surface at the right size, and is already positioned over the right monitor.
/// </summary>
public partial class BlackoutWindow : Window
{
    /// <summary>
    /// Far outside any possible desktop rectangle. Windows clamps a window to
    /// roughly -32000 in either axis, and no monitor arrangement reaches it, so a
    /// window parked here is invisible without being hidden.
    /// </summary>
    private const int ParkedX = -32000;
    private const int ParkedY = -32000;

    // Just enough of a window to sit unambiguously inside one monitor before
    // Windows takes over the sizing.
    private const int PlacementProbeSize = 200;

    private readonly Storyboard _hintFade;

    public BlackoutWindow()
    {
        InitializeComponent();

        _hintFade = (Storyboard)Resources["HintFade"];
    }

    /// <summary>Raised for key events observed while this window has focus.</summary>
    public event EventHandler<BlackoutKeyEventArgs>? KeyObserved;

    /// <summary>Raised when a mouse button is pressed on the overlay.</summary>
    public event EventHandler? MouseButtonObserved;

    /// <summary>
    /// The monitor this window is currently sized and positioned over, or null
    /// while it is parked. Compared against the next blackout's target so an
    /// unchanged layout skips the move-and-maximize entirely.
    /// </summary>
    public DisplayInfo? CoveredDisplay { get; private set; }

    /// <summary>
    /// True when the window already covers exactly this rectangle and needs
    /// nothing but a <see cref="Window.Show"/> to become a blackout again.
    /// </summary>
    public bool IsCovering(DisplayInfo display) =>
        CoveredDisplay is { } covered
        && covered.X == display.X
        && covered.Y == display.Y
        && covered.Width == display.Width
        && covered.Height == display.Height
        && WindowState == WindowState.Maximized;

    /// <summary>
    /// Puts the content into the state this blackout wants before the window goes
    /// up. Restarting the fade from here rather than from a Loaded trigger is what
    /// makes a reused window behave like a new one: Loaded fires once per window,
    /// so the second blackout would otherwise show no hint at all.
    /// </summary>
    public void PrepareForShow(string hintText, bool showHint)
    {
        _hintFade.Stop(this);

        HintText.Text = hintText;
        HintText.Visibility = showHint ? Visibility.Visible : Visibility.Collapsed;
        HintText.Opacity = 0;

        if (showHint)
            _hintFade.Begin(this, isControllable: true);
    }

    /// <summary>Stops the fade so a hidden window is not animating in the background.</summary>
    public void StopHint() => _hintFade.Stop(this);

    /// <summary>
    /// Shows the window purely to pay WPF's one-time costs — the handle, the
    /// render thread, the surface, the glyph typeface — without taking focus from
    /// whatever the user is doing. Only ever called on a window parked off every
    /// monitor.
    ///
    /// ShowActivated is turned off for this one show and straight back on,
    /// because WPF refuses outright to show a window that is both maximized and
    /// non-activating, and maximized is what every real blackout is.
    /// </summary>
    public void ShowForPrewarm()
    {
        ShowActivated = false;

        try
        {
            Show();
        }
        finally
        {
            ShowActivated = true;
        }
    }

    /// <summary>
    /// Covers the given monitor exactly. Called while the window is visible, since
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

        CoveredDisplay = display;
    }

    /// <summary>
    /// Moves the window off every monitor, leaving it hidden if it already is.
    ///
    /// <paramref name="width"/> and <paramref name="height"/> are the size the
    /// window is expected to be shown at next. Parking at that size is not
    /// cosmetic: it means the maximize on the way back up is a move rather than a
    /// resize, and WPF keeps the render surface it already allocated instead of
    /// building a new one the size of a 4K monitor.
    /// </summary>
    public void ParkOffScreen(int width, int height)
    {
        WindowState = WindowState.Normal;
        CoveredDisplay = null;

        if (NativeInterop.TryPlaceHiddenWindowAtDeviceBounds(this, ParkedX, ParkedY, width, height))
            return;

        // No handle yet — this window has never been shown. WPF's own properties
        // are all there is to set, and they decide where it first appears.
        Left = ParkedX;
        Top = ParkedY;
        Width = width;
        Height = height;
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
