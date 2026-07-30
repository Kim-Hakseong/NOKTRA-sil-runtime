using System.Text;
using Sil.Core.Channels;
using Sil.Core.Engine;
using Sil.Core.Logging;
using Sil.Core.Models.Builtin;
using Sil.Core.Stimulus;
using Sil.Core.Systems;
using Sil.Core.Timing;
using Xunit;

namespace Sil.Core.Tests;

public class CsvChannelLoggerTests
{
    private static ChannelTable Table()
    {
        var builder = new ChannelTableBuilder();
        builder.Add("Speed", "m/s");
        builder.Add("Temp", "degC");
        return builder.Build();
    }

    [Fact]
    public void HeaderCarriesTheTimeColumnAndChannelUnits()
    {
        ChannelTable channels = Table();
        var writer = new StringWriter();
        using var logger = new CsvChannelLogger(channels, writer);
        var engine = new SimEngine(new SimClock(SimRate.FromHz(10)), [logger]);

        engine.RunSteps(1);
        logger.Flush();

        string[] lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("t,Speed[m/s],Temp[degC]", lines[0]);
        Assert.Equal(["Speed", "Temp"], logger.Columns);
    }

    [Fact]
    public void EachCycleWritesOneRowOfTimeAndValues()
    {
        ChannelTable channels = Table();
        var writer = new StringWriter();
        using var logger = new CsvChannelLogger(channels, writer);
        var ramp = new DelegateSimTask("ramp", ctx => channels.Set("Speed", ctx.StepIndex));
        var engine = new SimEngine(new SimClock(SimRate.FromHz(10)), [ramp, logger]);

        engine.RunSteps(3);
        logger.Flush();

        string[] lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.Equal("0,0,0", lines[1]);
        Assert.Equal("0.1,1,0", lines[2]);
        Assert.Equal("0.2,2,0", lines[3]);
        Assert.Equal(3, logger.RowsWritten);
    }

    [Fact]
    public void SelectedChannelsAreLoggedInTheRequestedOrder()
    {
        ChannelTable channels = Table();
        channels.Set("Speed", 1.0);
        channels.Set("Temp", 2.0);
        var writer = new StringWriter();
        using var logger = new CsvChannelLogger(channels, writer, ["Temp"]);
        var engine = new SimEngine(new SimClock(SimRate.FromHz(10)), [logger]);

        engine.RunSteps(1);
        logger.Flush();

        string[] lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("t,Temp[degC]", lines[0]);
        Assert.Equal("0,2", lines[1]);
    }

    [Fact]
    public void DecimationLogsEveryNthCycle()
    {
        ChannelTable channels = Table();
        var writer = new StringWriter();
        using var logger = new CsvChannelLogger(channels, writer, decimation: 4);
        var engine = new SimEngine(new SimClock(SimRate.FromHz(100)), [logger]);

        engine.RunSteps(20);
        logger.Flush();

        Assert.Equal(5, logger.RowsWritten);
    }

    [Fact]
    public void LineEndingsAreAlwaysLf()
    {
        ChannelTable channels = Table();
        var writer = new StringWriter { NewLine = "\r\n" };
        using var logger = new CsvChannelLogger(channels, writer);
        var engine = new SimEngine(new SimClock(SimRate.FromHz(10)), [logger]);

        engine.RunSteps(2);
        logger.Flush();

        Assert.DoesNotContain('\r', writer.ToString());
    }

    [Fact]
    public void ValuesRoundTripThroughTheirTextForm()
    {
        ChannelTable channels = Table();
        var writer = new StringWriter();
        using var logger = new CsvChannelLogger(channels, writer, ["Speed"]);
        double awkward = 1.0 / 3.0;
        var task = new DelegateSimTask("set", _ => channels.Set("Speed", awkward));
        var engine = new SimEngine(new SimClock(SimRate.FromHz(10)), [task, logger]);

        engine.RunSteps(1);
        logger.Flush();

        string cell = writer.ToString().Split('\n')[1].Split(',')[1];
        Assert.Equal(awkward, double.Parse(cell, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheSameScenarioRunTwiceProducesByteIdenticalFiles()
    {
        string first = Path.Combine(Path.GetTempPath(), $"sil-log-a-{Guid.NewGuid():N}.csv");
        string second = Path.Combine(Path.GetTempPath(), $"sil-log-b-{Guid.NewGuid():N}.csv");

        try
        {
            RunLoggedScenario(first);
            RunLoggedScenario(second);

            byte[] a = File.ReadAllBytes(first);
            byte[] b = File.ReadAllBytes(second);

            Assert.Equal(a, b);
            Assert.True(a.Length > 0);
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public void FilesAreWrittenAsUtf8WithoutABom()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sil-log-{Guid.NewGuid():N}.csv");
        try
        {
            RunLoggedScenario(path);

            byte[] bytes = File.ReadAllBytes(path);
            byte[] bom = Encoding.UTF8.GetPreamble();

            Assert.False(bytes.Take(bom.Length).SequenceEqual(bom));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void RunLoggedScenario(string path)
    {
        var builder = new SilSystemBuilder();
        builder.AddModel(new MassSpringDamperModel("msd", mass: 1.5, damping: 0.6, stiffness: 8.0));
        builder.AddChannel("Force", "N");
        builder.AddChannel("Pos", "m");
        builder.AddChannel("Vel", "m/s");
        builder.Map("msd", "F", "Force");
        builder.Map("msd", "x", "Pos");
        builder.Map("msd", "v", "Vel");

        using SilSystem system = builder.Build();
        var stimulus = new StimulusTask(system.Channels,
            [new StimulusBinding("Force", new SineProfile(3.0, 0.7))]);
        using CsvChannelLogger logger = CsvChannelLogger.ToFile(system.Channels, path);

        SimEngine engine = system.CreateEngine(
            SimRate.FromHz(200), stimulus: [stimulus], recorders: [logger]);
        engine.RunUntil(5.0);
        logger.Flush();
    }

    [Fact]
    public void UnknownChannelsAndBadDecimationAreRejected()
    {
        ChannelTable channels = Table();
        var writer = new StringWriter();

        Assert.Throws<ArgumentException>(() => new CsvChannelLogger(channels, writer, ["Nope"]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CsvChannelLogger(channels, writer, decimation: 0));
    }

    [Fact]
    public void UseAfterDisposeIsRejected()
    {
        ChannelTable channels = Table();
        var writer = new StringWriter();
        var logger = new CsvChannelLogger(channels, writer);
        logger.Dispose();

        var ctx = new StepContext(0, 0.0, 0.1);
        Assert.Throws<ObjectDisposedException>(() => logger.Step(in ctx));
        Assert.Throws<ObjectDisposedException>(logger.Flush);

        logger.Dispose();
    }
}

public class LimitMonitorTests
{
    private static ChannelTable OneChannel()
    {
        var builder = new ChannelTableBuilder();
        builder.Add("Value");
        return builder.Build();
    }

    /// <summary>Drives a channel through a fixed sequence, one sample per cycle.</summary>
    private static ScenarioResult RunSequence(double[] sequence, ChannelLimit limit)
    {
        ChannelTable channels = OneChannel();
        var driver = new DelegateSimTask("drive", ctx =>
            channels.Set("Value", sequence[(int)ctx.StepIndex]));
        var monitor = new LimitMonitor(channels, [limit]);
        var engine = new SimEngine(new SimClock(SimRate.FromHz(10)), [driver, monitor]);

        engine.RunSteps(sequence.Length);
        return monitor.Finish();
    }

    [Fact]
    public void GoldenSequence_ProducesOneHighViolationAndAFailVerdict()
    {
        ScenarioResult result = RunSequence(
            GoldenVectors.LimitSequence,
            new ChannelLimit("Value", GoldenVectors.LimitLow, GoldenVectors.LimitHigh));

        Assert.Equal(ScenarioVerdict.Fail, result.Verdict);
        Assert.False(result.Passed);
        Assert.Equal(1, GoldenVectors.LimitExpectedViolationCount);
        LimitViolation violation = Assert.Single(result.Violations);
        Assert.Equal(LimitViolationKind.High, violation.Kind);
        Assert.Equal(GoldenVectors.LimitExpectedViolationStepIndex, violation.FirstStepIndex);
        Assert.Equal(GoldenVectors.LimitHigh, violation.Limit);
        Assert.Equal(5.6, violation.PeakValue);
        Assert.Equal(1, violation.SampleCount);
        Assert.Equal(0.1, violation.FirstTime, 1e-12);
    }

    [Fact]
    public void AllSamplesInBand_Passes()
    {
        ScenarioResult result = RunSequence([4.6, 5.0, 5.4], new ChannelLimit("Value", 4.5, 5.5));

        Assert.Equal(ScenarioVerdict.Pass, result.Verdict);
        Assert.Empty(result.Violations);
        Assert.Equal(3, result.SamplesEvaluated);
    }

    [Fact]
    public void LimitsAreInclusive()
    {
        ScenarioResult result = RunSequence([4.5, 5.5], new ChannelLimit("Value", 4.5, 5.5));

        Assert.Equal(ScenarioVerdict.Pass, result.Verdict);
    }

    [Fact]
    public void ALowExcursionIsRecordedWithItsMinimum()
    {
        ScenarioResult result = RunSequence([5.0, 4.0, 3.2, 4.9], new ChannelLimit("Value", 4.5, 5.5));

        LimitViolation violation = Assert.Single(result.Violations);
        Assert.Equal(LimitViolationKind.Low, violation.Kind);
        Assert.Equal(3.2, violation.PeakValue);
        Assert.Equal(1.3, violation.PeakExcess, 1e-12);
        Assert.Equal(1, violation.FirstStepIndex);
        Assert.Equal(2, violation.LastStepIndex);
        Assert.Equal(2, violation.SampleCount);
    }

    [Fact]
    public void AContinuousExcursionIsOneEventNotOnePerSample()
    {
        ScenarioResult result = RunSequence([6.0, 6.1, 6.2, 6.3], new ChannelLimit("Value", 4.5, 5.5));

        LimitViolation violation = Assert.Single(result.Violations);
        Assert.Equal(4, violation.SampleCount);
        Assert.Equal(6.3, violation.PeakValue);
        Assert.Equal(0.3, violation.LastTime, 1e-12);
    }

    [Fact]
    public void RecoveringAndLeavingAgainProducesTwoEvents()
    {
        ScenarioResult result = RunSequence([6.0, 5.0, 6.0], new ChannelLimit("Value", 4.5, 5.5));

        Assert.Equal(2, result.Violations.Count);
        Assert.Equal(0, result.Violations[0].FirstStepIndex);
        Assert.Equal(2, result.Violations[1].FirstStepIndex);
    }

    [Fact]
    public void CrossingStraightFromHighToLowSplitsIntoTwoEvents()
    {
        ScenarioResult result = RunSequence([6.0, 3.0], new ChannelLimit("Value", 4.5, 5.5));

        Assert.Equal(2, result.Violations.Count);
        Assert.Equal(LimitViolationKind.High, result.Violations[0].Kind);
        Assert.Equal(LimitViolationKind.Low, result.Violations[1].Kind);
    }

    [Fact]
    public void AnExcursionStillOpenAtTheEndIsClosedByFinish()
    {
        ChannelTable channels = OneChannel();
        var driver = new DelegateSimTask("drive", _ => channels.Set("Value", 9.0));
        var monitor = new LimitMonitor(channels, [new ChannelLimit("Value", 0.0, 1.0)]);
        var engine = new SimEngine(new SimClock(SimRate.FromHz(10)), [driver, monitor]);

        engine.RunSteps(3);

        Assert.Empty(monitor.Violations);   // still open

        ScenarioResult result = monitor.Finish();

        LimitViolation violation = Assert.Single(result.Violations);
        Assert.Equal(3, violation.SampleCount);
    }

    [Fact]
    public void ViolationsAcrossChannelsAreOrderedByStartTime()
    {
        var builder = new ChannelTableBuilder();
        builder.Add("A");
        builder.Add("B");
        ChannelTable channels = builder.Build();

        var driver = new DelegateSimTask("drive", ctx =>
        {
            channels.Set("A", ctx.StepIndex == 2 ? 99.0 : 0.0);
            channels.Set("B", ctx.StepIndex == 0 ? 99.0 : 0.0);
        });
        var monitor = new LimitMonitor(channels,
            [new ChannelLimit("A", -1.0, 1.0), new ChannelLimit("B", -1.0, 1.0)]);
        var engine = new SimEngine(new SimClock(SimRate.FromHz(10)), [driver, monitor]);

        engine.RunSteps(4);
        ScenarioResult result = monitor.Finish();

        Assert.Equal(2, result.Violations.Count);
        Assert.Equal("B", result.Violations[0].ChannelName);
        Assert.Equal("A", result.Violations[1].ChannelName);
    }

    [Fact]
    public void ResetClearsPreviousViolations()
    {
        ChannelTable channels = OneChannel();
        var driver = new DelegateSimTask("drive", _ => channels.Set("Value", 99.0));
        var monitor = new LimitMonitor(channels, [new ChannelLimit("Value", 0.0, 1.0)]);
        var engine = new SimEngine(new SimClock(SimRate.FromHz(10)), [driver, monitor]);

        engine.RunSteps(2);
        monitor.Finish();
        Assert.NotEmpty(monitor.Violations);

        engine.Reset();

        Assert.Empty(monitor.Violations);
        Assert.Equal(ScenarioVerdict.Pass, monitor.Result.Verdict);
    }

    [Fact]
    public void ViolationToStringNamesTheChannelAndTime()
    {
        ScenarioResult result = RunSequence(
            GoldenVectors.LimitSequence,
            new ChannelLimit("Value", GoldenVectors.LimitLow, GoldenVectors.LimitHigh));

        string text = result.Violations[0].ToString();

        Assert.Contains("Value", text, StringComparison.Ordinal);
        Assert.Contains("High", text, StringComparison.Ordinal);
    }

    [Fact]
    public void InvertedNonFiniteAndUnknownLimitsAreRejected()
    {
        ChannelTable channels = OneChannel();

        Assert.Throws<ArgumentException>(() =>
            new LimitMonitor(channels, [new ChannelLimit("Value", 5.0, 1.0)]));
        Assert.Throws<ArgumentException>(() =>
            new LimitMonitor(channels, [new ChannelLimit("Value", double.NaN, 1.0)]));
        Assert.Throws<ArgumentException>(() =>
            new LimitMonitor(channels, [new ChannelLimit("Missing", 0.0, 1.0)]));
    }

    [Fact]
    public void MonitoringAModelledSystemFailsOnAnOvershoot()
    {
        var builder = new SilSystemBuilder();
        // Lightly damped: a unit step overshoots well past the plateau.
        builder.AddModel(new MassSpringDamperModel("msd", mass: 1.0, damping: 0.2, stiffness: 1.0));
        builder.AddChannel("Force", "N");
        builder.AddChannel("Pos", "m");
        builder.Map("msd", "F", "Force");
        builder.Map("msd", "x", "Pos");

        using SilSystem system = builder.Build();
        var stimulus = new StimulusTask(system.Channels,
            [new StimulusBinding("Force", new StepProfile(0.0, 0.0, 1.0))]);
        var monitor = new LimitMonitor(system.Channels, [new ChannelLimit("Pos", -0.1, 1.1)]);

        SimEngine engine = system.CreateEngine(
            SimRate.FromHz(500), stimulus: [stimulus], recorders: [monitor]);
        engine.RunUntil(20.0);
        ScenarioResult result = monitor.Finish();

        Assert.Equal(ScenarioVerdict.Fail, result.Verdict);
        Assert.Equal(LimitViolationKind.High, result.Violations[0].Kind);
        Assert.True(result.Violations[0].PeakValue > 1.1);

        // A band wide enough for the overshoot passes the same run.
        var wide = new LimitMonitor(system.Channels, [new ChannelLimit("Pos", -0.5, 2.5)]);
        SimEngine second = system.CreateEngine(
            SimRate.FromHz(500), stimulus: [stimulus], recorders: [wide]);
        second.RunUntil(20.0);

        Assert.Equal(ScenarioVerdict.Pass, wide.Finish().Verdict);
    }
}

public class LimitRetentionTests
{
    [Fact]
    public void ChatteringAcrossALimitKeepsTheCountExactButBoundsTheRecords()
    {
        var builder = new ChannelTableBuilder();
        builder.Add("Value");
        ChannelTable channels = builder.Build();

        // Alternate in and out of band on every cycle: one excursion event per two cycles.
        var driver = new DelegateSimTask("chatter", ctx =>
            channels.Set("Value", ctx.StepIndex % 2 == 0 ? 9.0 : 0.0));
        var monitor = new LimitMonitor(channels, [new ChannelLimit("Value", -1.0, 1.0)],
            maxRetainedViolations: 10);
        var engine = new SimEngine(new SimClock(SimRate.FromHz(100)), [driver, monitor]);

        engine.RunSteps(200);
        ScenarioResult result = monitor.Finish();

        Assert.Equal(ScenarioVerdict.Fail, result.Verdict);
        Assert.Equal(100, result.TotalViolationCount);
        Assert.Equal(10, result.Violations.Count);
        Assert.Equal(90, result.DroppedViolationCount);
    }

    [Fact]
    public void ResetClearsTheTotalCountAsWellAsTheRecords()
    {
        var builder = new ChannelTableBuilder();
        builder.Add("Value");
        ChannelTable channels = builder.Build();

        var driver = new DelegateSimTask("out", _ => channels.Set("Value", 9.0));
        var monitor = new LimitMonitor(channels, [new ChannelLimit("Value", -1.0, 1.0)]);
        var engine = new SimEngine(new SimClock(SimRate.FromHz(100)), [driver, monitor]);

        engine.RunSteps(5);
        monitor.Finish();
        Assert.Equal(1, monitor.TotalViolationCount);

        engine.Reset();

        Assert.Equal(0, monitor.TotalViolationCount);
        Assert.Equal(0, monitor.Result.DroppedViolationCount);
    }

    [Fact]
    public void ARetentionCapBelowOneIsRejected()
    {
        var builder = new ChannelTableBuilder();
        builder.Add("Value");
        ChannelTable channels = builder.Build();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LimitMonitor(channels, [new ChannelLimit("Value", 0.0, 1.0)], maxRetainedViolations: 0));
    }
}
