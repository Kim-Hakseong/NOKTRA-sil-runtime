using System.Globalization;
using System.Text;
using Sil.Core.Channels;
using Sil.Core.Engine;
using Sil.Core.Models.Builtin;
using Sil.Core.Stimulus;
using Sil.Core.Systems;
using Sil.Core.Timing;
using Xunit;

namespace Sil.Core.Tests;

public class StimulusProfileTests
{
    [Fact]
    public void Step_MatchesGoldenVector()
    {
        var profile = new StepProfile(
            GoldenVectors.StepStimulusT0,
            GoldenVectors.StepStimulusBefore,
            GoldenVectors.StepStimulusAfter);

        Assert.Equal(GoldenVectors.StepStimulusBefore, profile.ValueAt(0.99));
        Assert.Equal(GoldenVectors.StepStimulusAfter, profile.ValueAt(1.0));
    }

    [Fact]
    public void Step_HoldsBothLevelsFarFromTheTransition()
    {
        var profile = new StepProfile(1.0, 0.0, 5.0);

        Assert.Equal(0.0, profile.ValueAt(0.0));
        Assert.Equal(5.0, profile.ValueAt(1000.0));
    }

    [Fact]
    public void Ramp_MatchesGoldenVector()
    {
        var profile = new RampProfile(
            startTime: 0.0,
            duration: GoldenVectors.RampStimulusDuration,
            from: 0.0,
            to: GoldenVectors.RampStimulusTo);

        Assert.Equal(GoldenVectors.RampStimulusAtT1, profile.ValueAt(1.0), 1e-12);
    }

    [Fact]
    public void Ramp_HoldsItsEndpointsOutsideTheRampWindow()
    {
        var profile = new RampProfile(startTime: 1.0, duration: 2.0, from: -3.0, to: 7.0);

        Assert.Equal(-3.0, profile.ValueAt(0.0));
        Assert.Equal(-3.0, profile.ValueAt(1.0));
        Assert.Equal(2.0, profile.ValueAt(2.0), 1e-12);
        Assert.Equal(7.0, profile.ValueAt(3.0));
        Assert.Equal(7.0, profile.ValueAt(50.0));
        Assert.Equal(3.0, profile.EndTime);
    }

    [Fact]
    public void Ramp_RejectsNonPositiveDuration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RampProfile(0.0, 0.0, 0.0, 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RampProfile(0.0, -1.0, 0.0, 1.0));
    }

    [Fact]
    public void Sine_MatchesItsClosedForm()
    {
        var profile = new SineProfile(amplitude: 2.0, frequencyHz: 0.25, phaseRadians: 0.0, offset: 1.0);

        Assert.Equal(1.0, profile.ValueAt(0.0), 1e-12);
        Assert.Equal(3.0, profile.ValueAt(1.0), 1e-12);   // quarter period -> peak
        Assert.Equal(1.0, profile.ValueAt(2.0), 1e-12);   // half period -> offset
        Assert.Equal(-1.0, profile.ValueAt(3.0), 1e-12);  // three quarters -> trough
        Assert.Equal(1.0, profile.ValueAt(4.0), 1e-12);   // full period -> offset
    }

    [Fact]
    public void Sine_HoldsItsOffsetBeforeTheStartTime()
    {
        var profile = new SineProfile(amplitude: 1.0, frequencyHz: 1.0, offset: 0.5, startTime: 2.0);

        Assert.Equal(0.5, profile.ValueAt(0.0));
        Assert.Equal(0.5, profile.ValueAt(1.999));
        Assert.Equal(0.5, profile.ValueAt(2.0), 1e-12);
        Assert.Equal(1.5, profile.ValueAt(2.25), 1e-12);
    }

    [Fact]
    public void Sine_RejectsNonPositiveFrequency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SineProfile(1.0, 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SineProfile(1.0, -1.0));
    }

    [Fact]
    public void Constant_HoldsOneValue()
    {
        var profile = new ConstantProfile(4.25);

        Assert.Equal(4.25, profile.ValueAt(-10.0));
        Assert.Equal(4.25, profile.ValueAt(1e6));
    }

    [Fact]
    public void Profiles_RejectNonFiniteParameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConstantProfile(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StepProfile(double.NaN, 0.0, 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SineProfile(double.PositiveInfinity, 1.0));
    }

    [Fact]
    public void Profiles_ArePureFunctionsOfTime()
    {
        IStimulusProfile[] profiles =
        [
            new ConstantProfile(1.0),
            new StepProfile(1.0, 0.0, 5.0),
            new RampProfile(0.0, 2.0, 0.0, 10.0),
            new SineProfile(1.0, 3.0, 0.4, 0.2),
        ];

        foreach (IStimulusProfile profile in profiles)
        {
            // Sampling out of order must not change what a later in-order sample returns.
            double forward = profile.ValueAt(0.75);
            _ = profile.ValueAt(100.0);
            _ = profile.ValueAt(-5.0);

            Assert.Equal(
                BitConverter.DoubleToInt64Bits(forward),
                BitConverter.DoubleToInt64Bits(profile.ValueAt(0.75)));
        }
    }
}

public class CsvPlaybackTests
{
    /// <summary>Generates a CSV body from a function; no fixture files are checked in.</summary>
    private static string BuildCsv(string[] headers, int rows, double dt, Func<int, int, double> cell)
    {
        var sb = new StringBuilder();
        sb.Append("t");
        foreach (string header in headers)
        {
            sb.Append(',').Append(header);
        }

        sb.AppendLine();

        for (int r = 0; r < rows; r++)
        {
            sb.Append((r * dt).ToString("R", CultureInfo.InvariantCulture));
            for (int c = 0; c < headers.Length; c++)
            {
                sb.Append(',').Append(cell(r, c).ToString("R", CultureInfo.InvariantCulture));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    [Fact]
    public void LinearInterpolation_ReproducesTheRampGoldenVector()
    {
        // Two samples describing the golden ramp: 0 -> 10 over 2 s.
        var profile = new CsvPlaybackProfile([0.0, 2.0], [0.0, GoldenVectors.RampStimulusTo]);

        Assert.Equal(GoldenVectors.RampStimulusAtT1, profile.ValueAt(1.0), 1e-12);
    }

    [Fact]
    public void HoldInterpolation_KeepsThePreviousSample()
    {
        var profile = new CsvPlaybackProfile(
            [0.0, 1.0, 2.0], [0.0, 5.0, 9.0], PlaybackInterpolation.Hold);

        Assert.Equal(0.0, profile.ValueAt(0.5));
        Assert.Equal(5.0, profile.ValueAt(1.0));
        Assert.Equal(5.0, profile.ValueAt(1.99));
        Assert.Equal(9.0, profile.ValueAt(2.0));
    }

    [Fact]
    public void OutsideTheTable_TheEndpointsAreHeld()
    {
        var profile = new CsvPlaybackProfile([1.0, 2.0], [3.0, 4.0]);

        Assert.Equal(3.0, profile.ValueAt(0.0));
        Assert.Equal(4.0, profile.ValueAt(99.0));
        Assert.Equal(1.0, profile.StartTime);
        Assert.Equal(2.0, profile.EndTime);
        Assert.Equal(2, profile.SampleCount);
    }

    [Fact]
    public void LoopEndBehaviour_RepeatsWithTheTablePeriod()
    {
        var profile = new CsvPlaybackProfile(
            [0.0, 2.0], [0.0, 10.0], PlaybackInterpolation.Linear, PlaybackEndBehaviour.Loop);

        Assert.Equal(5.0, profile.ValueAt(1.0), 1e-12);
        Assert.Equal(5.0, profile.ValueAt(3.0), 1e-12);
        Assert.Equal(5.0, profile.ValueAt(21.0), 1e-12);
    }

    [Fact]
    public void SingleSampleTable_IsAConstant()
    {
        var profile = new CsvPlaybackProfile([1.5], [42.0]);

        Assert.Equal(42.0, profile.ValueAt(0.0));
        Assert.Equal(42.0, profile.ValueAt(100.0));
    }

    [Fact]
    public void ExactSampleTimes_ReadTheSampleValue()
    {
        var profile = new CsvPlaybackProfile([0.0, 1.0, 2.0], [1.0, -1.0, 3.0]);

        Assert.Equal(-1.0, profile.ValueAt(1.0));
    }

    [Fact]
    public void NonIncreasingTimes_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => new CsvPlaybackProfile([0.0, 0.0], [1.0, 2.0]));
        Assert.Throws<ArgumentException>(() => new CsvPlaybackProfile([1.0, 0.5], [1.0, 2.0]));
    }

    [Fact]
    public void MismatchedOrEmptyOrNonFiniteSamples_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => new CsvPlaybackProfile([0.0, 1.0], [1.0]));
        Assert.Throws<ArgumentException>(() => new CsvPlaybackProfile([], []));
        Assert.Throws<ArgumentException>(() => new CsvPlaybackProfile([0.0], [double.NaN]));
    }

    [Fact]
    public void Reader_ParsesHeaderAndColumns()
    {
        string csv = BuildCsv(["a", "b"], rows: 5, dt: 0.5, cell: (r, c) => (r * 2.0) + c);

        IReadOnlyList<CsvStimulusColumn> columns = CsvStimulusReader.ReadText(csv);

        Assert.Equal(2, columns.Count);
        Assert.Equal("a", columns[0].Name);
        Assert.Equal("b", columns[1].Name);
        Assert.Equal(5, columns[0].Profile.SampleCount);
        Assert.Equal(2.0, columns[0].Profile.EndTime, 1e-12);
        Assert.Equal(4.0, columns[0].Profile.ValueAt(1.0), 1e-12);
        Assert.Equal(5.0, columns[1].Profile.ValueAt(1.0), 1e-12);
    }

    [Fact]
    public void Reader_InterpolatesBetweenGeneratedSamples()
    {
        string csv = BuildCsv(["v"], rows: 3, dt: 1.0, cell: (r, _) => r * 10.0);

        CsvPlaybackProfile profile = CsvStimulusReader.ReadText(csv)[0].Profile;

        Assert.Equal(5.0, profile.ValueAt(0.5), 1e-12);
        Assert.Equal(15.0, profile.ValueAt(1.5), 1e-12);
    }

    [Fact]
    public void Reader_SkipsBlankLinesAndComments()
    {
        const string csv = "# generated\nt,v\n\n0,1\n# midway\n1,2\n";

        CsvPlaybackProfile profile = CsvStimulusReader.ReadText(csv)[0].Profile;

        Assert.Equal(2, profile.SampleCount);
        Assert.Equal(1.5, profile.ValueAt(0.5), 1e-12);
    }

    [Fact]
    public void Reader_ReadsDecimalPointsOnly()
    {
        // A stimulus file must mean the same thing on every machine, so the decimal separator is
        // always '.'. A comma-decimal file is a row-width error, never a silently different value.
        CsvPlaybackProfile profile = CsvStimulusReader.ReadText("t,v\n0,1.5\n1,2.5\n")[0].Profile;
        Assert.Equal(1.5, profile.ValueAt(0.0));
        Assert.Equal(2.0, profile.ValueAt(0.5), 1e-12);

        Assert.Throws<StimulusFormatException>(() => CsvStimulusReader.ReadText("t,v\n0,1,5\n1,2,5\n"));
    }

    [Fact]
    public void Reader_ReadsFromDisk()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sil-stim-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(path, BuildCsv(["v"], rows: 4, dt: 0.25, cell: (r, _) => r));

            CsvPlaybackProfile profile = CsvStimulusReader.ReadFile(path)[0].Profile;

            Assert.Equal(4, profile.SampleCount);
            Assert.Equal(0.75, profile.EndTime, 1e-12);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("t\n0\n", "signal column")]
    [InlineData("t,v\n", "no data rows")]
    [InlineData("t,v\n0,1,2\n", "cells")]
    [InlineData("t,v\n0,abc\n", "not a number")]
    [InlineData("t,v\n0,1\n0,2\n", "strictly increase")]
    public void Reader_RejectsMalformedInput(string csv, string expectedFragment)
    {
        StimulusFormatException ex = Assert.Throws<StimulusFormatException>(() => CsvStimulusReader.ReadText(csv));

        Assert.Contains(expectedFragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public class StimulusTaskTests
{
    private static ChannelTable TwoChannels()
    {
        var builder = new ChannelTableBuilder();
        builder.Add("A");
        builder.Add("B");
        return builder.Build();
    }

    [Fact]
    public void Task_WritesProfileValuesAtTheCycleTime()
    {
        ChannelTable channels = TwoChannels();
        var task = new StimulusTask(channels,
        [
            new StimulusBinding("A", new StepProfile(
                GoldenVectors.StepStimulusT0, GoldenVectors.StepStimulusBefore, GoldenVectors.StepStimulusAfter)),
            new StimulusBinding("B", new RampProfile(0.0, GoldenVectors.RampStimulusDuration, 0.0,
                GoldenVectors.RampStimulusTo)),
        ]);

        var engine = new SimEngine(new SimClock(SimRate.FromHz(100)), [task]);

        engine.RunSteps(99);   // last executed cycle was t = 0.98; clock now at t = 0.99
        Assert.Equal(0.99, engine.Time, 1e-12);
        Assert.Equal(GoldenVectors.StepStimulusBefore, channels.Get("A"));

        engine.RunSteps(1);    // executes the t = 0.99 cycle, clock now at 1.00
        Assert.Equal(GoldenVectors.StepStimulusBefore, channels.Get("A"));

        engine.RunSteps(1);    // executes the t = 1.00 cycle
        Assert.Equal(GoldenVectors.StepStimulusAfter, channels.Get("A"));

        Assert.Equal(GoldenVectors.RampStimulusAtT1, channels.Get("B"), 1e-9);
    }

    [Fact]
    public void Task_AppliesTheZeroTimeValueOnReset()
    {
        ChannelTable channels = TwoChannels();
        var task = new StimulusTask(channels, [new StimulusBinding("A", new ConstantProfile(3.5))]);
        var engine = new SimEngine(new SimClock(SimRate.FromHz(10)), [task]);

        engine.Reset();

        Assert.Equal(3.5, channels.Get("A"));
        Assert.Equal(1, task.BindingCount);
    }

    [Fact]
    public void Task_RejectsUnknownChannels()
    {
        ChannelTable channels = TwoChannels();

        Assert.Throws<ArgumentException>(() =>
            new StimulusTask(channels, [new StimulusBinding("Z", new ConstantProfile(1.0))]));
    }

    [Fact]
    public void Task_RejectsTwoProfilesOnOneChannel()
    {
        ChannelTable channels = TwoChannels();

        ArgumentException ex = Assert.Throws<ArgumentException>(() => new StimulusTask(channels,
        [
            new StimulusBinding("A", new ConstantProfile(1.0)),
            new StimulusBinding("A", new ConstantProfile(2.0)),
        ]));

        Assert.Contains("already has a stimulus", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StimulusDrivesAModelThroughTheChannelTable()
    {
        var builder = new SilSystemBuilder();
        builder.AddModel(new FirstOrderLagModel("plant", timeConstant: 0.05));
        builder.AddChannel("Command");
        builder.AddChannel("Feedback");
        builder.Map("plant", "u", "Command");
        builder.Map("plant", "x", "Feedback");

        using SilSystem system = builder.Build();
        var stimulus = new StimulusTask(system.Channels,
        [
            new StimulusBinding("Command", new StepProfile(
                GoldenVectors.StepStimulusT0, GoldenVectors.StepStimulusBefore, GoldenVectors.StepStimulusAfter)),
        ]);

        SimEngine engine = system.CreateEngine(SimRate.FromHz(1000), stimulus: [stimulus]);

        engine.RunUntil(0.9);
        Assert.Equal(0.0, system.Channels.Get("Feedback"), 1e-12);

        engine.RunUntil(2.0);
        Assert.Equal(GoldenVectors.StepStimulusAfter, system.Channels.Get("Feedback"), 1e-6);
    }

    [Fact]
    public void StimulusDrivenRun_IsReproducible()
    {
        static double Run()
        {
            var builder = new SilSystemBuilder();
            builder.AddModel(new MassSpringDamperModel("msd", mass: 1.0, damping: 0.4, stiffness: 9.0));
            builder.AddChannel("Force", "N");
            builder.AddChannel("Pos", "m");
            builder.Map("msd", "F", "Force");
            builder.Map("msd", "x", "Pos");

            using SilSystem system = builder.Build();
            var stimulus = new StimulusTask(system.Channels,
                [new StimulusBinding("Force", new SineProfile(2.0, 0.5))]);

            SimEngine engine = system.CreateEngine(SimRate.FromHz(500), stimulus: [stimulus]);
            engine.RunUntil(10.0);
            return system.Channels.Get("Pos");
        }

        Assert.Equal(BitConverter.DoubleToInt64Bits(Run()), BitConverter.DoubleToInt64Bits(Run()));
    }
}
