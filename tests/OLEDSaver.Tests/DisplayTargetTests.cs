using OLEDSaver.Models;
using OLEDSaver.Services;
using Xunit;

namespace OLEDSaver.Tests;

public class DisplayTargetTests
{
    private static readonly DisplayInfo Oled = new(@"\\.\DISPLAY1", "Display 1: OLED42", 0, 0, 3840, 2160, IsPrimary: true);
    private static readonly DisplayInfo Lcd = new(@"\\.\DISPLAY2", "Display 2", 3840, 0, 2560, 1440, IsPrimary: false);
    private static readonly DisplayInfo Vertical = new(@"\\.\DISPLAY3", "Display 3", -1080, 0, 1080, 1920, IsPrimary: false);

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
            new[] { @"\\.\DISPLAY3", @"\\.\DISPLAY1" });

        Assert.Equal(new[] { Oled, Vertical }, targets);
    }

    [Fact]
    public void Selected_displays_matches_ids_case_insensitively()
    {
        IReadOnlyList<DisplayInfo> targets = DisplayService.ResolveTargets(
            Displays,
            DisplayTargetMode.SelectedDisplays,
            new[] { @"\\.\display2" });

        Assert.Equal(new[] { Lcd }, targets);
    }

    [Fact]
    public void A_selection_that_matches_nothing_falls_back_to_every_display()
    {
        // The selected monitor was unplugged. A hotkey that silently does nothing
        // is worse than blanking more than asked.
        IReadOnlyList<DisplayInfo> targets = DisplayService.ResolveTargets(
            Displays,
            DisplayTargetMode.SelectedDisplays,
            new[] { @"\\.\DISPLAY9" });

        Assert.Equal(Displays, targets);
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
