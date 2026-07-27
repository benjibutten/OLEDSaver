namespace OLEDSaver.Services;

/// <summary>
/// The rule behind "blank the screen on its own after a while". Kept separate
/// from the timer that drives it so every branch is unit testable.
/// </summary>
public static class IdleBlackoutEvaluator
{
    /// <param name="isEnabled">The idle blackout setting.</param>
    /// <param name="idleDuration">How long the session has had no input at all.</param>
    /// <param name="threshold">How long it has to be idle first.</param>
    /// <param name="isBlackoutActive">Already blacked out; nothing to do.</param>
    /// <param name="skipWhenFullscreen">The "not while something is full-screen" setting.</param>
    /// <param name="isForegroundFullscreen">
    /// A full-screen window owns the foreground. Watching a film generates no
    /// input for two hours, and blanking the screen then is the one behaviour
    /// users hate most about idle screen blanking.
    /// </param>
    public static bool ShouldBlackout(
        bool isEnabled,
        TimeSpan idleDuration,
        TimeSpan threshold,
        bool isBlackoutActive,
        bool skipWhenFullscreen,
        bool isForegroundFullscreen)
    {
        if (!isEnabled || isBlackoutActive)
            return false;

        if (threshold <= TimeSpan.Zero || idleDuration < threshold)
            return false;

        if (skipWhenFullscreen && isForegroundFullscreen)
            return false;

        return true;
    }
}
