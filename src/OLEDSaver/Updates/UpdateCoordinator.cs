using System.Windows;
using OLEDSaver.Helpers;

using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace OLEDSaver.Updates;

/// <summary>
/// The one place that decides what the user sees around an update. An automatic check
/// stays silent unless there is something to install — this app spends its life in the
/// tray, and a dialog that only says "you are up to date" is pure interruption.
/// </summary>
internal static class UpdateCoordinator
{
    private const string Caption = "OLED Saver update";

    private static readonly GitHubUpdateService Service = new();
    private static int _checkInProgress;

    public static async Task CheckAsync(Window owner, bool manual)
    {
        Version? currentVersion = AppVersion.Current;

        // A local build carries the placeholder 1.0.0.0, so every release looks newer
        // than it. Installing a release over a working copy of the source tree is not
        // what anyone asked for.
        if (currentVersion is null || currentVersion.Major < 2000)
        {
            if (manual)
            {
                MessageBox.Show(
                    owner,
                    "Updates can only be checked for in released builds.",
                    Caption,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return;
        }

        if (Interlocked.Exchange(ref _checkInProgress, 1) != 0)
            return;

        try
        {
            UpdateInfo? update = await Service.CheckAsync(currentVersion, manual);
            if (update is null)
            {
                if (manual)
                {
                    MessageBox.Show(
                        owner,
                        "You already have the latest version.",
                        Caption,
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                return;
            }

            AppDiagnostics.Info($"Update available: {update.TagName} (installed {AppVersion.DisplayText}).");

            var answer = MessageBox.Show(
                owner,
                $"OLED Saver {update.TagName} is available. You have {AppVersion.DisplayText}.\n\n"
                    + "Download and install it now? The app closes and restarts automatically.\n\n"
                    + "Windows may ask for approval, or show a SmartScreen warning for a newly "
                    + "published or unsigned build.",
                Caption,
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes)
                return;

            var progressWindow = new UpdateProgressWindow(owner);
            owner.IsEnabled = false;
            progressWindow.Show();

            try
            {
                var progress = new Progress<UpdateProgress>(progressWindow.Report);
                await Service.LaunchInstallerAsync(update, StartedIntoTray(), progress);

                // From here the copied updater is waiting for this process to exit before
                // it can touch a single file, so the shutdown has to be the ordinary one
                // that drops the tray icon and flushes the debounced settings save.
                if (Application.Current.MainWindow is MainWindow mainWindow)
                    mainWindow.ExitForUpdate();
                else
                    Application.Current.Shutdown();
            }
            finally
            {
                progressWindow.Close();
                owner.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error("The update check failed.", ex);

            if (manual)
            {
                MessageBox.Show(
                    owner,
                    $"Could not check for or prepare the update.\n\n{ex.Message}",
                    Caption,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _checkInProgress, 0);
        }
    }

    /// <summary>
    /// Whether this process was launched into the tray — which is how the "start with
    /// Windows" entry launches it. The window is always up by the time an update is
    /// accepted (the dialogs need a visible owner), so the launch argument is the only
    /// thing left that says whether this copy is a background app or one the user
    /// opened themselves. Only that one argument is carried over: the rest of the
    /// command line may be a leftover --update-cleanup, or a --blackout that has long
    /// since been dismissed.
    /// </summary>
    private static bool StartedIntoTray() =>
        Environment.GetCommandLineArgs()
            .Skip(1)
            .Contains(App.MinimizedArgument, StringComparer.OrdinalIgnoreCase);
}
