using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using OLEDSaver.Helpers;

using MessageBox = System.Windows.MessageBox;

namespace OLEDSaver.Updates;

/// <summary>
/// Installs the app into Program Files from wherever it was unpacked, which is what
/// lets it start as administrator at logon (see <see cref="Elevation"/>).
///
/// Two processes take part. The copy the user opened offers the install and starts
/// an elevated copy of itself with <see cref="InstallArgument"/>, then exits. The
/// elevated copy waits for it to go, copies the files, and starts the installed exe
/// with <see cref="InstalledArgument"/>, which turns on "Start with Windows".
/// </summary>
internal static class SelfInstaller
{
    /// <summary>Runs the exe as the elevated installer.</summary>
    public const string InstallArgument = "--install";

    /// <summary>Passed to the freshly installed app, which then turns on "Start with Windows".</summary>
    public const string InstalledArgument = "--installed";

    private const string Caption = "OLED Saver";
    private const string ExecutableName = "OLEDSaver.exe";
    private const int ERROR_CANCELLED = 1223;

    public static string InstallDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OLEDSaver");

    public static bool IsInstallMode(string[] args) =>
        args.Contains(InstallArgument, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when this is a released build that the user opened by hand from somewhere
    /// it cannot start elevated from. Local builds are never offered, or every
    /// <c>dotnet run</c> would ask.
    /// </summary>
    public static bool ShouldOffer(string? exePath) =>
        AppVersion.IsRelease
        && exePath is not null
        && !Elevation.IsProtectedFromNonAdministrators(exePath);

    /// <summary>
    /// Asks whether to install, and on yes starts the elevated installer.
    /// </summary>
    /// <returns>
    /// True when the installer is on its way and this process must exit so it can
    /// proceed; false to carry on running from here.
    /// </returns>
    public static bool OfferInstall()
    {
        MessageBoxResult answer = MessageBox.Show(
            "Install OLED Saver?\n\n"
                + $"It is copied to {InstallDirectory} and starts as administrator at every logon, "
                + "so the hotkey also works while a game or a program running as administrator has focus.\n\n"
                + "Windows asks for approval once. Choose No to run it from where it is instead.",
            Caption,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
            return false;

        var startInfo = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add(InstallArgument);
        startInfo.ArgumentList.Add("--process-id");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

        try
        {
            _ = Process.Start(startInfo);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            // The UAC prompt was declined; running from here is still useful.
            return false;
        }
    }

    /// <summary>
    /// The elevated half: copies the exe and the native libraries beside it into
    /// <see cref="InstallDirectory"/> and starts the installed copy. Failures are
    /// shown to the user, since nothing else is on screen to report them.
    /// </summary>
    public static void Run(string[] args)
    {
        try
        {
            WaitForLauncherToExit(args);

            Directory.CreateDirectory(InstallDirectory);

            string installedExe = Path.Combine(InstallDirectory, ExecutableName);
            CopyFile(Environment.ProcessPath!, installedExe);

            foreach (string library in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
                CopyFile(library, Path.Combine(InstallDirectory, Path.GetFileName(library)));

            var startInfo = new ProcessStartInfo(installedExe)
            {
                UseShellExecute = true,
                WorkingDirectory = InstallDirectory
            };
            startInfo.ArgumentList.Add(InstalledArgument);
            _ = Process.Start(startInfo);

            AppDiagnostics.Info($"Installed into {InstallDirectory}.");
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error("Installing into Program Files failed.", ex);
            MessageBox.Show(
                $"OLED Saver could not be installed.\n\n{ex.Message}",
                Caption,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    // The launcher still holds the single-instance mutex, and the installed app
    // started while it does would hand over to it and quit.
    private static void WaitForLauncherToExit(string[] args)
    {
        int index = Array.FindIndex(args, value => string.Equals(value, "--process-id", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out int processId))
            return;

        try
        {
            using Process launcher = Process.GetProcessById(processId);
            launcher.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    private static void CopyFile(string source, string destination)
    {
        File.Copy(source, destination, overwrite: true);

        // The download's "came from the internet" mark travels with the copy, and
        // would have SmartScreen question the installed app when opened from Explorer.
        try { File.Delete(destination + ":Zone.Identifier"); } catch { }
    }
}
