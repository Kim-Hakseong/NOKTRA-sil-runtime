using System.Diagnostics;

namespace Sil.Core.Timing;

/// <summary>
/// Injected real-time source. Only used by the optional wall-clock pacing mode; the
/// deterministic core never consults it. Injection keeps pacing testable without sleeping.
/// </summary>
public interface IWallClock
{
    /// <summary>Monotonic seconds since an arbitrary origin.</summary>
    double NowSeconds { get; }

    /// <summary>
    /// Blocks until <see cref="NowSeconds"/> reaches <paramref name="targetSeconds"/>.
    /// Returns immediately if the target is already in the past.
    /// </summary>
    void WaitUntil(double targetSeconds, CancellationToken cancellationToken);
}

/// <summary>Monotonic wall clock backed by <see cref="Stopwatch"/>.</summary>
public sealed class StopwatchWallClock : IWallClock
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public double NowSeconds => _sw.Elapsed.TotalSeconds;

    public void WaitUntil(double targetSeconds, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double remaining = targetSeconds - NowSeconds;
            if (remaining <= 0.0)
            {
                return;
            }

            // Coarse sleep for the bulk of the wait, then spin for the tail to limit jitter.
            if (remaining > 0.002)
            {
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(remaining - 0.001));
            }
            else
            {
                Thread.SpinWait(64);
            }
        }
    }
}

/// <summary>
/// Wall clock whose time only moves when the caller moves it. Used for deterministic tests of
/// pacing behaviour and for headless batch runs that must not sleep.
/// </summary>
public sealed class ManualWallClock : IWallClock
{
    private double _now;

    public ManualWallClock(double startSeconds = 0.0)
    {
        _now = startSeconds;
    }

    public double NowSeconds => _now;

    /// <summary>Number of <see cref="WaitUntil"/> calls that actually had to wait.</summary>
    public int WaitCount { get; private set; }

    /// <summary>Total virtual seconds spent inside <see cref="WaitUntil"/>.</summary>
    public double TotalWaitedSeconds { get; private set; }

    public void Advance(double seconds)
    {
        if (seconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "Time must not move backwards.");
        }

        _now += seconds;
    }

    public void WaitUntil(double targetSeconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (targetSeconds <= _now)
        {
            return;
        }

        TotalWaitedSeconds += targetSeconds - _now;
        WaitCount++;
        _now = targetSeconds;
    }
}
