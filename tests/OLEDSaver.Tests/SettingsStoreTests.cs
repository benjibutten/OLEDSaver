using System.IO;
using OLEDSaver.Input;
using OLEDSaver.Models;
using OLEDSaver.Services;
using Xunit;

namespace OLEDSaver.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OLEDSaverTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_missing_file_gives_the_defaults()
    {
        var store = new SettingsStore(_folder);

        AppSettings settings = store.Load();

        Assert.Equal(HotkeyDefinition.Default, settings.GetHotkey());
        Assert.Equal(DisplayTargetMode.AllDisplays, settings.DisplayTargetMode);
        Assert.True(settings.DismissOnKeyPress);
        Assert.True(settings.DismissOnMouseClick);
        // Movement is deliberately not a default trigger; see AppSettings.
        Assert.False(settings.DismissOnMouseMove);
        Assert.Equal(DismissTriggers.KeyPress | DismissTriggers.MouseClick, settings.GetDismissTriggers());
        Assert.True(settings.ShowHintOnBlackout);
        Assert.False(settings.KeepDisplaysAwake);
        Assert.False(settings.IdleBlackoutEnabled);
        Assert.False(settings.StartWithWindows);
    }

    [Fact]
    public void Settings_survive_a_round_trip()
    {
        var store = new SettingsStore(_folder);
        var saved = new AppSettings
        {
            DisplayTargetMode = DisplayTargetMode.SelectedDisplays,
            SelectedDisplayIds = { @"\\.\DISPLAY2" },
            DismissOnKeyPress = false,
            DismissOnMouseMove = false,
            MouseMoveThresholdPixels = 120,
            ShowHintOnBlackout = false,
            KeepDisplaysAwake = true,
            IdleBlackoutEnabled = true,
            IdleBlackoutMinutes = 25,
            SkipIdleBlackoutWhenFullscreen = false,
            StartWithWindows = true
        };
        saved.SetHotkey(new HotkeyDefinition(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x7B));

        store.Save(saved);
        AppSettings loaded = new SettingsStore(_folder).Load();

        Assert.Equal(new HotkeyDefinition(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x7B), loaded.GetHotkey());
        Assert.Equal(DisplayTargetMode.SelectedDisplays, loaded.DisplayTargetMode);
        Assert.Equal(new[] { @"\\.\DISPLAY2" }, loaded.SelectedDisplayIds);
        Assert.False(loaded.DismissOnKeyPress);
        Assert.False(loaded.DismissOnMouseMove);
        Assert.Equal(120, loaded.MouseMoveThresholdPixels);
        Assert.False(loaded.ShowHintOnBlackout);
        Assert.True(loaded.KeepDisplaysAwake);
        Assert.True(loaded.IdleBlackoutEnabled);
        Assert.Equal(25, loaded.IdleBlackoutMinutes);
        Assert.False(loaded.SkipIdleBlackoutWhenFullscreen);
        Assert.True(loaded.StartWithWindows);
    }

    [Fact]
    public void Saving_twice_replaces_the_file_rather_than_appending()
    {
        var store = new SettingsStore(_folder);

        store.Save(new AppSettings { IdleBlackoutMinutes = 5 });
        store.Save(new AppSettings { IdleBlackoutMinutes = 7 });

        Assert.Equal(7, store.Load().IdleBlackoutMinutes);
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void A_corrupt_file_falls_back_to_the_defaults_instead_of_throwing()
    {
        Directory.CreateDirectory(_folder);
        var store = new SettingsStore(_folder);
        File.WriteAllText(store.SettingsFilePath, "{ this is not json");

        Assert.Equal(HotkeyDefinition.Default, store.Load().GetHotkey());
    }

    [Fact]
    public void Out_of_range_values_are_pulled_back_into_range()
    {
        AppSettings settings = SettingsStore.Deserialize("""
            {
              "MouseMoveThresholdPixels": 9000,
              "IdleBlackoutMinutes": -4,
              "DisplayTargetMode": "AllDisplays"
            }
            """);

        Assert.Equal(AppSettings.MaxMouseMoveThresholdPixels, settings.MouseMoveThresholdPixels);
        Assert.Equal(AppSettings.MinIdleBlackoutMinutes, settings.IdleBlackoutMinutes);
    }

    [Fact]
    public void A_hotkey_of_only_a_modifier_falls_back_to_the_default()
    {
        // 0xA2 is VK_LCONTROL: nothing RegisterHotKey can ever deliver.
        AppSettings settings = SettingsStore.Deserialize("""
            { "HotkeyModifiers": 2, "HotkeyVirtualKey": 162 }
            """);

        Assert.Equal(HotkeyDefinition.Default, settings.GetHotkey());
    }

    [Fact]
    public void Duplicate_and_blank_display_ids_are_dropped()
    {
        AppSettings settings = SettingsStore.Deserialize("""
            { "SelectedDisplayIds": ["\\\\.\\DISPLAY1", "\\\\.\\DISPLAY1", "  ", ""] }
            """);

        Assert.Equal(new[] { @"\\.\DISPLAY1" }, settings.SelectedDisplayIds);
    }

    [Fact]
    public void An_unknown_display_mode_falls_back_to_all_displays()
    {
        AppSettings settings = SettingsStore.Deserialize("""{ "DisplayTargetMode": "AllDisplays" }""");
        Assert.Equal(DisplayTargetMode.AllDisplays, settings.DisplayTargetMode);
    }

    [Fact]
    public void Selecting_no_displays_falls_back_to_all_displays()
    {
        // "Only the ones I tick" with nothing ticked blanks every display anyway,
        // so the stored mode is corrected to say what will actually happen.
        AppSettings settings = SettingsStore.Deserialize("""
            { "DisplayTargetMode": "SelectedDisplays", "SelectedDisplayIds": [] }
            """);

        Assert.Equal(DisplayTargetMode.AllDisplays, settings.DisplayTargetMode);
    }

    [Fact]
    public void The_schema_version_is_stamped_after_normalizing()
    {
        AppSettings settings = SettingsStore.Deserialize("""{ "SchemaVersion": 0 }""");

        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }

    [Fact]
    public void Orphaned_temp_files_are_swept_on_load()
    {
        Directory.CreateDirectory(_folder);
        var store = new SettingsStore(_folder);
        store.Save(new AppSettings());

        // What a crash between the write and the replace leaves behind.
        File.WriteAllText(store.SettingsFilePath + ".deadbeef.tmp", "{}");
        File.WriteAllText(store.SettingsFilePath + ".deadbeef.tmp.bak", "{}");

        store.Load();

        Assert.Equal(new[] { store.SettingsFilePath }, Directory.GetFiles(_folder));
    }

    [Fact]
    public void Dismiss_triggers_reflect_the_three_switches()
    {
        var settings = new AppSettings
        {
            DismissOnKeyPress = true,
            DismissOnMouseMove = false,
            DismissOnMouseClick = true
        };

        Assert.Equal(DismissTriggers.KeyPress | DismissTriggers.MouseClick, settings.GetDismissTriggers());

        settings.DismissOnKeyPress = false;
        settings.DismissOnMouseClick = false;

        Assert.Equal(DismissTriggers.None, settings.GetDismissTriggers());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder must not fail the test run.
        }
    }
}
