using Sil.Core.Timing;

namespace Sil.Core.Engine;

/// <summary>How the fixed-step loop relates to real time.</summary>
public enum TimingMode
{
    /// <summary>
    /// Run as fast as possible on virtual time. This is the reference mode: results are
    /// bit-identical regardless of machine speed.
    /// </summary>
    Virtual = 0,

    /// <summary>
    /// Pace each cycle against an <see cref="IWallClock"/> so one simulated second takes about
    /// one real second. A convenience for live viewing only; it never changes computed results.
    /// </summary>
    WallClockSynced = 1,
}

/// <summary>Engine configuration.</summary>
public sealed class EngineOptions
{
    /// <summary>Timing mode. Defaults to <see cref="TimingMode.Virtual"/>.</summary>
    public TimingMode TimingMode { get; init; } = TimingMode.Virtual;

    /// <summary>
    /// Real-time source used by <see cref="TimingMode.WallClockSynced"/>. Defaults to a
    /// <see cref="StopwatchWallClock"/>.
    /// </summary>
    public IWallClock? WallClock { get; init; }

    /// <summary>
    /// When wall-clock synced and the loop falls behind by more than this many cycles, the
    /// deadline is rebased to now instead of trying to catch up. Zero disables rebasing.
    /// </summary>
    public long MaxCatchUpSteps { get; init; } = 10;
}

/// <summary>Why a run loop returned.</summary>
public enum StopReason
{
    /// <summary>The requested step count or end time was reached.</summary>
    Completed = 0,

    /// <summary>A caller invoked <c>RequestStop</c>.</summary>
    StopRequested = 1,

    /// <summary>The supplied cancellation token was cancelled.</summary>
    Cancelled = 2,
}

/// <summary>Outcome of one run loop.</summary>
/// <param name="StepsExecuted">Number of cycles executed by this call.</param>
/// <param name="EndStepIndex">Clock step index after the run.</param>
/// <param name="EndTime">Simulation time after the run, in seconds.</param>
/// <param name="Reason">Why the loop returned.</param>
/// <param name="OverrunCount">Cycles that missed their wall-clock deadline (0 in virtual mode).</param>
public readonly record struct RunResult(
    long StepsExecuted,
    long EndStepIndex,
    double EndTime,
    StopReason Reason,
    long OverrunCount);
