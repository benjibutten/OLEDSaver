using OLEDSaver.Services;
using Xunit;

namespace OLEDSaver.Tests;

public class IdleBlackoutEvaluatorTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(10);

    [Fact]
    public void Blacks_out_once_the_idle_threshold_is_reached()
    {
        Assert.True(Evaluate(idleMinutes: 10));
        Assert.True(Evaluate(idleMinutes: 42));
    }

    [Fact]
    public void Waits_until_the_threshold_is_reached()
    {
        Assert.False(Evaluate(idleMinutes: 0));
        Assert.False(Evaluate(idleMinutes: 9));
    }

    [Fact]
    public void Does_nothing_when_the_feature_is_off()
    {
        Assert.False(Evaluate(idleMinutes: 30, isEnabled: false));
    }

    [Fact]
    public void Does_nothing_when_already_blacked_out()
    {
        // The idle clock keeps running while the screen is black; without this the
        // watcher would try to blank it again on every tick.
        Assert.False(Evaluate(idleMinutes: 30, isBlackoutActive: true));
    }

    [Fact]
    public void Holds_back_for_a_fullscreen_app()
    {
        Assert.False(Evaluate(idleMinutes: 30, isForegroundFullscreen: true));
    }

    [Fact]
    public void Blacks_out_over_a_fullscreen_app_when_that_guard_is_off()
    {
        Assert.True(Evaluate(idleMinutes: 30, isForegroundFullscreen: true, skipWhenFullscreen: false));
    }

    [Fact]
    public void A_zero_threshold_never_fires()
    {
        // Guards against a hand-edited settings file blanking the screen constantly.
        Assert.False(IdleBlackoutEvaluator.ShouldBlackout(
            isEnabled: true,
            idleDuration: TimeSpan.FromMinutes(5),
            threshold: TimeSpan.Zero,
            isBlackoutActive: false,
            skipWhenFullscreen: true,
            isForegroundFullscreen: false));
    }

    private static bool Evaluate(
        int idleMinutes,
        bool isEnabled = true,
        bool isBlackoutActive = false,
        bool skipWhenFullscreen = true,
        bool isForegroundFullscreen = false)
    {
        return IdleBlackoutEvaluator.ShouldBlackout(
            isEnabled,
            TimeSpan.FromMinutes(idleMinutes),
            Threshold,
            isBlackoutActive,
            skipWhenFullscreen,
            isForegroundFullscreen);
    }
}
