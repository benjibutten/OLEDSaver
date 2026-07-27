using System.Threading;
using System.Windows;
using System.Windows.Threading;
using OLEDSaver.Helpers;

using Application = System.Windows.Application;

namespace OLEDSaver;

public partial class App : Application
{
    /// <summary>Starts in the tray with no window. Used by the Windows startup entry.</summary>
    public const string MinimizedArgument = "--minimized";

    /// <summary>Blacks the screen out immediately, then behaves like a normal start.</summary>
    public const string BlackoutArgument = "--blackout";

    /// <summary>
    /// Blacks the screen out, or takes an existing blackout back down. This is the
    /// one a single Stream Deck button wants: <see cref="BlackoutArgument"/> only
    /// ever switches on, so binding it to a button gives no way back from that
    /// same button.
    /// </summary>
    public const string ToggleArgument = "--toggle";

    private const string SingleInstanceMutexName = @"Local\OLEDSaver.SingleInstance";
    private const string ActivateExistingInstanceEventName = @"Local\OLEDSaver.ActivateExistingInstance";
    private const string BlackoutExistingInstanceEventName = @"Local\OLEDSaver.BlackoutExistingInstance";
    private const string ToggleExistingInstanceEventName = @"Local\OLEDSaver.ToggleExistingInstance";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private EventWaitHandle? _blackoutEvent;
    private EventWaitHandle? _toggleEvent;
    private RegisteredWaitHandle? _activateWaitHandle;
    private RegisteredWaitHandle? _blackoutWaitHandle;
    private RegisteredWaitHandle? _toggleWaitHandle;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        SessionEnding += OnSessionEnding;

        bool startHiddenInTray = HasArgument(e.Args, MinimizedArgument);
        bool toggleOnStart = HasArgument(e.Args, ToggleArgument);
        bool blackoutOnStart = HasArgument(e.Args, BlackoutArgument) || toggleOnStart;

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirstInstance);
        _ownsSingleInstanceMutex = isFirstInstance;

        if (!isFirstInstance)
        {
            // A second launch is a remote control for the instance already
            // running: "--blackout" and "--toggle" drive the screen (which is what
            // makes the app bindable from a Stream Deck, a shortcut or another
            // launcher), and a plain launch just brings the window up.
            SignalRunningInstance(
                toggleOnStart ? ToggleExistingInstanceEventName
                : blackoutOnStart ? BlackoutExistingInstanceEventName
                : ActivateExistingInstanceEventName);
            Shutdown();
            return;
        }

        _activateEvent = CreateSignal(ActivateExistingInstanceEventName, static app => app.ActivateMainWindow(), out _activateWaitHandle);
        _blackoutEvent = CreateSignal(BlackoutExistingInstanceEventName, static app => app.BlackoutFromSignal(), out _blackoutWaitHandle);
        _toggleEvent = CreateSignal(ToggleExistingInstanceEventName, static app => app.ToggleFromSignal(), out _toggleWaitHandle);

        base.OnStartup(e);

        var mainWindow = new MainWindow(startHiddenInTray);
        MainWindow = mainWindow;

        if (startHiddenInTray)
            mainWindow.StartHiddenInTray();
        else
            mainWindow.Show();

        // Nothing is blacked out yet in a fresh process, so "--toggle" and
        // "--blackout" mean the same thing on the way up.
        if (blackoutOnStart)
            mainWindow.ShowBlackoutFromCommandLine();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnCurrentDomainUnhandledException;
        SessionEnding -= OnSessionEnding;

        // Belt and braces for any exit path that did not go through the tray or
        // the window — Shutdown() called from anywhere still has to drop the tray
        // icon and flush the debounced save. Teardown is idempotent.
        ShutDownMainWindow();

        _activateWaitHandle?.Unregister(null);
        _activateWaitHandle = null;
        _blackoutWaitHandle?.Unregister(null);
        _blackoutWaitHandle = null;
        _toggleWaitHandle?.Unregister(null);
        _toggleWaitHandle = null;

        _activateEvent?.Dispose();
        _activateEvent = null;
        _blackoutEvent?.Dispose();
        _blackoutEvent = null;
        _toggleEvent?.Dispose();
        _toggleEvent = null;

        if (_ownsSingleInstanceMutex)
            _singleInstanceMutex?.ReleaseMutex();

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        base.OnExit(e);
    }

    private EventWaitHandle CreateSignal(string name, Action<App> handler, out RegisteredWaitHandle? waitHandle)
    {
        var signal = new EventWaitHandle(initialState: false, mode: EventResetMode.AutoReset, name: name);

        waitHandle = ThreadPool.RegisterWaitForSingleObject(
            signal,
            (state, _) =>
            {
                if (state is App app && !app.Dispatcher.HasShutdownStarted)
                    app.Dispatcher.BeginInvoke(() => handler(app));
            },
            this,
            Timeout.Infinite,
            executeOnlyOnce: false);

        return signal;
    }

    private static void SignalRunningInstance(string eventName)
    {
        try
        {
            using EventWaitHandle signal = EventWaitHandle.OpenExisting(eventName);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first instance is still starting and has not created its signals yet.
        }
    }

    private static bool HasArgument(string[] args, string name) =>
        args.Contains(name, StringComparer.OrdinalIgnoreCase);

    private void ActivateMainWindow()
    {
        if (MainWindow is MainWindow mainWindow)
            mainWindow.ShowAndActivate();
    }

    private void BlackoutFromSignal()
    {
        if (MainWindow is MainWindow mainWindow)
            mainWindow.ShowBlackoutFromCommandLine();
    }

    private void ToggleFromSignal()
    {
        if (MainWindow is MainWindow mainWindow)
            mainWindow.ToggleBlackoutFromCommandLine();
    }

    /// <summary>
    /// Windows is logging off or shutting down. The process is about to go away
    /// without OnClosed ever running, so the tray icon and the debounced save have
    /// to be dealt with here or the icon is left as a ghost in the notification
    /// area and the last settings change is lost.
    /// </summary>
    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        AppDiagnostics.Info($"Session ending ({e.ReasonSessionEnding}); shutting down.");
        ShutDownMainWindow();
    }

    private void ShutDownMainWindow()
    {
        if (MainWindow is MainWindow mainWindow)
            mainWindow.PrepareForShutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppDiagnostics.Error("Unhandled dispatcher exception.", e.Exception);
    }

    private void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            AppDiagnostics.Error(
                e.IsTerminating ? "Unhandled terminating exception." : "Unhandled exception.",
                exception);
            return;
        }

        AppDiagnostics.Error($"Unhandled non-exception object: {e.ExceptionObject}");
    }
}
