using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace OLEDSaver.Helpers;

/// <summary>
/// Append-only text log in LocalAppData. The app spends most of its life in the
/// tray with no window to show errors in, so failures that would otherwise be
/// invisible (a hotkey another app already owns, a registry write denied by
/// policy) end up here.
///
/// Writing happens on a background thread. The callers that matter most are on
/// the blackout's hot path — "Blackout on" is logged between the overlays going
/// up and the compositor presenting them — and opening, appending to and closing
/// a file in LocalAppData is not free: on a machine with a filesystem filter
/// driver in the way it is tens of milliseconds, paid on the UI thread, every
/// single toggle. Queueing the line instead costs an allocation.
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

    /// <summary>
    /// A ceiling on how far the writer may fall behind. Diagnostics are never
    /// worth unbounded memory, so lines past this are dropped rather than queued.
    /// </summary>
    private const int MaxQueuedEntries = 4096;

    private static readonly object SyncRoot = new();

    private static readonly BlockingCollection<string> Queue =
        new(new ConcurrentQueue<string>(), MaxQueuedEntries);

    private static readonly Thread Writer;

    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OLEDSaver",
        "logs");

    static AppDiagnostics()
    {
        Writer = new Thread(WriterLoop)
        {
            // Never keeps the process alive on its own; Flush is what guarantees
            // the tail of the log reaches disk on the way out.
            IsBackground = true,
            Name = "OLEDSaver diagnostics",
            Priority = ThreadPriority.BelowNormal
        };

        Writer.Start();
    }

    public static string LogPath { get; } = Path.Combine(LogDirectory, "oledsaver.log");

    public static void Info(string message) => Write("INFO", message, exception: null);

    public static void Warning(string message, Exception? exception = null) => Write("WARN", message, exception);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    /// <summary>
    /// Drains everything still queued and stops the writer. Called on the way out,
    /// because the process disappearing with lines still in memory would lose
    /// exactly the entries that explain why it went.
    /// </summary>
    public static void Flush()
    {
        try
        {
            Queue.CompleteAdding();
            Writer.Join(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Diagnostics must never interfere with app behavior.
        }
    }

    private static void Write(string level, string message, Exception? exception)
    {
        string entry = Format(level, message, exception);

        // TryAdd fails once Flush has closed the queue, and once the writer has
        // fallen MaxQueuedEntries behind. The shutdown path is the one that
        // matters: those lines go straight to disk rather than vanishing.
        try
        {
            if (Queue.TryAdd(entry))
                return;
        }
        catch (Exception)
        {
            // An added-after-completed race lands here; fall through to the
            // synchronous write.
        }

        if (Queue.IsAddingCompleted)
            Append(entry);
    }

    private static string Format(string level, string message, Exception? exception)
    {
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
        return builder.ToString();
    }

    /// <summary>
    /// Writes queued lines in batches. Batching is the point: a burst of entries
    /// (startup, or a blackout going up and straight back down) becomes one file
    /// open instead of one per line.
    /// </summary>
    private static void WriterLoop()
    {
        try
        {
            foreach (string first in Queue.GetConsumingEnumerable())
            {
                var batch = new StringBuilder(first);

                while (Queue.TryTake(out string? next))
                    batch.Append(next);

                Append(batch.ToString());
            }
        }
        catch (Exception)
        {
            // Diagnostics must never interfere with app behavior.
        }
    }

    private static void Append(string text)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);

            lock (SyncRoot)
            {
                RollIfTooLarge();
                File.AppendAllText(LogPath, text);
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
