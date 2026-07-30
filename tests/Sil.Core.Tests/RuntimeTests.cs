using Sil.Core.Channels;
using Sil.Core.Engine;
using Sil.Core.Models.Builtin;
using Sil.Core.Runtime;
using Sil.Core.Systems;
using Sil.Core.Timing;
using Xunit;

namespace Sil.Core.Tests;

public class ChannelTraceTests
{
    [Fact]
    public void SamplesAreReturnedOldestFirst()
    {
        var trace = new ChannelTrace("A", 4);
        trace.Add(0.0, 10.0);
        trace.Add(0.1, 11.0);

        double[] times = new double[4];
        double[] values = new double[4];
        int count = trace.Snapshot(times, values);

        Assert.Equal(2, count);
        Assert.Equal([0.0, 0.1], times[..2]);
        Assert.Equal([10.0, 11.0], values[..2]);
    }

    [Fact]
    public void TheBufferEvictsItsOldestSampleWhenFull()
    {
        var trace = new ChannelTrace("A", 3);
        for (int i = 0; i < 5; i++)
        {
            trace.Add(i, i * 2.0);
        }

        double[] times = new double[3];
        double[] values = new double[3];
        int count = trace.Snapshot(times, values);

        Assert.Equal(3, count);
        Assert.Equal(3, trace.Count);
        Assert.Equal([2.0, 3.0, 4.0], times);
        Assert.Equal([4.0, 6.0, 8.0], values);
    }

    [Fact]
    public void ValueRangeCoversTheRetainedSamplesOnly()
    {
        var trace = new ChannelTrace("A", 3);
        trace.Add(0, -100.0);   // evicted
        trace.Add(1, 1.0);
        trace.Add(2, 5.0);
        trace.Add(3, 3.0);

        (double min, double max) = trace.ValueRange();

        Assert.Equal(1.0, min);
        Assert.Equal(5.0, max);
    }

    [Fact]
    public void AnEmptyTraceReportsAZeroRange()
    {
        var trace = new ChannelTrace("A", 4);

        Assert.Equal((0.0, 0.0), trace.ValueRange());
        Assert.Equal(0, trace.Count);
    }

    [Fact]
    public void ClearDiscardsEverything()
    {
        var trace = new ChannelTrace("A", 4);
        trace.Add(0, 1.0);
        trace.Clear();

        Assert.Equal(0, trace.Count);
    }

    [Fact]
    public async Task ConcurrentWritesAndSnapshotsStayConsistent()
    {
        var trace = new ChannelTrace("A", 64);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        Task writer = Task.Run(() =>
        {
            int i = 0;
            while (!cts.IsCancellationRequested)
            {
                trace.Add(i * 0.001, i);
                i++;
            }
        });

        double[] times = new double[64];
        double[] values = new double[64];
        while (!cts.IsCancellationRequested)
        {
            int count = trace.Snapshot(times, values);
            Assert.InRange(count, 0, 64);
        }

        await writer;
    }

    [Fact]
    public void UndersizedSnapshotBuffersAndBadCapacityAreRejected()
    {
        var trace = new ChannelTrace("A", 4);

        Assert.Throws<ArgumentException>(() => trace.Snapshot(new double[2], new double[4]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelTrace("A", 0));
        Assert.Throws<ArgumentException>(() => new ChannelTrace(" ", 4));
    }
}

public class TraceRecorderTests
{
    private static ChannelTable Table()
    {
        var builder = new ChannelTableBuilder();
        builder.Add("A");
        return builder.Build();
    }

    [Fact]
    public void RecorderFillsTracesFromTheChannelTable()
    {
        ChannelTable channels = Table();
        var trace = new ChannelTrace("A", 16);
        var driver = new DelegateSimTask("drive", ctx => channels.Set("A", ctx.StepIndex));
        var recorder = new TraceRecorder(channels, [trace]);
        var engine = new SimEngine(new SimClock(SimRate.FromHz(10)), [driver, recorder]);

        engine.RunSteps(3);

        double[] times = new double[16];
        double[] values = new double[16];
        int count = trace.Snapshot(times, values);

        Assert.Equal(3, count);
        Assert.Equal([0.0, 1.0, 2.0], values[..3]);
    }

    [Fact]
    public void DecimationRecordsEveryNthCycle()
    {
        ChannelTable channels = Table();
        var trace = new ChannelTrace("A", 64);
        var recorder = new TraceRecorder(channels, [trace], decimation: 5);
        var engine = new SimEngine(new SimClock(SimRate.FromHz(100)), [recorder]);

        engine.RunSteps(50);

        Assert.Equal(10, trace.Count);
    }

    [Fact]
    public void ResetClearsTraces()
    {
        ChannelTable channels = Table();
        var trace = new ChannelTrace("A", 16);
        var recorder = new TraceRecorder(channels, [trace]);
        var engine = new SimEngine(new SimClock(SimRate.FromHz(10)), [recorder]);
        engine.RunSteps(4);

        engine.Reset();

        Assert.Equal(0, trace.Count);
    }

    [Fact]
    public void UnknownChannelsAndBadDecimationAreRejected()
    {
        ChannelTable channels = Table();

        Assert.Throws<ArgumentException>(() =>
            new TraceRecorder(channels, [new ChannelTrace("Nope", 4)]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TraceRecorder(channels, [new ChannelTrace("A", 4)], decimation: 0));
    }
}

public class SimulationHostTests
{
    private static SilSystem BuildSystem()
    {
        var builder = new SilSystemBuilder();
        builder.AddModel(new FirstOrderLagModel("plant", timeConstant: 0.1, initialValue: 1.0));
        builder.AddChannel("X");
        builder.Map("plant", "x", "X");
        return builder.Build();
    }

    [Fact]
    public async Task RunningToAnEndTimeCompletesWithThatTime()
    {
        using SilSystem system = BuildSystem();
        using var host = new SimulationHost(system.CreateEngine(SimRate.FromHz(1000)));

        RunResult result = await host.StartAsync(untilSimulationTime: 1.0);

        Assert.Equal(StopReason.Completed, result.Reason);
        Assert.Equal(1.0, result.EndTime, 1e-9);
        Assert.False(host.IsRunning);
        Assert.Equal(result, host.LastResult);
    }

    [Fact]
    public async Task PauseStopsAFreeRunAndReportsIt()
    {
        using SilSystem system = BuildSystem();
        using var host = new SimulationHost(system.CreateEngine(SimRate.FromHz(1000)));

        Task<RunResult> run = host.StartAsync();
        RunResult? result = await host.PauseAsync();

        Assert.NotNull(result);
        Assert.Equal(StopReason.StopRequested, result.Value.Reason);
        Assert.True(run.IsCompleted);
        Assert.False(host.IsRunning);
    }

    [Fact]
    public async Task PausingAnIdleHostIsHarmless()
    {
        using SilSystem system = BuildSystem();
        using var host = new SimulationHost(system.CreateEngine(SimRate.FromHz(100)));

        Assert.Null(await host.PauseAsync());
    }

    [Fact]
    public async Task StartingTwiceIsRejected()
    {
        using SilSystem system = BuildSystem();
        using var host = new SimulationHost(system.CreateEngine(SimRate.FromHz(1000)));

        Task<RunResult> run = host.StartAsync();
        try
        {
            Assert.Throws<InvalidOperationException>(() => { _ = host.StartAsync(); });
        }
        finally
        {
            await host.PauseAsync();
            await run;
        }
    }

    [Fact]
    public void SteppingAndResettingWorkWhileIdle()
    {
        using SilSystem system = BuildSystem();
        using var host = new SimulationHost(system.CreateEngine(SimRate.FromHz(100)));

        host.StepOnce();
        host.StepOnce();
        Assert.Equal(2, host.StepIndex);

        host.Reset();

        Assert.Equal(0, host.StepIndex);
        Assert.Equal(1.0, system.Channels.Get("X"));
        Assert.Null(host.LastResult);
    }

    [Fact]
    public async Task SteppingOrResettingDuringARunIsRejected()
    {
        using SilSystem system = BuildSystem();
        using var host = new SimulationHost(system.CreateEngine(SimRate.FromHz(1000)));

        Task<RunResult> run = host.StartAsync();
        try
        {
            Assert.Throws<InvalidOperationException>(host.StepOnce);
            Assert.Throws<InvalidOperationException>(host.Reset);
        }
        finally
        {
            await host.PauseAsync();
            await run;
        }
    }

    [Fact]
    public void DisposeStopsAnInFlightRun()
    {
        using SilSystem system = BuildSystem();
        var host = new SimulationHost(system.CreateEngine(SimRate.FromHz(1000)));
        _ = host.StartAsync();

        host.Dispose();

        Assert.False(host.IsRunning);
        host.Dispose();
    }

    [Fact]
    public void UseAfterDisposeIsRejected()
    {
        using SilSystem system = BuildSystem();
        var host = new SimulationHost(system.CreateEngine(SimRate.FromHz(100)));
        host.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = host.StartAsync(); });
        Assert.Throws<ObjectDisposedException>(host.StepOnce);
        Assert.Throws<ObjectDisposedException>(host.Reset);
    }

    [Fact]
    public async Task AHostedRunProducesTheSameNumbersAsADirectOne()
    {
        using SilSystem hosted = BuildSystem();
        using var host = new SimulationHost(hosted.CreateEngine(SimRate.FromHz(1000)));
        await host.StartAsync(untilSimulationTime: 0.5);

        using SilSystem direct = BuildSystem();
        SimEngine engine = direct.CreateEngine(SimRate.FromHz(1000));
        engine.RunUntil(0.5);

        Assert.Equal(
            BitConverter.DoubleToInt64Bits(direct.Channels.Get("X")),
            BitConverter.DoubleToInt64Bits(hosted.Channels.Get("X")));
    }
}

public class SimulationHostStopRaceTests
{
    private static SilSystem BuildSystem()
    {
        var builder = new SilSystemBuilder();
        builder.AddModel(new FirstOrderLagModel("plant", timeConstant: 0.1, initialValue: 1.0));
        builder.AddChannel("X");
        builder.Map("plant", "x", "X");
        return builder.Build();
    }

    /// <summary>
    /// Pausing immediately after starting must always terminate. Before the stop request was made
    /// sticky this deadlocked: the background loop cleared the flag on entry, so a pause that
    /// arrived first was erased and the free run never ended.
    /// </summary>
    [Fact]
    public async Task PausingImmediatelyAfterStartingAlwaysTerminates()
    {
        for (int attempt = 0; attempt < 25; attempt++)
        {
            using SilSystem system = BuildSystem();
            using var host = new SimulationHost(system.CreateEngine(SimRate.FromHz(1000)));

            Task<RunResult> run = host.StartAsync();
            RunResult? paused = await host.PauseAsync();

            Assert.True(run.IsCompleted);
            Assert.NotNull(paused);
            Assert.Equal(StopReason.StopRequested, paused.Value.Reason);
            Assert.False(host.IsRunning);
        }
    }
}
