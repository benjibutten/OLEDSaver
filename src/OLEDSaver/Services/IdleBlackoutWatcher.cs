using System.Windows.Threading;
using OLEDSaver.Helpers;

namespace OLEDSaver.Services;

public interface IIdleBlackoutOptions
{
    bool IdleBlackoutEnabled { get; }

    int IdleBlackoutMinutes { get; }

    bool SkipIdleBlackoutWhenFullscreen { get; }
}

/// <summary>
/// Polls how long the session has been idle and switches the blackout on when it
/// crosses the configured threshold.
///
/// The timer only runs while the feature is enabled — an app that nobody asked to
/// blank the screen automatically should not wake the CPU at all. Five seconds
/// between ticks bounds how late the blackout can be while keeping the cost to one
/// trivial syscall.
/// </summary>
public sealed class IdleBlackoutWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly IIdleBlackoutOptions _options;
    private readonly Action _requestBlackout;
    private readonly Func<bool> _isBlackoutActive;
    private readonly Func<TimeSpan> _idleDurationProvider;
    private readonly Func<bool> _isForegroundFullscreenProvider;
    private readonly DispatcherTimer _timer = new() { Interval = PollInterval };

    private bool _disposed;

    public IdleBlackoutWatcher(
        IIdleBlackoutOptions options,
        Action requestBlackout,
        Func<bool> isBlackoutActive,
        Func<TimeSpan>? idleDurationProvider = null,
        Func<bool>? isForegroundFullscreenProvider = null)
    {
        _options = options;
        _requestBlackout = requestBlackout;
        _isBlackoutActive = isBlackoutActive;
        _idleDurationProvider = idleDurationProvider ?? NativeInterop.GetIdleDuration;
        _isForegroundFullscreenProvider = isForegroundFullscreenProvider ?? NativeInterop.IsForegroundWindowFullscreen;
        _timer.Tick += OnTick;
    }

    /// <summary>Starts or stops polling to match the current settings.</summary>
    public void Sync()
    {
        if (_disposed)
            return;

        if (_options.IdleBlackoutEnabled)
        {
            if (!_timer.IsEnabled)
                _timer.Start();

            return;
        }

        _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        try
        {
            bool shouldBlackout = IdleBlackoutEvaluator.ShouldBlackout(
                _options.IdleBlackoutEnabled,
                _idleDurationProvider(),
                TimeSpan.FromMinutes(_options.IdleBlackoutMinutes),
                _isBlackoutActive(),
                _options.SkipIdleBlackoutWhenFullscreen,
                _isForegroundFullscreenProvider());

            if (shouldBlackout)
                _requestBlackout();
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warning("Idle blackout check failed.", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
