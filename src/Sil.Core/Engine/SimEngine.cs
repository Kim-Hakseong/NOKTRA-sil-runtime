using Sil.Core.Timing;

namespace Sil.Core.Engine;

/// <summary>Lifecycle state of a <see cref="SimEngine"/>.</summary>
public enum EngineState
{
    /// <summary>Reset and ready at t=0, or freshly constructed.</summary>
    Idle = 0,

    /// <summary>Inside a run loop.</summary>
    Running = 1,

    /// <summary>Stopped part-way through; the clock and task state are preserved.</summary>
    Paused = 2,
}

/// <summary>
/// The fixed-cycle executor. It owns the virtual clock and drives an ordered list of tasks
/// exactly once per cycle. Given the same tasks and the same start state, a run always produces
/// the same result — the determinism rule the whole product rests on.
/// </summary>
public sealed class SimEngine
{
    private readonly ISimTask[] _tasks;
    private readonly EngineOptions _options;
    private readonly IWallClock _wallClock;

    private volatile bool _stopRequested;
    private double _wallOrigin;
    private bool _initialized;

    public SimEngine(SimClock clock, IReadOnlyList<ISimTask> tasks, EngineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(tasks);

        for (int i = 0; i < tasks.Count; i++)
        {
            if (tasks[i] is null)
            {
                throw new ArgumentException($"Task at index {i} is null.", nameof(tasks));
            }
        }

        Clock = clock;
        _tasks = [.. tasks];
        _options = options ?? new EngineOptions();
        _wallClock = _options.WallClock ?? new StopwatchWallClock();
    }

    /// <summary>Raised after each cycle completes. Kept synchronous and cheap by design.</summary>
    public event Action<StepContext>? Stepped;

    public SimClock Clock { get; }

    public EngineState State { get; private set; } = EngineState.Idle;

    public IReadOnlyList<ISimTask> Tasks => _tasks;

    public long StepIndex => Clock.StepIndex;

    public double Time => Clock.Time;

    /// <summary>Cycles that missed their wall-clock deadline since the last reset.</summary>
    public long OverrunCount { get; private set; }

    /// <summary>
    /// Returns the engine to t=0 and re-initializes every task. Must be called before the first
    /// step; run loops do it automatically on a fresh engine.
    /// </summary>
    public void Reset()
    {
        ThrowIfRunning();

        Clock.Reset();
        OverrunCount = 0;
        _stopRequested = false;

        StepContext ctx = CurrentContext();
        foreach (ISimTask task in _tasks)
        {
            task.Initialize(in ctx);
        }

        _initialized = true;
        State = EngineState.Idle;
    }

    /// <summary>Executes exactly one cycle. Valid while idle or paused.</summary>
    public void StepOnce()
    {
        ThrowIfRunning();
        EnsureInitialized();

        ExecuteOneCycle();
        State = EngineState.Paused;
    }

    /// <summary>Runs a fixed number of cycles.</summary>
    public RunResult RunSteps(long steps, CancellationToken cancellationToken = default)
    {
        if (steps < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(steps), steps, "Step count must not be negative.");
        }

        long target = checked(Clock.StepIndex + steps);
        return RunCore(() => Clock.StepIndex < target, cancellationToken);
    }

    /// <summary>
    /// Runs until simulation time reaches <paramref name="endTime"/>. The final cycle is the one
    /// that starts strictly before <paramref name="endTime"/>, so the end state is at or just
    /// past the requested time.
    /// </summary>
    public RunResult RunUntil(double endTime, CancellationToken cancellationToken = default)
        => RunCore(() => Clock.Time < endTime - (Clock.Dt * 0.5), cancellationToken);

    /// <summary>Runs until <see cref="RequestStop"/> or cancellation.</summary>
    public RunResult RunFree(CancellationToken cancellationToken = default)
        => RunCore(static () => true, cancellationToken);

    /// <summary>
    /// Asks a running loop to return at the next cycle boundary.
    /// </summary>
    /// <remarks>
    /// A stop requested while the engine is idle carries over to the next run, which then returns
    /// having executed no cycles. That is deliberate: a run started on a background thread has not
    /// necessarily entered its loop by the time the caller gets control back, and a stop request
    /// that lands in that window must not be swallowed. The flag is cleared when a run finishes
    /// and by <see cref="Reset"/>.
    /// </remarks>
    public void RequestStop() => _stopRequested = true;

    private RunResult RunCore(Func<bool> shouldContinue, CancellationToken cancellationToken)
    {
        ThrowIfRunning();
        EnsureInitialized();

        State = EngineState.Running;

        long startIndex = Clock.StepIndex;
        StopReason reason = StopReason.Completed;

        try
        {
            _wallOrigin = _wallClock.NowSeconds - (Clock.StepIndex * Clock.Dt);

            while (shouldContinue())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    reason = StopReason.Cancelled;
                    break;
                }

                if (_stopRequested)
                {
                    reason = StopReason.StopRequested;
                    break;
                }

                if (_options.TimingMode == TimingMode.WallClockSynced)
                {
                    PaceCycle(cancellationToken);
                }

                ExecuteOneCycle();
            }
        }
        catch (OperationCanceledException)
        {
            reason = StopReason.Cancelled;
        }
        finally
        {
            State = EngineState.Paused;
            _stopRequested = false;
        }

        return new RunResult(
            StepsExecuted: Clock.StepIndex - startIndex,
            EndStepIndex: Clock.StepIndex,
            EndTime: Clock.Time,
            Reason: reason,
            OverrunCount: OverrunCount);
    }

    private void PaceCycle(CancellationToken cancellationToken)
    {
        double deadline = _wallOrigin + (Clock.StepIndex * Clock.Dt);
        double now = _wallClock.NowSeconds;

        if (now > deadline)
        {
            OverrunCount++;

            long lateSteps = Clock.Dt > 0.0 ? (long)((now - deadline) / Clock.Dt) : 0;
            if (_options.MaxCatchUpSteps > 0 && lateSteps > _options.MaxCatchUpSteps)
            {
                // Too far behind to catch up honestly; rebase so the loop stays responsive.
                _wallOrigin = now - (Clock.StepIndex * Clock.Dt);
            }

            return;
        }

        _wallClock.WaitUntil(deadline, cancellationToken);
    }

    private void ExecuteOneCycle()
    {
        StepContext ctx = CurrentContext();

        foreach (ISimTask task in _tasks)
        {
            task.Step(in ctx);
        }

        Clock.Advance();
        Stepped?.Invoke(ctx);
    }

    private StepContext CurrentContext() => new(Clock.StepIndex, Clock.Time, Clock.Dt);

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        // An explicit Reset() is meant to discard a pending stop request, but this lazy
        // first-run initialization is an implementation detail and must not: a stop requested
        // before the very first run still has to be honoured.
        bool pendingStop = _stopRequested;
        Reset();
        _stopRequested = pendingStop;
    }

    private void ThrowIfRunning()
    {
        if (State == EngineState.Running)
        {
            throw new InvalidOperationException("The engine is already running.");
        }
    }
}
