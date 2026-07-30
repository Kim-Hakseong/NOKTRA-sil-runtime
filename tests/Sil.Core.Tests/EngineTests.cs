using Sil.Core.Engine;
using Sil.Core.Timing;
using Xunit;

namespace Sil.Core.Tests;

public class SimRateTests
{
    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(10, 0.1)]
    [InlineData(100, 0.01)]
    [InlineData(1000, 0.001)]
    public void FromHz_AcceptsSupportedRange(int hz, double expectedDt)
    {
        SimRate rate = SimRate.FromHz(hz);

        Assert.Equal(hz, rate.Hz);
        Assert.Equal(expectedDt, rate.Dt, 1e-12);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public void FromHz_RejectsOutOfRange(int hz)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SimRate.FromHz(hz));
        Assert.False(SimRate.TryFromHz(hz, out _));
    }

    [Fact]
    public void Equality_IsByFrequency()
    {
        Assert.Equal(SimRate.FromHz(50), SimRate.FromHz(50));
        Assert.NotEqual(SimRate.FromHz(50), SimRate.FromHz(51));
        Assert.True(SimRate.FromHz(50) == SimRate.FromHz(50));
        Assert.True(SimRate.FromHz(50) != SimRate.FromHz(51));
    }
}

public class SimClockTests
{
    [Fact]
    public void Time_IsDerivedFromStepIndex_AndDoesNotDrift()
    {
        var clock = new SimClock(SimRate.FromHz(1000));

        for (int i = 0; i < 10_000; i++)
        {
            clock.Advance();
        }

        // Accumulating 0.001 ten thousand times would drift; index * dt does not.
        Assert.Equal(10_000, clock.StepIndex);
        Assert.Equal(10.0, clock.Time, 1e-12);
        Assert.Equal(10.0 * 1000 * clock.Dt, clock.StepIndex * clock.Dt, 1e-12);
    }

    [Fact]
    public void Reset_ReturnsToZero()
    {
        var clock = new SimClock(SimRate.FromHz(100));
        clock.Advance();
        clock.Advance();

        clock.Reset();

        Assert.Equal(0, clock.StepIndex);
        Assert.Equal(0.0, clock.Time);
    }
}

public class SimEngineTests
{
    private static SimEngine BuildCounter(out List<StepContext> seen, int hz = 100, EngineOptions? options = null)
    {
        var log = new List<StepContext>();
        seen = log;
        var task = new DelegateSimTask("counter", ctx => log.Add(ctx), _ => log.Clear());
        return new SimEngine(new SimClock(SimRate.FromHz(hz)), [task], options);
    }

    [Fact]
    public void RunSteps_ExecutesExactlyTheRequestedCycles()
    {
        SimEngine engine = BuildCounter(out List<StepContext> seen);

        RunResult result = engine.RunSteps(5);

        Assert.Equal(5, result.StepsExecuted);
        Assert.Equal(5, engine.StepIndex);
        Assert.Equal(StopReason.Completed, result.Reason);
        Assert.Equal(5, seen.Count);
        Assert.Equal(0.0, seen[0].Time, 1e-12);
        Assert.Equal(0.04, seen[4].Time, 1e-12);
        Assert.Equal(0.01, seen[0].Dt, 1e-12);
    }

    [Fact]
    public void StepOnce_AdvancesOneCycle()
    {
        SimEngine engine = BuildCounter(out List<StepContext> seen);

        engine.StepOnce();
        engine.StepOnce();

        Assert.Equal(2, engine.StepIndex);
        Assert.Equal(2, seen.Count);
        Assert.Equal(EngineState.Paused, engine.State);
    }

    [Fact]
    public void RunUntil_StopsAtRequestedSimulationTime()
    {
        SimEngine engine = BuildCounter(out _, hz: 100);

        RunResult result = engine.RunUntil(1.0);

        Assert.Equal(100, result.EndStepIndex);
        Assert.Equal(1.0, result.EndTime, 1e-12);
    }

    [Fact]
    public void RequestStop_EndsFreeRunAtCycleBoundary()
    {
        var log = new List<long>();
        SimEngine? engine = null;
        var task = new DelegateSimTask("stopper", ctx =>
        {
            log.Add(ctx.StepIndex);
            if (ctx.StepIndex == 9)
            {
                engine!.RequestStop();
            }
        });

        engine = new SimEngine(new SimClock(SimRate.FromHz(100)), [task]);

        RunResult result = engine.RunFree();

        Assert.Equal(StopReason.StopRequested, result.Reason);
        Assert.Equal(10, result.StepsExecuted);
        Assert.Equal(EngineState.Paused, engine.State);
    }

    [Fact]
    public void Cancellation_EndsRunAndIsReported()
    {
        using var cts = new CancellationTokenSource();
        var task = new DelegateSimTask("canceller", ctx =>
        {
            if (ctx.StepIndex == 3)
            {
                cts.Cancel();
            }
        });

        var engine = new SimEngine(new SimClock(SimRate.FromHz(100)), [task]);

        RunResult result = engine.RunFree(cts.Token);

        Assert.Equal(StopReason.Cancelled, result.Reason);
        Assert.Equal(4, result.StepsExecuted);
    }

    [Fact]
    public void Reset_ReinitializesTasksAndClock()
    {
        SimEngine engine = BuildCounter(out List<StepContext> seen);
        engine.RunSteps(3);

        engine.Reset();

        Assert.Equal(0, engine.StepIndex);
        Assert.Empty(seen);
        Assert.Equal(EngineState.Idle, engine.State);
    }

    [Fact]
    public void RunSteps_ResumesFromCurrentClockPosition()
    {
        SimEngine engine = BuildCounter(out _);

        engine.RunSteps(3);
        RunResult second = engine.RunSteps(4);

        Assert.Equal(4, second.StepsExecuted);
        Assert.Equal(7, second.EndStepIndex);
    }

    [Fact]
    public void TasksRunInListOrder_WithinOneCycle()
    {
        var order = new List<string>();
        var a = new DelegateSimTask("a", _ => order.Add("a"));
        var b = new DelegateSimTask("b", _ => order.Add("b"));
        var c = new DelegateSimTask("c", _ => order.Add("c"));

        var engine = new SimEngine(new SimClock(SimRate.FromHz(10)), [a, b, c]);
        engine.RunSteps(2);

        Assert.Equal(["a", "b", "c", "a", "b", "c"], order);
    }

    [Fact]
    public void SteppedEvent_FiresOncePerCycle()
    {
        SimEngine engine = BuildCounter(out _);
        var observed = new List<long>();
        engine.Stepped += ctx => observed.Add(ctx.StepIndex);

        engine.RunSteps(3);

        Assert.Equal([0L, 1L, 2L], observed);
    }

    [Fact]
    public void SameRunTwice_ProducesIdenticalOutput()
    {
        static double[] Run()
        {
            double x = 1.0;
            var task = new DelegateSimTask(
                "decay",
                ctx => x += ctx.Dt * -x,
                _ => x = 1.0);
            var engine = new SimEngine(new SimClock(SimRate.FromHz(10)), [task]);
            engine.RunSteps(10);
            return [x];
        }

        double[] first = Run();
        double[] second = Run();

        Assert.Equal(BitConverter.DoubleToInt64Bits(first[0]), BitConverter.DoubleToInt64Bits(second[0]));
        Assert.Equal(GoldenVectors.EulerDecay10Steps, first[0], GoldenVectors.Tolerance);
    }

    [Fact]
    public void WallClockMode_PacesEachCycleAgainstInjectedClock()
    {
        var wall = new ManualWallClock();
        var options = new EngineOptions { TimingMode = TimingMode.WallClockSynced, WallClock = wall };
        SimEngine engine = BuildCounter(out _, hz: 100, options: options);

        engine.RunSteps(10);

        // Cycle 0 runs immediately; cycles 1..9 each wait one dt.
        Assert.Equal(9, wall.WaitCount);
        Assert.Equal(0.09, wall.TotalWaitedSeconds, 1e-12);
    }

    [Fact]
    public void WallClockMode_CountsOverrunsWhenTheLoopIsLate()
    {
        var wall = new ManualWallClock();
        var options = new EngineOptions { TimingMode = TimingMode.WallClockSynced, WallClock = wall };

        // Each cycle burns 30 ms of wall time against a 10 ms budget.
        var slow = new DelegateSimTask("slow", _ => wall.Advance(0.03));
        var engine = new SimEngine(new SimClock(SimRate.FromHz(100)), [slow], options);

        RunResult result = engine.RunSteps(5);

        Assert.Equal(4, result.OverrunCount);
        Assert.Equal(0, wall.WaitCount);
    }

    [Fact]
    public void WallClockMode_ResultsAreIdenticalToVirtualMode()
    {
        static double RunWith(EngineOptions? options)
        {
            double x = 1.0;
            var task = new DelegateSimTask("decay", ctx => x += ctx.Dt * -x, _ => x = 1.0);
            var engine = new SimEngine(new SimClock(SimRate.FromHz(10)), [task], options);
            engine.RunSteps(10);
            return x;
        }

        double virt = RunWith(null);
        double paced = RunWith(new EngineOptions
        {
            TimingMode = TimingMode.WallClockSynced,
            WallClock = new ManualWallClock(),
        });

        Assert.Equal(BitConverter.DoubleToInt64Bits(virt), BitConverter.DoubleToInt64Bits(paced));
    }

    [Fact]
    public void Constructor_RejectsNullTask()
    {
        Assert.Throws<ArgumentException>(() =>
            new SimEngine(new SimClock(SimRate.FromHz(10)), [null!]));
    }

    [Fact]
    public void RunSteps_RejectsNegativeCount()
    {
        SimEngine engine = BuildCounter(out _);

        Assert.Throws<ArgumentOutOfRangeException>(() => engine.RunSteps(-1));
    }
}

public class StopRequestRaceTests
{
    /// <summary>
    /// A stop requested before the loop has entered must not be swallowed. This is the exact
    /// shape of the UI race: Start hands the loop to a background thread, and Pause can land
    /// before that thread reaches its first cycle check.
    /// </summary>
    [Fact]
    public void AStopRequestedWhileIdleAppliesToTheNextRun()
    {
        var counter = 0;
        var task = new DelegateSimTask("count", _ => counter++);
        var engine = new SimEngine(new SimClock(SimRate.FromHz(100)), [task]);
        engine.Reset();

        engine.RequestStop();
        RunResult result = engine.RunFree();

        Assert.Equal(StopReason.StopRequested, result.Reason);
        Assert.Equal(0, result.StepsExecuted);
        Assert.Equal(0, counter);
    }

    [Fact]
    public void TheStopRequestIsClearedOnceARunHasEnded()
    {
        var task = new DelegateSimTask("noop", _ => { });
        var engine = new SimEngine(new SimClock(SimRate.FromHz(100)), [task]);

        engine.RequestStop();
        engine.RunFree();

        // The carried-over request is spent; the next run is free to proceed.
        RunResult second = engine.RunSteps(3);

        Assert.Equal(3, second.StepsExecuted);
        Assert.Equal(StopReason.Completed, second.Reason);
    }

    [Fact]
    public void ResetClearsAPendingStopRequest()
    {
        var task = new DelegateSimTask("noop", _ => { });
        var engine = new SimEngine(new SimClock(SimRate.FromHz(100)), [task]);

        engine.RequestStop();
        engine.Reset();

        Assert.Equal(3, engine.RunSteps(3).StepsExecuted);
    }
}
