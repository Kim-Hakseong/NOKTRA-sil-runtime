using Sil.Core.Engine;

namespace Sil.Core.Runtime;

/// <summary>
/// Drives a <see cref="SimEngine"/> from a background thread so an interactive shell stays
/// responsive. The host owns nothing about the simulation: it only decides when the loop runs,
/// so the deterministic core is untouched by the fact that a UI is watching.
/// </summary>
public sealed class SimulationHost : IDisposable
{
    private readonly object _gate = new();
    private readonly SimEngine _engine;

    private CancellationTokenSource? _cts;
    private Task<RunResult>? _run;
    private bool _disposed;

    public SimulationHost(SimEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    /// <summary>The engine being driven.</summary>
    public SimEngine Engine => _engine;

    /// <summary>True while a run loop is in flight.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _run is { IsCompleted: false };
            }
        }
    }

    /// <summary>Current simulation time in seconds.</summary>
    public double Time => _engine.Time;

    /// <summary>Cycles completed since the last reset.</summary>
    public long StepIndex => _engine.StepIndex;

    /// <summary>Result of the most recently completed run.</summary>
    public RunResult? LastResult { get; private set; }

    /// <summary>
    /// Starts a run on a background thread and returns a task that completes when the loop
    /// stops. Passing null runs until <see cref="PauseAsync"/> or disposal.
    /// </summary>
    public Task<RunResult> StartAsync(double? untilSimulationTime = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_run is { IsCompleted: false })
            {
                throw new InvalidOperationException("The simulation is already running.");
            }

            var cts = new CancellationTokenSource();
            _cts = cts;

            // LongRunning, not the thread pool. A free run is an unbounded CPU-bound loop that
            // never yields; on a pool thread it starves every other queued work item, including
            // the continuation that is waiting for this very loop to stop.
            Task<RunResult> run = Task.Factory.StartNew(
                () =>
                {
                    RunResult result = untilSimulationTime is { } end
                        ? _engine.RunUntil(end, cts.Token)
                        : _engine.RunFree(cts.Token);

                    LastResult = result;
                    return result;
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            _run = run;
            return run;
        }
    }

    /// <summary>Asks the loop to stop at the next cycle boundary and waits for it.</summary>
    public async Task<RunResult?> PauseAsync()
    {
        Task<RunResult>? run;
        lock (_gate)
        {
            run = _run;
            if (run is null || run.IsCompleted)
            {
                return LastResult;
            }
        }

        _engine.RequestStop();
        return await run.ConfigureAwait(false);
    }

    /// <summary>Executes exactly one cycle. Not valid while a run is in flight.</summary>
    public void StepOnce()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfRunning();
        _engine.StepOnce();
    }

    /// <summary>Returns the simulation to t=0. Not valid while a run is in flight.</summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfRunning();
        _engine.Reset();
        LastResult = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Task<RunResult>? run;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            run = _run;
            cts = _cts;
            _run = null;
            _cts = null;
        }

        _engine.RequestStop();
        cts?.Cancel();

        bool stopped = true;
        try
        {
            stopped = run is null || run.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The loop was cancelled; nothing further to report at teardown.
        }

        // Only dispose the token source once the loop can no longer touch its token.
        if (stopped)
        {
            cts?.Dispose();
        }
    }

    private void ThrowIfRunning()
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("Pause the simulation before stepping or resetting it.");
        }
    }
}
