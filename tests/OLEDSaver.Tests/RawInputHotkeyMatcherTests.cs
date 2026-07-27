using OLEDSaver.Input;
using Xunit;

namespace OLEDSaver.Tests;

public class RawInputHotkeyMatcherTests
{
    private const uint VkB = 0x42;
    private const uint VkLControl = 0xA2;
    private const uint VkRControl = 0xA3;
    private const uint VkLMenu = 0xA4;
    private const uint VkLShift = 0xA0;

    private sealed class TestClock
    {
        private DateTime _utcNow = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

        public Func<DateTime> Now => () => _utcNow;

        public void Advance(TimeSpan by) => _utcNow += by;
    }

    private static RawInputHotkeyMatcher CreateMatcher(
        IEnumerable<uint>? held = null,
        bool hotkeyKeyAlreadyHeld = false)
    {
        var matcher = new RawInputHotkeyMatcher();
        matcher.Configure(HotkeyDefinition.Default, held, hotkeyKeyAlreadyHeld);
        return matcher;
    }

    [Fact]
    public void Fires_when_the_full_combination_is_pressed()
    {
        RawInputHotkeyMatcher matcher = CreateMatcher();

        Assert.False(matcher.ProcessKeyEvent(VkLControl, isKeyDown: true));
        Assert.False(matcher.ProcessKeyEvent(VkLMenu, isKeyDown: true));
        Assert.True(matcher.ProcessKeyEvent(VkB, isKeyDown: true));
    }

    [Fact]
    public void Does_not_fire_when_a_modifier_is_missing()
    {
        RawInputHotkeyMatcher matcher = CreateMatcher();

        matcher.ProcessKeyEvent(VkLControl, isKeyDown: true);

        Assert.False(matcher.ProcessKeyEvent(VkB, isKeyDown: true));
    }

    [Fact]
    public void Does_not_fire_when_an_extra_modifier_is_held()
    {
        RawInputHotkeyMatcher matcher = CreateMatcher();

        matcher.ProcessKeyEvent(VkLControl, isKeyDown: true);
        matcher.ProcessKeyEvent(VkLMenu, isKeyDown: true);
        matcher.ProcessKeyEvent(VkLShift, isKeyDown: true);

        Assert.False(matcher.ProcessKeyEvent(VkB, isKeyDown: true));
    }

    [Fact]
    public void Auto_repeat_does_not_fire_again()
    {
        RawInputHotkeyMatcher matcher = CreateMatcher(new[] { VkLControl, VkLMenu });

        Assert.True(matcher.ProcessKeyEvent(VkB, isKeyDown: true));
        Assert.False(matcher.ProcessKeyEvent(VkB, isKeyDown: true));
        Assert.False(matcher.ProcessKeyEvent(VkB, isKeyDown: true));
    }

    [Fact]
    public void A_second_press_after_release_fires_again()
    {
        RawInputHotkeyMatcher matcher = CreateMatcher(new[] { VkLControl, VkLMenu });

        Assert.True(matcher.ProcessKeyEvent(VkB, isKeyDown: true));
        matcher.ProcessKeyEvent(VkB, isKeyDown: false);

        Assert.True(matcher.ProcessKeyEvent(VkB, isKeyDown: true));
    }

    [Fact]
    public void Adding_the_missing_modifier_mid_hold_does_not_fire_on_the_next_repeat()
    {
        RawInputHotkeyMatcher matcher = CreateMatcher(new[] { VkLControl });

        // B goes down without Alt, then Alt joins while B is still held. The
        // auto-repeat that follows is not a fresh press.
        Assert.False(matcher.ProcessKeyEvent(VkB, isKeyDown: true));
        matcher.ProcessKeyEvent(VkLMenu, isKeyDown: true);

        Assert.False(matcher.ProcessKeyEvent(VkB, isKeyDown: true));
    }

    [Fact]
    public void Either_side_of_a_modifier_counts()
    {
        RawInputHotkeyMatcher matcher = CreateMatcher();

        matcher.ProcessKeyEvent(VkRControl, isKeyDown: true);
        matcher.ProcessKeyEvent(VkLMenu, isKeyDown: true);

        Assert.True(matcher.ProcessKeyEvent(VkB, isKeyDown: true));
    }

    [Fact]
    public void Releasing_one_of_two_ctrl_keys_keeps_ctrl_held()
    {
        RawInputHotkeyMatcher matcher = CreateMatcher();

        matcher.ProcessKeyEvent(VkLControl, isKeyDown: true);
        matcher.ProcessKeyEvent(VkRControl, isKeyDown: true);
        matcher.ProcessKeyEvent(VkLControl, isKeyDown: false);
        matcher.ProcessKeyEvent(VkLMenu, isKeyDown: true);

        Assert.True(matcher.ProcessKeyEvent(VkB, isKeyDown: true));
    }

    [Fact]
    public void The_key_held_at_configure_time_is_ignored_until_released()
    {
        // The state right after recording a combination: the user is still holding it.
        RawInputHotkeyMatcher matcher = CreateMatcher(
            new[] { VkLControl, VkLMenu },
            hotkeyKeyAlreadyHeld: true);

        Assert.False(matcher.ProcessKeyEvent(VkB, isKeyDown: true));

        matcher.ProcessKeyEvent(VkB, isKeyDown: false);

        Assert.True(matcher.ProcessKeyEvent(VkB, isKeyDown: true));
    }

    [Fact]
    public void Raw_input_and_WM_HOTKEY_together_trigger_once_per_press()
    {
        RawInputHotkeyMatcher matcher = CreateMatcher(new[] { VkLControl, VkLMenu });

        // Raw input arrives first and claims the press.
        Assert.True(matcher.ProcessKeyEvent(VkB, isKeyDown: true));
        Assert.False(matcher.TryHandleHotkeyMessage());

        // The claim is released by the key-up, so the next press works either way round.
        matcher.ProcessKeyEvent(VkB, isKeyDown: false);

        Assert.True(matcher.TryHandleHotkeyMessage());
        Assert.False(matcher.ProcessKeyEvent(VkB, isKeyDown: true));
    }

    [Fact]
    public void A_missed_key_up_does_not_disable_the_raw_input_path_for_good()
    {
        // A session lock, the secure desktop or a game grabbing the device can
        // swallow the key-up. Without recovery the auto-repeat guard would stay
        // latched and the hotkey would be dead until the next settings change.
        var clock = new TestClock();
        var matcher = new RawInputHotkeyMatcher(clock.Now);
        matcher.Configure(HotkeyDefinition.Default, new[] { VkLControl, VkLMenu });

        Assert.True(matcher.ProcessKeyEvent(VkB, isKeyDown: true));

        // ...and the key-up never arrives.
        clock.Advance(RawInputHotkeyMatcher.StalePressTimeout);

        Assert.True(matcher.ProcessKeyEvent(VkB, isKeyDown: true));
    }

    [Fact]
    public void A_missed_key_up_does_not_disable_the_WM_HOTKEY_path_for_good()
    {
        var clock = new TestClock();
        var matcher = new RawInputHotkeyMatcher(clock.Now);
        matcher.Configure(HotkeyDefinition.Default, new[] { VkLControl, VkLMenu });

        Assert.True(matcher.ProcessKeyEvent(VkB, isKeyDown: true));
        Assert.False(matcher.TryHandleHotkeyMessage());

        clock.Advance(RawInputHotkeyMatcher.StalePressTimeout);

        Assert.True(matcher.TryHandleHotkeyMessage());
    }

    [Fact]
    public void Holding_the_hotkey_down_never_goes_stale()
    {
        // Auto-repeat keeps arriving while the key is held, so a long hold must
        // not be mistaken for a missed key-up and fire a second time.
        var clock = new TestClock();
        var matcher = new RawInputHotkeyMatcher(clock.Now);
        matcher.Configure(HotkeyDefinition.Default, new[] { VkLControl, VkLMenu });

        Assert.True(matcher.ProcessKeyEvent(VkB, isKeyDown: true));

        // Ten seconds of holding, at a repeat rate well inside the timeout.
        for (int i = 0; i < 300; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(33));
            Assert.False(matcher.ProcessKeyEvent(VkB, isKeyDown: true));
        }
    }

    [Fact]
    public void An_unset_hotkey_never_fires()
    {
        var matcher = new RawInputHotkeyMatcher();
        matcher.Configure(HotkeyDefinition.None);

        Assert.False(matcher.ProcessKeyEvent(VkB, isKeyDown: true));
    }
}
