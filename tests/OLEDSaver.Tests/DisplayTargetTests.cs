using OLEDSaver.Models;
using OLEDSaver.Services;
using Xunit;

namespace OLEDSaver.Tests;

public class DisplayTargetTests
{
    private const string OledPath = @"\\?\DISPLAY#GSM5B08#5&3058a09&0&UID41217#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string LcdPath = @"\\?\DISPLAY#ACI27EC#5&3058a09&0&UID41219#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string VerticalPath = @"\\?\DISPLAY#AUSAA1D#5&3058a09&0&UID41221#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    private static readonly DisplayInfo Oled = new(@"\\.\DISPLAY1", OledPath, "Display 1: OLED42", 0, 0, 3840, 2160, IsPrimary: true);
    private static readonly DisplayInfo Lcd = new(@"\\.\DISPLAY2", LcdPath, "Display 2", 3840, 0, 2560, 1440, IsPrimary: false);
    private static readonly DisplayInfo Vertical = new(@"\\.\DISPLAY3", VerticalPath, "Display 3", -1080, 0, 1080, 1920, IsPrimary: false);

    private static readonly DisplayInfo[] Displays = { Oled, Lcd, Vertical };

    [Fact]
    public void All_displays_returns_everything()
    {
        Assert.Equal(Displays, DisplayService.ResolveTargets(Displays, DisplayTargetMode.AllDisplays, Array.Empty<string>()));
    }

    [Fact]
    public void Primary_only_returns_the_primary()
    {
        Assert.Equal(new[] { Oled }, DisplayService.ResolveTargets(Displays, DisplayTargetMode.PrimaryOnly, Array.Empty<string>()));
    }

    [Fact]
    public void Primary_only_falls_back_to_the_first_display_when_none_is_flagged()
    {
        var noPrimary = new[] { Lcd, Vertical };

        Assert.Equal(new[] { Lcd }, DisplayService.ResolveTargets(noPrimary, DisplayTargetMode.PrimaryOnly, Array.Empty<string>()));
    }

    [Fact]
    public void Selected_displays_returns_the_ticked_ones_in_display_order()
    {
        IReadOnlyList<DisplayInfo> targets = DisplayService.ResolveTargets(
            Displays,
            DisplayTargetMode.SelectedDisplays,
            new[] { VerticalPath, OledPath });

        Assert.Equal(new[] { Oled, Vertical }, targets);
    }

    [Fact]
    public void Selected_displays_matches_ids_case_insensitively()
    {
        IReadOnlyList<DisplayInfo> targets = DisplayService.ResolveTargets(
            Displays,
            DisplayTargetMode.SelectedDisplays,
            new[] { LcdPath.ToLowerInvariant() });

        Assert.Equal(new[] { Lcd }, targets);
    }

    [Fact]
    public void Selected_displays_follows_the_monitor_when_windows_reshuffles_the_slots()
    {
        // The whole point of storing the hardware id. After a monitor sleeps or is
        // switched off at the panel, Windows can hand the GDI slots back out in a
        // different order — here the OLED comes back as \\.\DISPLAY2 and the LCD
        // takes \\.\DISPLAY1. The tick has to stay on the OLED.
        var reshuffled = new[]
        {
            Lcd with { Id = @"\\.\DISPLAY1", IsPrimary = true },
            Oled with { Id = @"\\.\DISPLAY2", IsPrimary = false }
        };

        IReadOnlyList<DisplayInfo> targets = DisplayService.ResolveTargets(
            reshuffled,
            DisplayTargetMode.SelectedDisplays,
            new[] { OledPath });

        Assert.Equal(new[] { OledPath }, targets.Select(target => target.HardwareId));
    }

    [Fact]
    public void Selected_displays_falls_back_to_the_gdi_name_when_no_hardware_id_is_reported()
    {
        // Nothing on the machine would say which panel is which, so the slot name
        // is all there is; it still has to match what was saved.
        var pathless = new[] { Oled with { HardwareId = "" }, Lcd with { HardwareId = "" } };

        IReadOnlyList<DisplayInfo> targets = DisplayService.ResolveTargets(
            pathless,
            DisplayTargetMode.SelectedDisplays,
            new[] { @"\\.\DISPLAY2" });

        Assert.Equal(new[] { pathless[1] }, targets);
    }

    [Fact]
    public void A_selection_that_matches_nothing_falls_back_to_every_display()
    {
        // The selected monitor was unplugged. A hotkey that silently does nothing
        // is worse than blanking more than asked.
        IReadOnlyList<DisplayInfo> targets = DisplayService.ResolveTargets(
            Displays,
            DisplayTargetMode.SelectedDisplays,
            new[] { @"\\?\DISPLAY#DEL0000#5&0&UID99999#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}" });

        Assert.Equal(Displays, targets);
    }

    [Fact]
    public void A_saved_gdi_slot_name_no_longer_matches_a_monitor_that_reports_a_hardware_id()
    {
        // An unmigrated slot name must not resolve by slot, because the slot is
        // exactly what moves. Falling back to every display is the visible,
        // correctable failure; blanking the wrong monitor is not.
        IReadOnlyList<DisplayInfo> targets = DisplayService.ResolveTargets(
            Displays,
            DisplayTargetMode.SelectedDisplays,
            new[] { @"\\.\DISPLAY1" });

        Assert.Equal(Displays, targets);
    }

    [Fact]
    public void Migration_pins_a_saved_slot_name_to_the_monitor_in_that_slot()
    {
        IReadOnlyList<string> migrated = DisplayService.MigrateLegacySelection(
            Displays,
            new[] { @"\\.\DISPLAY2" });

        Assert.Equal(new[] { LcdPath }, migrated);
    }

    [Fact]
    public void Migration_drops_a_slot_with_no_monitor_behind_it()
    {
        // That monitor is switched off right now. Keeping the slot name would let
        // it match some other monitor the next time the layout changes.
        IReadOnlyList<string> migrated = DisplayService.MigrateLegacySelection(
            Displays,
            new[] { @"\\.\DISPLAY2", @"\\.\DISPLAY9" });

        Assert.Equal(new[] { LcdPath }, migrated);
    }

    [Fact]
    public void Migration_leaves_hardware_ids_alone()
    {
        IReadOnlyList<string> migrated = DisplayService.MigrateLegacySelection(
            Displays,
            new[] { OledPath, VerticalPath });

        Assert.Equal(new[] { OledPath, VerticalPath }, migrated);
    }

    [Fact]
    public void Migration_keeps_the_slot_name_when_the_monitor_reports_no_hardware_id()
    {
        // Nothing better exists on this machine, and dropping the tick would lose
        // a setting that still works.
        var pathless = new[] { Oled with { HardwareId = "" }, Lcd with { HardwareId = "" } };

        IReadOnlyList<string> migrated = DisplayService.MigrateLegacySelection(
            pathless,
            new[] { @"\\.\DISPLAY1" });

        Assert.Equal(new[] { @"\\.\DISPLAY1" }, migrated);
    }

    [Fact]
    public void Migration_collapses_two_slots_that_now_name_one_monitor()
    {
        IReadOnlyList<string> migrated = DisplayService.MigrateLegacySelection(
            new[] { Oled },
            new[] { @"\\.\DISPLAY1", OledPath });

        Assert.Equal(new[] { OledPath }, migrated);
    }

    [Fact]
    public void An_empty_selection_resolves_to_every_display()
    {
        // Which is why AppSettings.Normalize and MainViewModel move the mode back
        // to AllDisplays when the last box is unticked: this result is fine, but
        // the radio button has to agree with it.
        IReadOnlyList<DisplayInfo> targets = DisplayService.ResolveTargets(
            Displays,
            DisplayTargetMode.SelectedDisplays,
            Array.Empty<string>());

        Assert.Equal(Displays, targets);
    }

    [Fact]
    public void No_displays_at_all_resolves_to_nothing()
    {
        Assert.Empty(DisplayService.ResolveTargets(Array.Empty<DisplayInfo>(), DisplayTargetMode.AllDisplays, Array.Empty<string>()));
    }
}
