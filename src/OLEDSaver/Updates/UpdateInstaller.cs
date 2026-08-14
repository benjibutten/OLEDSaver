using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows;

using MessageBox = System.Windows.MessageBox;

namespace OLEDSaver.Updates;

/// <summary>
/// The half of the update that runs in the copied executable, outside the install
/// folder. It waits for the real app to exit, swaps the files, and starts the new
/// build. Every file it replaces is backed up first, so a failure partway through
/// puts the previous install back rather than leaving half of one version and half
/// of another.
/// </summary>
internal static class UpdateInstaller
{
    /// <summary>Runs the copied exe as the updater.</summary>
    public const string ApplyArgument = "--apply-update";

    /// <summary>Runs the installed exe as a janitor for a failed update.</summary>
    public const string CleanupArgument = "--cleanup-update";

    /// <summary>Passed to the freshly installed app so it can drop the updater's temp folder.</summary>
    public const string PostInstallCleanupArgument = "--update-cleanup";

    /// <summary>
    /// Set when the app being updated was itself started into the tray. A copy that
    /// was launched at logon has to come back the same way: putting the settings
    /// window on screen is not what a background app restarting means, and nobody may
    /// even be at the machine to close it again.
    /// </summary>
    public const string RestartMinimizedArgument = "--restart-minimized";

    private const int FileOperationAttempts = 20;
    private static readonly TimeSpan FileOperationDelay = TimeSpan.FromMilliseconds(250);

    public static bool IsUpdateMode(string[] args) =>
        args.Contains(ApplyArgument, StringComparer.OrdinalIgnoreCase);

    public static bool IsCleanupMode(string[] args) =>
        args.Contains(CleanupArgument, StringComparer.OrdinalIgnoreCase);

    public static async Task RunCleanupAsync(string[] args)
    {
        try
        {
            int processId = int.Parse(GetRequiredArgument(args, "--process-id"));
            string workDirectory = GetValidatedWorkDirectory(Path.Combine(
                GetRequiredArgument(args, "--work-directory"),
                "update.zip"));

            await Task.Run(() => WaitForProcessToExit(processId));
            await DeleteWorkDirectoryAsync(workDirectory);
        }
        catch
        {
            // Cleanup is best effort and must never start the normal application.
        }
    }

    public static async Task RunAsync(string[] args)
    {
        var progressWindow = new UpdateProgressWindow(owner: null);
        progressWindow.Show();
        var progress = new Progress<UpdateProgress>(progressWindow.Report);

        try
        {
            await Task.Run(() => Apply(args, progress));
        }
        catch (UpdateRollbackException ex)
        {
            // No cleanup here on purpose: the work folder holds the backup, which is
            // now the only copy of the files the failed install replaced. Deleting it
            // would take the user's way back with it.
            MessageBox.Show(
                ex.Message,
                "OLED Saver update",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            // Everything else leaves the previous install intact — either nothing was
            // replaced yet, or the rollback put it all back — so the app is started
            // again. Failing to update is no reason to leave the machine without the
            // hotkey it had a minute ago.
            MessageBox.Show(
                $"The update could not be completed, so the installed version was kept.\n\n{ex.Message}",
                "OLED Saver update",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            TryRestartInstalledApp(args);
            LaunchCleanupProcess(args);
        }
        finally
        {
            progressWindow.Close();
        }
    }

    private static void Apply(string[] args, IProgress<UpdateProgress> progress)
    {
        int processId = int.Parse(GetRequiredArgument(args, "--process-id"));
        string zipPath = Path.GetFullPath(GetRequiredArgument(args, "--zip-path"));
        string expectedHash = GetRequiredArgument(args, "--expected-hash");
        string installDirectory = Path.GetFullPath(GetRequiredArgument(args, "--install-directory"));
        string executablePath = Path.GetFullPath(GetRequiredArgument(args, "--executable-path"));
        string workDirectory = GetValidatedWorkDirectory(zipPath);

        progress.Report(new UpdateProgress("Waiting for OLED Saver to close…"));
        WaitForProcessToExit(processId);

        // Checked a second time, here rather than only before launch: between the two
        // runs the file has been sitting in a world-writable temp folder.
        progress.Report(new UpdateProgress("Verifying the update…"));
        VerifyArchive(zipPath, expectedHash);

        string stagingDirectory = Path.Combine(workDirectory, "staging");
        string backupDirectory = Path.Combine(workDirectory, "backup");
        progress.Report(new UpdateProgress("Unpacking the update…"));
        ZipFile.ExtractToDirectory(zipPath, stagingDirectory, overwriteFiles: true);

        string executableName = Path.GetFileName(executablePath);
        if (!File.Exists(Path.Combine(stagingDirectory, executableName)))
            throw new InvalidDataException($"The update archive does not contain {executableName}.");

        progress.Report(new UpdateProgress("Installing the update…"));
        InstallFiles(stagingDirectory, installDirectory, backupDirectory);

        progress.Report(new UpdateProgress("Starting OLED Saver again…"));
        ProcessStartInfo restart = CreateRestartStartInfo(executablePath, installDirectory, args);
        restart.ArgumentList.Add(PostInstallCleanupArgument);
        restart.ArgumentList.Add(workDirectory);
        _ = Process.Start(restart)
            ?? throw new InvalidOperationException("The updated app could not be started again.");
    }

    internal static ProcessStartInfo CreateRestartStartInfo(string executablePath, string installDirectory, string[] args)
    {
        var restart = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = installDirectory
        };

        if (HasFlag(args, RestartMinimizedArgument))
            restart.ArgumentList.Add(App.MinimizedArgument);

        return restart;
    }

    /// <summary>
    /// Puts the previous version back on screen after an update that failed without
    /// damaging the install. Best effort: the update has already been reported, and a
    /// second failure here has nothing left to tell the user that the first did not.
    /// </summary>
    private static void TryRestartInstalledApp(string[] args)
    {
        try
        {
            string executablePath = Path.GetFullPath(GetRequiredArgument(args, "--executable-path"));
            string installDirectory = Path.GetFullPath(GetRequiredArgument(args, "--install-directory"));
            _ = Process.Start(CreateRestartStartInfo(executablePath, installDirectory, args));
        }
        catch
        {
            // Nothing useful left to do; the failure is already in front of the user.
        }
    }

    /// <summary>
    /// Replaces the installed files with the staged ones, backing up each file before
    /// it is overwritten. Throws <see cref="UpdateRollbackException"/> if the unwind
    /// after a failure could not put every file back — the caller has to keep the
    /// backup folder in that case, because it holds the only copy of what was there.
    /// </summary>
    /// <summary>One replaced file: where it went, and where its previous copy is kept.</summary>
    internal readonly record struct InstalledFile(string Destination, string? Backup);

    internal static void InstallFiles(string stagingDirectory, string installDirectory, string backupDirectory)
    {
        Directory.CreateDirectory(backupDirectory);
        var installedFiles = new List<InstalledFile>();

        try
        {
            foreach (string sourcePath in Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(stagingDirectory, sourcePath);
                string destinationPath = Path.Combine(installDirectory, relativePath);
                string? destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (destinationDirectory is not null)
                    Directory.CreateDirectory(destinationDirectory);

                string? backupPath = null;
                if (File.Exists(destinationPath))
                {
                    backupPath = Path.Combine(backupDirectory, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    Retry(() => File.Copy(destinationPath, backupPath, overwrite: true));
                }

                ReplaceFile(sourcePath, destinationPath);
                installedFiles.Add(new InstalledFile(destinationPath, backupPath));
            }
        }
        catch (Exception installException)
        {
            IReadOnlyList<string> unrestoredFiles = RestoreBackups(installedFiles);

            // A rollback that could not finish is not the same failure as one that put
            // everything back: the install is now a mix of two versions, and saying so
            // is the difference between a user who can recover and one who cannot.
            if (unrestoredFiles.Count > 0)
                throw new UpdateRollbackException(backupDirectory, installDirectory, unrestoredFiles, installException);

            throw;
        }
    }

    /// <summary>
    /// Puts the previous install back, newest file first so that one which existed in
    /// both versions ends up as the older one. Every file is attempted even after one
    /// of them fails — stopping at the first would leave more of the install rolled
    /// forward than carrying on does — and the ones that could not be restored are
    /// returned rather than swallowed.
    /// </summary>
    internal static IReadOnlyList<string> RestoreBackups(IReadOnlyList<InstalledFile> installedFiles)
    {
        var unrestoredFiles = new List<string>();

        for (int index = installedFiles.Count - 1; index >= 0; index--)
        {
            (string destinationPath, string? backupPath) = installedFiles[index];
            try
            {
                if (backupPath is not null)
                    ReplaceFile(backupPath, destinationPath);
                else
                    Retry(() => File.Delete(destinationPath));
            }
            catch
            {
                unrestoredFiles.Add(destinationPath);
            }
        }

        return unrestoredFiles;
    }

    /// <summary>
    /// Copies next to the destination and then moves over it. The move is the only step
    /// that touches the live file, so a half-written copy can never end up as the
    /// installed one.
    /// </summary>
    private static void ReplaceFile(string sourcePath, string destinationPath)
    {
        string incomingPath = Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            $".{Path.GetFileName(destinationPath)}.update-{Guid.NewGuid():N}.tmp");

        try
        {
            File.Copy(sourcePath, incomingPath, overwrite: true);
            Retry(() => File.Move(incomingPath, destinationPath, overwrite: true));
        }
        finally
        {
            try { File.Delete(incomingPath); } catch { }
        }
    }

    /// <summary>
    /// Windows keeps files locked for a moment after a process exits — antivirus and
    /// Explorer both do it — so a lock is a reason to wait rather than to give up.
    /// </summary>
    private static void Retry(Action operation)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (IOException) when (attempt < FileOperationAttempts)
            {
                Thread.Sleep(FileOperationDelay);
            }
            catch (UnauthorizedAccessException) when (attempt < FileOperationAttempts)
            {
                Thread.Sleep(FileOperationDelay);
            }
        }
    }

    private static void WaitForProcessToExit(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            process.WaitForExit();
        }
        catch (ArgumentException)
        {
            // It already exited before the updater started waiting.
        }
    }

    private static void VerifyArchive(string zipPath, string expectedHash)
    {
        using FileStream stream = File.OpenRead(zipPath);
        string actualHash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update failed its SHA-256 check.");
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static string GetRequiredArgument(string[] args, string name)
    {
        int index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            throw new ArgumentException($"The update argument {name} is missing.");

        return args[index + 1];
    }

    /// <summary>
    /// The arguments arrive on a command line, and the install step deletes and
    /// overwrites whatever they point at. Only a folder this app itself created
    /// directly under %TEMP% is accepted.
    /// </summary>
    private static string GetValidatedWorkDirectory(string zipPath)
    {
        string workDirectory = Path.GetDirectoryName(Path.GetFullPath(zipPath))
            ?? throw new InvalidOperationException("The update's working folder is not valid.");
        string tempDirectory = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string expectedPrefix = Path.Combine(tempDirectory, "OLEDSaver-update-");

        if (!workDirectory.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetDirectoryName(workDirectory)?.TrimEnd(Path.DirectorySeparatorChar),
                tempDirectory.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The update's working folder cannot be trusted.");
        }

        return workDirectory;
    }

    /// <summary>
    /// The failed updater cannot delete the folder it is itself running from, so the
    /// installed app is asked to do it once this process is gone.
    /// </summary>
    private static void LaunchCleanupProcess(string[] args)
    {
        try
        {
            string executablePath = Path.GetFullPath(GetRequiredArgument(args, "--executable-path"));
            string zipPath = Path.GetFullPath(GetRequiredArgument(args, "--zip-path"));
            string workDirectory = GetValidatedWorkDirectory(zipPath);

            var cleanup = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!
            };
            cleanup.ArgumentList.Add(CleanupArgument);
            cleanup.ArgumentList.Add("--process-id");
            cleanup.ArgumentList.Add(Environment.ProcessId.ToString());
            cleanup.ArgumentList.Add("--work-directory");
            cleanup.ArgumentList.Add(workDirectory);
            _ = Process.Start(cleanup);
        }
        catch
        {
            // The update failure has already been reported; cleanup is best effort.
        }
    }

    private static async Task DeleteWorkDirectoryAsync(string workDirectory)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));

            try
            {
                Directory.Delete(workDirectory, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch { }
        }
    }

    /// <summary>
    /// Called by the freshly started app: the updater that unpacked it is still exiting,
    /// so its temp folder is deleted in the background rather than on the startup path.
    /// </summary>
    public static void ScheduleCleanup(string[] args)
    {
        int index = Array.FindIndex(args, value => string.Equals(value, PostInstallCleanupArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length)
            return;

        string workDirectory;
        try
        {
            workDirectory = GetValidatedWorkDirectory(Path.Combine(args[index + 1], "update.zip"));
        }
        catch
        {
            return;
        }

        _ = DeleteWorkDirectoryAsync(workDirectory);
    }
}
