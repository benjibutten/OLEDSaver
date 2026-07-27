using System.IO;
using System.Text;

namespace OLEDSaver.Helpers;

/// <summary>
/// Append-only text log in LocalAppData. The app spends most of its life in the
/// tray with no window to show errors in, so failures that would otherwise be
/// invisible (a hotkey another app already owns, a registry write denied by
/// policy) end up here.
/// </summary>
public static class AppDiagnostics
{
    /// <summary>
    /// The log is rolled to <c>oledsaver.log.1</c> past this size, keeping one
    /// previous generation. Every blackout and every dismissal is recorded, and on
    /// a machine with the idle blackout on that is a steady trickle for months —
    /// an append-only file with no ceiling would eventually be the largest thing
    /// the app leaves behind.
    /// </summary>
    private const long MaxLogBytes = 1024 * 1024;

    private static readonly object SyncRoot = new();

    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OLEDSaver",
        "logs");

    public static string LogPath { get; } = Path.Combine(LogDirectory, "oledsaver.log");

    public static void Info(string message) => Write("INFO", message, exception: null);

    public static void Warning(string message, Exception? exception = null) => Write("WARN", message, exception);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);

            var builder = new StringBuilder()
                .Append(DateTimeOffset.UtcNow.ToString("O"))
                .Append(' ')
                .Append(level)
                .Append(' ')
                .Append(message);

            if (exception != null)
            {
                builder.AppendLine();
                builder.Append(exception);
            }

            builder.AppendLine();

            lock (SyncRoot)
            {
                RollIfTooLarge();
                File.AppendAllText(LogPath, builder.ToString());
            }
        }
        catch
        {
            // Diagnostics must never interfere with app behavior.
        }
    }

    /// <summary>Caller holds <see cref="SyncRoot"/>.</summary>
    private static void RollIfTooLarge()
    {
        var current = new FileInfo(LogPath);
        if (!current.Exists || current.Length < MaxLogBytes)
            return;

        string previous = LogPath + ".1";

        // Delete on a missing file is a no-op, and Move needs the destination gone.
        File.Delete(previous);
        File.Move(LogPath, previous);
    }
}
