using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OLEDSaver.Helpers;
using OLEDSaver.Models;

namespace OLEDSaver.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON under LocalAppData.
///
/// Writes go through a temp file and File.Replace: settings are saved while the
/// user drags a slider, and a crash mid-write must never leave a truncated file
/// that resets every preference on next launch.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _folder;
    private readonly string _path;

    public SettingsStore(string? folder = null)
    {
        _folder = string.IsNullOrWhiteSpace(folder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OLEDSaver")
            : folder;

        _path = Path.Combine(_folder, "settings.json");
    }

    public string SettingsFilePath => _path;

    public static string Serialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.Serialize(settings, JsonOptions);
    }

    public static AppSettings Deserialize(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        settings.Normalize();
        return settings;
    }

    public AppSettings Load()
    {
        CleanUpOrphanedTempFiles();

        if (!File.Exists(_path))
            return new AppSettings();

        try
        {
            return Deserialize(File.ReadAllText(_path));
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warning($"Failed to load '{_path}'. Falling back to default settings.", ex);
            return new AppSettings();
        }
    }

    /// <summary>
    /// Writes the file if it is not there yet. A fresh install otherwise has
    /// nothing on disk until the first setting changes, which makes "open the
    /// settings folder" show an empty folder — and it surfaces an unwritable
    /// folder in the log at startup rather than the first time a setting changes.
    /// </summary>
    public void EnsureSaved(AppSettings settings)
    {
        if (!File.Exists(_path))
            Save(settings);
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            WriteTextAtomically(_path, Serialize(settings));
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error($"Failed to save '{_path}'.", ex);
        }
    }

    private void WriteTextAtomically(string destinationPath, string content)
    {
        string tempPath = Path.Combine(_folder, $"{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        File.WriteAllText(tempPath, content);

        string? backupPath = null;

        try
        {
            if (File.Exists(destinationPath))
            {
                backupPath = tempPath + ".bak";
                File.Replace(tempPath, destinationPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, destinationPath);
            }
        }
        finally
        {
            TryDeleteFile(tempPath);

            if (!string.IsNullOrWhiteSpace(backupPath))
                TryDeleteFile(backupPath);
        }
    }

    /// <summary>
    /// Removes temp and backup files a crash between the write and the replace
    /// left behind. <see cref="WriteTextAtomically"/> only cleans up after itself,
    /// so a process that died mid-write orphans one for good — in a folder the
    /// settings window invites the user to open.
    /// </summary>
    private void CleanUpOrphanedTempFiles()
    {
        try
        {
            if (!Directory.Exists(_folder))
                return;

            string pattern = $"{Path.GetFileName(_path)}.*.tmp*";
            foreach (string path in Directory.EnumerateFiles(_folder, pattern))
                TryDeleteFile(path);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warning($"Failed to clean up temp files in '{_folder}'.", ex);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Cleanup failures must not mask the write that already succeeded.
        }
    }
}
