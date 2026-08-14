using System.IO;

namespace OLEDSaver.Updates;

/// <summary>
/// Thrown when an update failed <em>and</em> putting the previous files back failed
/// too, which is the one outcome where the install is left as a mix of two versions.
/// It is a separate type because it has to be handled differently from every other
/// update failure: the backup is the only remaining copy of the replaced files, so
/// the work folder must survive, and the app must not be started again as if nothing
/// had happened.
/// </summary>
internal sealed class UpdateRollbackException : Exception
{
    public UpdateRollbackException(
        string backupDirectory,
        string installDirectory,
        IReadOnlyList<string> unrestoredFiles,
        Exception innerException)
        : base(BuildMessage(backupDirectory, installDirectory, unrestoredFiles), innerException)
    {
        BackupDirectory = backupDirectory;
        UnrestoredFiles = unrestoredFiles;
    }

    /// <summary>Where the replaced files were copied before they were overwritten.</summary>
    public string BackupDirectory { get; }

    public IReadOnlyList<string> UnrestoredFiles { get; }

    private static string BuildMessage(
        string backupDirectory,
        string installDirectory,
        IReadOnlyList<string> unrestoredFiles)
    {
        string names = string.Join(
            Environment.NewLine,
            unrestoredFiles.Take(5).Select(path => "  " + Path.GetFileName(path)));
        if (unrestoredFiles.Count > 5)
            names += $"{Environment.NewLine}  …and {unrestoredFiles.Count - 5} more";

        return $"""
            The update failed, and {unrestoredFiles.Count} file(s) could not be put back:

            {names}

            The installation may now be a mix of two versions, so OLED Saver was left
            closed rather than started again.

            Your previous files were kept here:
              {backupDirectory}

            Copy them back over
              {installDirectory}
            or download the latest release from
            https://github.com/benjibutten/OLEDSaver/releases and extract it there.
            """;
    }
}
