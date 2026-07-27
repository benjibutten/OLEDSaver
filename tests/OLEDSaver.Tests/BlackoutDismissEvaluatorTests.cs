using OLEDSaver.Input;
using Xunit;

namespace OLEDSaver.Tests;

public class BlackoutDismissEvaluatorTests
{
    private const uint VkB = 0x42;
    private const uint VkA = 0x41;
    private const uint VkEscape = 0x1B;
    private const uint VkLControl = 0xA2;

    private static readonly DateTime Start = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AfterGrace = Start + TimeSpan.FromSeconds(1);

    private static BlackoutDismissEvaluator CreateArmed(
        DismissTriggers triggers = DismissTriggers.All,
        int threshold = 50,
        IEnumerable<uint>? keysHeld = null)
    {
        var evaluator = new BlackoutDismissEvaluator();
        evaluator.Configure(triggers, threshold);
        evaluator.Arm(Start, keysHeld);
        return evaluator;
    }

    [Fact]
    public void Nothing_dismisses_before_it_is_armed()
    {
        var evaluator = new BlackoutDismissEvaluator();
        evaluator.Configure(DismissTriggers.All, 50);

        Assert.False(evaluator.ProcessKey(VkA, isKeyDown: true, AfterGrace));
        Assert.False(evaluator.ProcessMouseButton(AfterGrace));
        Assert.False(evaluator.ProcessMouseMove(500, 500, isAbsolute: false, AfterGrace));
    }

    [Fact]
    public void Input_during_the_grace_period_is_ignored()
    {
        BlackoutDismissEvaluator evaluator = CreateArmed();

        Assert.False(evaluator.IsListening(Start));
        Assert.False(evaluator.ProcessKey(VkA, isKeyDown: true, Start + TimeSpan.FromMilliseconds(100)));
        Assert.False(evaluator.ProcessMouseButton(Start + TimeSpan.FromMilliseconds(100)));
        Assert.True(evaluator.IsListening(AfterGrace));
    }

    [Fact]
    public void A_key_press_dismisses_once_the_grace_period_has_passed()
    {
        BlackoutDismissEvaluator evaluator = CreateArmed();

        Assert.True(evaluator.ProcessKey(VkA, isKeyDown: true, AfterGrace));
    }

    [Fact]
    public void The_hotkey_still_held_when_the_blackout_appears_is_ignored_until_released()
    {
        // Ctrl + Alt + B switched the blackout on and is still down; auto-repeat
        // must not take it straight back off.
        BlackoutDismissEvaluator evaluator = CreateArmed(keysHeld: new[] { VkLControl, VkB });

        Assert.False(evaluator.ProcessKey(VkB, isKeyDown: true, AfterGrace));
        Assert.False(evaluator.ProcessKey(VkB, isKeyDown: true, AfterGrace));

        // Releasing it re-arms that key for the next press.
        evaluator.ProcessKey(VkB, isKeyDown: false, AfterGrace);

        Assert.True(evaluator.ProcessKey(VkB, isKeyDown: true, AfterGrace));
    }

    [Fact]
    public void Another_key_still_dismisses_while_the_hotkey_is_held()
    {
        BlackoutDismissEvaluator evaluator = CreateArmed(keysHeld: new[] { VkLControl, VkB });

        Assert.True(evaluator.ProcessKey(VkA, isKeyDown: true, AfterGrace));
    }

    [Theory]
    [InlineData(0xA2)] // VK_LCONTROL
    [InlineData(0xA4)] // VK_LMENU
    [InlineData(0xA0)] // VK_LSHIFT
    [InlineData(0x5B)] // VK_LWIN
    public void Modifier_keys_alone_never_dismiss(uint modifierVk)
    {
        // Pressing the hotkey again to switch the blackout off starts with its
        // modifiers. If those dismissed it, the hotkey would then switch it
        // straight back on.
        BlackoutDismissEvaluator evaluator = CreateArmed();

        Assert.False(evaluator.ProcessKey(modifierVk, isKeyDown: true, AfterGrace));
    }

    [Fact]
    public void A_modified_key_still_dismisses()
    {
        BlackoutDismissEvaluator evaluator = CreateArmed();

        evaluator.ProcessKey(VkLControl, isKeyDown: true, AfterGrace);

        Assert.True(evaluator.ProcessKey(VkA, isKeyDown: true, AfterGrace));
    }

    [Fact]
    public void Key_releases_never_dismiss()
    {
        BlackoutDismissEvaluator evaluator = CreateArmed();

        Assert.False(evaluator.ProcessKey(VkA, isKeyDown: false, AfterGrace));
    }

    [Fact]
    public void Escape_dismisses_even_with_key_presses_switched_off()
    {
        BlackoutDismissEvaluator evaluator = CreateArmed(DismissTriggers.None);

        Assert.False(evaluator.ProcessKey(VkA, isKeyDown: true, AfterGrace));
        Assert.True(evaluator.ProcessKey(VkEscape, isKeyDown: true, AfterGrace));
    }

    [Fact]
    public void Key_presses_do_not_dismiss_when_that_trigger_is_off()
    {
        BlackoutDismissEvaluator evaluator = CreateArmed(DismissTriggers.MouseClick);

        Assert.False(evaluator.ProcessKey(VkA, isKeyDown: true, AfterGrace));
        Assert.True(evaluator.ProcessMouseButton(AfterGrace));
    }

    [Fact]
    public void Mouse_clicks_do_not_dismiss_when_that_trigger_is_off()
    {
        BlackoutDismissEvaluator evaluator = CreateArmed(DismissTriggers.KeyPress);

        Assert.False(evaluator.ProcessMouseButton(AfterGrace));
    }

    [Fact]
    public void Movement_has_to_cross_the_threshold()
    {
        BlackoutDismissEvaluator evaluator = CreateArmed(threshold: 50);

        Assert.False(evaluator.ProcessMouseMove(20, 0, isAbsolute: false, AfterGrace));
        Assert.False(evaluator.ProcessMouseMove(20, 0, isAbsolute: false, AfterGrace));
        Assert.True(evaluator.ProcessMouseMove(20, 0, isAbsolute: false, AfterGrace));
    }

    [Fact]
    public void Jitter_cancels_itself_out_instead_of_accumulating()
    {
        BlackoutDismissEvaluator evaluator = CreateArmed(threshold: 50);

        // A sensor twitching back and forth travels a long way without ever
        // moving anywhere; displacement is what counts.
        for (int i = 0; i < 200; i++)
        {
            Assert.False(evaluator.ProcessMouseMove(3, 0, isAbsolute: false, AfterGrace));
            Assert.False(evaluator.ProcessMouseMove(-3, 0, isAbsolute: false, AfterGrace));
        }
    }

    [Fact]
    public void Diagonal_movement_uses_the_real_distance()
    {
        BlackoutDismissEvaluator evaluator = CreateArmed(threshold: 50);

        // 30 across and 40 down is exactly 50 away.
        Assert.False(evaluator.ProcessMouseMove(30, 0, isAbsolute: false, AfterGrace));
        Assert.True(evaluator.ProcessMouseMove(0, 40, isAbsolute: false, AfterGrace));
    }

    [Fact]
    public void Movement_during_the_grace_period_is_not_banked()
    {
        BlackoutDismissEvaluator evaluator = CreateArmed(threshold: 50);

        Assert.False(evaluator.ProcessMouseMove(49, 0, isAbsolute: false, Start));
        Assert.False(evaluator.ProcessMouseMove(10, 0, isAbsolute: false, AfterGrace));
    }

    [Fact]
    public void Absolute_reports_are_differenced_against_the_first_one()
    {
        // Remote desktop and tablets report absolute positions rather than deltas.
        BlackoutDismissEvaluator evaluator = CreateArmed(threshold: 50);

        Assert.False(evaluator.ProcessMouseMove(10_000, 10_000, isAbsolute: true, AfterGrace));
        Assert.False(evaluator.ProcessMouseMove(10_030, 10_000, isAbsolute: true, AfterGrace));
        Assert.True(evaluator.ProcessMouseMove(10_060, 10_000, isAbsolute: true, AfterGrace));
    }

    [Fact]
    public void Absolute_reports_do_not_wipe_relative_displacement()
    {
        // A desktop with both a mouse and a tablet (or an RDP session) interleaves
        // the two kinds of packet. The absolute ones carry a delta of their own and
        // must add to the total rather than replace it.
        BlackoutDismissEvaluator evaluator = CreateArmed(threshold: 50);

        Assert.False(evaluator.ProcessMouseMove(30, 0, isAbsolute: false, AfterGrace));
        Assert.False(evaluator.ProcessMouseMove(10_000, 10_000, isAbsolute: true, AfterGrace));

        // 30 from the mouse plus 25 from the tablet is past the threshold.
        Assert.True(evaluator.ProcessMouseMove(10_025, 10_000, isAbsolute: true, AfterGrace));
    }

    [Fact]
    public void Absolute_jitter_still_cancels_itself_out()
    {
        BlackoutDismissEvaluator evaluator = CreateArmed(threshold: 50);

        evaluator.ProcessMouseMove(10_000, 10_000, isAbsolute: true, AfterGrace);

        for (int i = 0; i < 200; i++)
        {
            Assert.False(evaluator.ProcessMouseMove(10_040, 10_000, isAbsolute: true, AfterGrace));
            Assert.False(evaluator.ProcessMouseMove(10_000, 10_000, isAbsolute: true, AfterGrace));
        }
    }

    [Fact]
    public void Movement_does_not_dismiss_when_that_trigger_is_off()
    {
        BlackoutDismissEvaluator evaluator = CreateArmed(DismissTriggers.KeyPress);

        Assert.False(evaluator.ProcessMouseMove(5_000, 5_000, isAbsolute: false, AfterGrace));
    }

    [Fact]
    public void Disarming_stops_everything()
    {
        BlackoutDismissEvaluator evaluator = CreateArmed();
        evaluator.Disarm();

        Assert.False(evaluator.ProcessKey(VkA, isKeyDown: true, AfterGrace));
        Assert.False(evaluator.ProcessMouseButton(AfterGrace));
    }

    [Fact]
    public void Re_arming_clears_movement_and_the_ignored_keys()
    {
        BlackoutDismissEvaluator evaluator = CreateArmed(threshold: 50, keysHeld: new[] { VkB });
        evaluator.ProcessMouseMove(45, 0, isAbsolute: false, AfterGrace);

        DateTime secondStart = AfterGrace + TimeSpan.FromMinutes(5);
        evaluator.Arm(secondStart);
        DateTime secondAfterGrace = secondStart + TimeSpan.FromSeconds(1);

        Assert.False(evaluator.ProcessMouseMove(10, 0, isAbsolute: false, secondAfterGrace));
        Assert.True(evaluator.ProcessKey(VkB, isKeyDown: true, secondAfterGrace));
    }
}
