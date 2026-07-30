using System.Globalization;
using System.Text;
using Sil.Core.Engine;
using Sil.Core.Logging;
using Sil.Core.Models;
using Sil.Core.Numerics;
using Sil.Core.Runtime;
using Sil.Core.Scenarios;
using Sil.Core.Systems;
using Sil.Core.Timing;
using Xunit;

namespace Sil.Core.Tests;

/// <summary>Builds scenario documents in code; no fixture files are checked in.</summary>
internal static class ScenarioFixtures
{
    internal static ScenarioDefinition Decay(string integrator = "Rk4", int rateHz = 10) => new(
        Name: "decay",
        Description: "dx/dt = -x from x0 = 1",
        RateHz: rateHz,
        Models:
        [
            new ModelDefinition("plant", "FirstOrderLag", integrator, new()
            {
                ["timeConstant"] = 1.0,
                ["gain"] = 1.0,
                ["initialValue"] = 1.0,
            }),
        ],
        Channels: [new ChannelDeclaration("X", "eu")],
        Mappings: [new MappingDeclaration("plant", "x", "X")],
        Run: new RunSettings(EndTime: 1.0));

    internal static ScenarioDefinition FullyPopulated() => new(
        Name: "fully-populated",
        Description: "exercises every section of the schema",
        RateHz: 500,
        TimingMode: "WallClockSynced",
        Models:
        [
            new ModelDefinition("plant", "MassSpringDamper", "Rk4", new()
            {
                ["mass"] = 1.5,
                ["damping"] = 0.4,
                ["stiffness"] = 6.0,
                ["initialPosition"] = 0.1,
                ["initialVelocity"] = -0.2,
            }),
            new ModelDefinition("lag", "FirstOrderLag", "Euler", new()
            {
                ["timeConstant"] = 0.25,
            }),
        ],
        Channels:
        [
            new ChannelDeclaration("Force", "N", 0.0, "drive"),
            new ChannelDeclaration("Pos", "m", 0.0, "displacement"),
            new ChannelDeclaration("Filtered", "m"),
        ],
        Mappings:
        [
            new MappingDeclaration("plant", "F", "Force"),
            new MappingDeclaration("plant", "x", "Pos", 2.0, 1.0),
            new MappingDeclaration("lag", "x", "Filtered"),
        ],
        Links: [new LinkDeclaration("plant", "x", "lag", "u", 1.0, 0.0)],
        Stimulus:
        [
            new StimulusDeclaration("Force", "Sine", new()
            {
                ["amplitude"] = 2.0,
                ["frequencyHz"] = 0.8,
            }),
        ],
        Limits: [new LimitDeclaration("Pos", -5.0, 5.0)],
        Run: new RunSettings(EndTime: 4.0, LogDecimation: 2, LogChannels: ["Pos", "Filtered"]));
}

public class ScenarioFileTests
{
    [Fact]
    public void ADocumentSurvivesAJsonRoundTrip()
    {
        ScenarioDefinition original = ScenarioFixtures.FullyPopulated();

        string json = ScenarioFile.ToJson(original);
        ScenarioDefinition reloaded = ScenarioFile.FromJson(json);

        // Re-serialising the reloaded document must produce identical text: that proves nothing
        // was lost or reordered, field by field, without asserting on each one.
        Assert.Equal(json, ScenarioFile.ToJson(reloaded));

        Assert.Equal(original.Name, reloaded.Name);
        Assert.Equal(original.RateHz, reloaded.RateHz);
        Assert.Equal("WallClockSynced", reloaded.TimingMode);
        Assert.Equal(2, reloaded.Models!.Count);
        Assert.Equal(1.5, reloaded.Models[0].Parameters!["mass"]);
        Assert.Equal("Euler", reloaded.Models[1].Integrator);
        Assert.Equal(3, reloaded.Channels!.Count);
        Assert.Equal(2.0, reloaded.Mappings![1].ScaleA);
        Assert.Equal("lag", reloaded.Links![0].TargetModel);
        Assert.Equal(0.8, reloaded.Stimulus![0].Parameters!["frequencyHz"]);
        Assert.Equal(-5.0, reloaded.Limits![0].Low);
        Assert.Equal(4.0, reloaded.Run!.EndTime);
        Assert.Equal(["Pos", "Filtered"], reloaded.Run.LogChannels!);
    }

    [Fact]
    public void SavingAndLoadingFromDiskPreservesTheDocument()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sil-{Guid.NewGuid():N}{ScenarioFile.Extension}");
        try
        {
            ScenarioDefinition original = ScenarioFixtures.FullyPopulated();
            ScenarioFile.Save(original, path);

            ScenarioDefinition reloaded = ScenarioFile.Load(path);

            Assert.Equal(ScenarioFile.ToJson(original), ScenarioFile.ToJson(reloaded));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SavingTheSameScenarioTwiceProducesByteIdenticalFiles()
    {
        string first = Path.Combine(Path.GetTempPath(), $"sil-a-{Guid.NewGuid():N}.json");
        string second = Path.Combine(Path.GetTempPath(), $"sil-b-{Guid.NewGuid():N}.json");
        try
        {
            ScenarioDefinition scenario = ScenarioFixtures.FullyPopulated();
            ScenarioFile.Save(scenario, first);
            ScenarioFile.Save(scenario, second);

            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public void FilesAreWrittenAsLfUtf8WithoutABom()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sil-{Guid.NewGuid():N}.json");
        try
        {
            ScenarioFile.Save(ScenarioFixtures.Decay(), path);
            byte[] bytes = File.ReadAllBytes(path);

            Assert.False(bytes.Take(Encoding.UTF8.GetPreamble().Length)
                .SequenceEqual(Encoding.UTF8.GetPreamble()));
            Assert.DoesNotContain((byte)'\r', bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheJsonUsesCamelCasedNamesAndOmitsNulls()
    {
        string json = ScenarioFile.ToJson(ScenarioFixtures.Decay());

        Assert.Contains("\"formatVersion\"", json, StringComparison.Ordinal);
        Assert.Contains("\"rateHz\"", json, StringComparison.Ordinal);
        Assert.Contains("\"timeConstant\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"links\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AFutureFormatVersionIsRefusedRatherThanGuessedAt()
    {
        string json = ScenarioFile.ToJson(ScenarioFixtures.Decay())
            .Replace("\"formatVersion\": 1", "\"formatVersion\": 99", StringComparison.Ordinal);

        ScenarioFormatException ex = Assert.Throws<ScenarioFormatException>(() => ScenarioFile.FromJson(json));

        Assert.Contains("99", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedAndMissingDocumentsAreRejected()
    {
        Assert.Throws<ScenarioFormatException>(() => ScenarioFile.FromJson("{ not json"));
        Assert.Throws<ScenarioFormatException>(() => ScenarioFile.FromJson("null"));
        Assert.Throws<ScenarioFormatException>(() =>
            ScenarioFile.Load(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.json")));
    }

    [Fact]
    public void AHandWrittenMinimalDocumentLoads()
    {
        const string json = """
        {
          "formatVersion": 1,
          "name": "hand-written",
          "rateHz": 50,
          "models": [ { "name": "p", "kind": "FirstOrderLag" } ],
          "channels": [ { "name": "X" } ],
          "mappings": [ { "model": "p", "port": "x", "channel": "X" } ]
        }
        """;

        ScenarioDefinition scenario = ScenarioFile.FromJson(json);

        Assert.Equal("hand-written", scenario.Name);
        Assert.Equal(50, scenario.RateHz);
        Assert.Equal("Virtual", scenario.TimingMode);
        Assert.Single(scenario.Models!);
        Assert.Null(scenario.Models![0].Parameters);
    }
}

public class ScenarioBuilderTests
{
    [Theory]
    [InlineData("Euler", GoldenVectors.EulerDecay10Steps)]
    [InlineData("Rk4", GoldenVectors.Rk4Decay10Steps)]
    public void AScenarioLoadedFromJsonReproducesTheGoldenVectors(string integrator, double expected)
    {
        string json = ScenarioFile.ToJson(ScenarioFixtures.Decay(integrator));

        using RunnableScenario scenario = ScenarioBuilder.Build(ScenarioFile.FromJson(json));
        ScenarioResult result = scenario.RunToCompletion();

        Assert.Equal(expected, scenario.System.Channels.Get("X"), GoldenVectors.Tolerance);
        Assert.Equal(ScenarioVerdict.Pass, result.Verdict);
        Assert.Equal(10, result.SamplesEvaluated);
    }

    [Fact]
    public void ABuiltScenarioMatchesTheEquivalentHandWiredSystem()
    {
        using RunnableScenario scenario = ScenarioBuilder.Build(ScenarioFixtures.FullyPopulated());
        scenario.RunToCompletion();
        double fromScenario = scenario.System.Channels.Get("Pos");

        var builder = new SilSystemBuilder();
        builder.AddModel(new Models.Builtin.MassSpringDamperModel(
            "plant", 1.5, 0.4, 6.0, 0.1, -0.2, IntegratorKind.Rk4));
        builder.AddModel(new Models.Builtin.FirstOrderLagModel(
            "lag", 0.25, 1.0, 0.0, IntegratorKind.Euler));
        builder.AddChannel("Force", "N", 0.0, "drive");
        builder.AddChannel("Pos", "m", 0.0, "displacement");
        builder.AddChannel("Filtered", "m");
        builder.Map("plant", "F", "Force");
        builder.Map("plant", "x", "Pos", Channels.LinearScale.Create(2.0, 1.0));
        builder.Map("lag", "x", "Filtered");
        builder.Link("plant", "x", "lag", "u");

        using SilSystem system = builder.Build();
        var stimulus = new Stimulus.StimulusTask(system.Channels,
            [new Stimulus.StimulusBinding("Force", new Stimulus.SineProfile(2.0, 0.8))]);
        var monitor = new LimitMonitor(system.Channels, [new ChannelLimit("Pos", -5.0, 5.0)]);
        SimEngine engine = system.CreateEngine(
            SimRate.FromHz(500),
            new EngineOptions { TimingMode = TimingMode.WallClockSynced },
            stimulus: [stimulus],
            recorders: [monitor]);
        engine.RunUntil(4.0);

        Assert.Equal(
            BitConverter.DoubleToInt64Bits(system.Channels.Get("Pos")),
            BitConverter.DoubleToInt64Bits(fromScenario));
    }

    [Fact]
    public void RunningTheSameScenarioFileTwiceProducesByteIdenticalLogs()
    {
        string scenarioPath = Path.Combine(Path.GetTempPath(), $"sil-{Guid.NewGuid():N}.json");
        string logA = Path.Combine(Path.GetTempPath(), $"sil-a-{Guid.NewGuid():N}.csv");
        string logB = Path.Combine(Path.GetTempPath(), $"sil-b-{Guid.NewGuid():N}.csv");

        try
        {
            ScenarioFile.Save(ScenarioFixtures.FullyPopulated(), scenarioPath);

            RunLogged(scenarioPath, logA);
            RunLogged(scenarioPath, logB);

            byte[] a = File.ReadAllBytes(logA);
            Assert.Equal(a, File.ReadAllBytes(logB));
            Assert.True(a.Length > 0);
        }
        finally
        {
            File.Delete(scenarioPath);
            File.Delete(logA);
            File.Delete(logB);
        }

        static void RunLogged(string scenarioPath, string logPath)
        {
            using RunnableScenario scenario = ScenarioBuilder.Load(scenarioPath);
            RunSettings run = scenario.Definition.Run!;
            using CsvChannelLogger logger = CsvChannelLogger.ToFile(
                scenario.System.Channels, logPath, run.LogChannels, run.LogDecimation);

            scenario.RunToCompletion(extraRecorders: [logger]);
            logger.Flush();
        }
    }

    [Fact]
    public void TheRunSettingsDriveTheLoggedColumnsAndDecimation()
    {
        string logPath = Path.Combine(Path.GetTempPath(), $"sil-{Guid.NewGuid():N}.csv");
        try
        {
            using RunnableScenario scenario = ScenarioBuilder.Build(ScenarioFixtures.FullyPopulated());
            RunSettings run = scenario.Definition.Run!;
            using CsvChannelLogger logger = CsvChannelLogger.ToFile(
                scenario.System.Channels, logPath, run.LogChannels, run.LogDecimation);

            scenario.RunToCompletion(extraRecorders: [logger]);
            logger.Flush();
            logger.Dispose();

            string[] lines = File.ReadAllLines(logPath);
            Assert.Equal("t,Pos[m],Filtered[m]", lines[0]);

            // 4 s at 500 Hz is 2000 cycles; decimation 2 logs 1000 rows.
            Assert.Equal(1000, logger.RowsWritten);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public void ALoadedScenarioCanBeSteppedThroughAHost()
    {
        using RunnableScenario scenario = ScenarioBuilder.Build(ScenarioFixtures.Decay(rateHz: 100));
        IReadOnlyList<ChannelTrace> traces = ScenarioBuilder.CreateTraces(scenario, 512);
        var recorder = new TraceRecorder(scenario.System.Channels, traces);

        using var host = new SimulationHost(scenario.CreateEngine([recorder]));
        host.StepOnce();
        host.StepOnce();

        Assert.Equal(2, host.StepIndex);
        Assert.Equal(2, traces[0].Count);
        Assert.Equal("X", traces[0].ChannelName);
    }

    [Fact]
    public void LimitsDeclaredInTheDocumentProduceTheVerdict()
    {
        var definition = ScenarioFixtures.Decay() with
        {
            Limits = [new LimitDeclaration("X", 0.5, 1.0)],
        };

        using RunnableScenario scenario = ScenarioBuilder.Build(definition);
        ScenarioResult result = scenario.RunToCompletion();

        // The decay falls through 0.5 well before t = 1 s.
        Assert.Equal(ScenarioVerdict.Fail, result.Verdict);
        Assert.Equal(LimitViolationKind.Low, result.Violations[0].Kind);
    }

    [Fact]
    public void AScenarioWithoutStimulusBuildsWithNoStimulusTask()
    {
        using RunnableScenario scenario = ScenarioBuilder.Build(ScenarioFixtures.Decay());

        Assert.Null(scenario.Stimulus);
        Assert.Empty(scenario.Monitor.Limits);
    }

    [Fact]
    public void AScenarioWithoutAnEndTimeCannotBeRunAsABatch()
    {
        var definition = ScenarioFixtures.Decay() with { Run = null };
        using RunnableScenario scenario = ScenarioBuilder.Build(definition);

        ScenarioFormatException ex = Assert.Throws<ScenarioFormatException>(() => scenario.RunToCompletion());

        Assert.Contains("endTime", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryStimulusKindCanBeDeclared()
    {
        string csvPath = Path.Combine(Path.GetTempPath(), $"sil-{Guid.NewGuid():N}.csv");
        try
        {
            var csv = new StringBuilder("t,v\n");
            for (int i = 0; i < 5; i++)
            {
                csv.Append((i * 0.5).ToString("R", CultureInfo.InvariantCulture))
                   .Append(',')
                   .Append((i * 3.0).ToString("R", CultureInfo.InvariantCulture))
                   .Append('\n');
            }

            File.WriteAllText(csvPath, csv.ToString());

            var definition = new ScenarioDefinition(
                Name: "all-stimulus",
                RateHz: 100,
                Channels:
                [
                    new ChannelDeclaration("C1"), new ChannelDeclaration("C2"),
                    new ChannelDeclaration("C3"), new ChannelDeclaration("C4"),
                    new ChannelDeclaration("C5"),
                ],
                Stimulus:
                [
                    new StimulusDeclaration("C1", "Constant", new() { ["value"] = 2.5 }),
                    new StimulusDeclaration("C2", "Step", new()
                    {
                        ["startTime"] = GoldenVectors.StepStimulusT0,
                        ["before"] = GoldenVectors.StepStimulusBefore,
                        ["after"] = GoldenVectors.StepStimulusAfter,
                    }),
                    new StimulusDeclaration("C3", "Ramp", new()
                    {
                        ["duration"] = GoldenVectors.RampStimulusDuration,
                        ["to"] = GoldenVectors.RampStimulusTo,
                    }),
                    new StimulusDeclaration("C4", "Sine", new()
                    {
                        ["amplitude"] = 1.0,
                        ["frequencyHz"] = 0.25,
                    }),
                    new StimulusDeclaration("C5", "Csv", CsvPath: csvPath, CsvColumn: "v"),
                ],
                Run: new RunSettings(EndTime: 1.01));

            using RunnableScenario scenario = ScenarioBuilder.Build(definition);
            scenario.RunToCompletion();

            // Sampled on the t = 1.00 cycle, the last one before 1.01 s.
            Assert.Equal(2.5, scenario.System.Channels.Get("C1"));
            Assert.Equal(GoldenVectors.StepStimulusAfter, scenario.System.Channels.Get("C2"));
            Assert.Equal(GoldenVectors.RampStimulusAtT1, scenario.System.Channels.Get("C3"), 1e-9);

            // 0.25 Hz at t = 1 s is a quarter period, so the sine sits at its peak.
            Assert.Equal(1.0, scenario.System.Channels.Get("C4"), 1e-9);
            Assert.Equal(6.0, scenario.System.Channels.Get("C5"), 1e-9);
        }
        finally
        {
            File.Delete(csvPath);
        }
    }

    [Fact]
    public void CsvStimulusPathsResolveRelativeToTheScenarioFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sil-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "drive.csv"), "t,v\n0,1\n1,3\n");

            var definition = new ScenarioDefinition(
                Name: "relative-csv",
                RateHz: 100,
                Channels: [new ChannelDeclaration("C")],
                Stimulus: [new StimulusDeclaration("C", "Csv", CsvPath: "drive.csv")],
                Run: new RunSettings(EndTime: 0.51));

            string scenarioPath = Path.Combine(directory, "s.json");
            ScenarioFile.Save(definition, scenarioPath);

            using RunnableScenario scenario = ScenarioBuilder.Load(scenarioPath);
            scenario.RunToCompletion();

            Assert.Equal(2.0, scenario.System.Channels.Get("C"), 1e-9);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public class ScenarioValidationTests
{
    private static ScenarioDefinition WithModel(ModelDefinition model) => new(
        Name: "v", RateHz: 100, Models: [model], Channels: [new ChannelDeclaration("X")]);

    [Fact]
    public void AnUnknownModelKindIsRejectedAndListsWhatIsSupported()
    {
        ScenarioFormatException ex = Assert.Throws<ScenarioFormatException>(() =>
            ScenarioBuilder.Build(WithModel(new ModelDefinition("m", "Quantum"))));

        Assert.Contains("Quantum", ex.Message, StringComparison.Ordinal);
        Assert.Contains("FirstOrderLag", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMisspelledParameterIsAnErrorNotASilentDefault()
    {
        ScenarioFormatException ex = Assert.Throws<ScenarioFormatException>(() =>
            ScenarioBuilder.Build(WithModel(new ModelDefinition("m", "FirstOrderLag", "Rk4", new()
            {
                ["timeconstant"] = 2.0,   // wrong case
            }))));

        Assert.Contains("unknown parameter", ex.Message, StringComparison.Ordinal);
        Assert.Contains("timeconstant", ex.Message, StringComparison.Ordinal);
        Assert.Contains("timeConstant", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonFiniteParameterIsRejected()
    {
        Assert.Throws<ScenarioFormatException>(() =>
            ScenarioBuilder.Build(WithModel(new ModelDefinition("m", "FirstOrderLag", "Rk4", new()
            {
                ["timeConstant"] = double.NaN,
            }))));
    }

    [Fact]
    public void AnOutOfRangeParameterIsReportedAgainstItsModel()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScenarioBuilder.Build(WithModel(new ModelDefinition("m", "FirstOrderLag", "Rk4", new()
            {
                ["timeConstant"] = -1.0,
            }))));
    }

    [Fact]
    public void AnUnknownIntegratorIsRejected()
    {
        ScenarioFormatException ex = Assert.Throws<ScenarioFormatException>(() =>
            ScenarioBuilder.Build(WithModel(new ModelDefinition("m", "FirstOrderLag", "Verlet"))));

        Assert.Contains("Verlet", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void AnOutOfRangeRateIsRejected(int rateHz)
    {
        var definition = new ScenarioDefinition(Name: "v", RateHz: rateHz);

        ScenarioFormatException ex = Assert.Throws<ScenarioFormatException>(() =>
            ScenarioBuilder.Build(definition));

        Assert.Contains("rateHz", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownTimingModeIsRejected()
    {
        var definition = new ScenarioDefinition(Name: "v", RateHz: 100, TimingMode: "Realtime");

        ScenarioFormatException ex = Assert.Throws<ScenarioFormatException>(() =>
            ScenarioBuilder.Build(definition));

        Assert.Contains("Realtime", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BadWiringIsReportedAsAScenarioError()
    {
        var definition = new ScenarioDefinition(
            Name: "v",
            RateHz: 100,
            Models: [new ModelDefinition("m", "FirstOrderLag")],
            Channels: [new ChannelDeclaration("X")],
            Mappings: [new MappingDeclaration("ghost", "x", "X")]);

        ScenarioFormatException ex = Assert.Throws<ScenarioFormatException>(() =>
            ScenarioBuilder.Build(definition));

        Assert.Contains("ghost", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonInvertibleMappingScaleIsRejected()
    {
        var definition = new ScenarioDefinition(
            Name: "v",
            RateHz: 100,
            Models: [new ModelDefinition("m", "FirstOrderLag")],
            Channels: [new ChannelDeclaration("X")],
            Mappings: [new MappingDeclaration("m", "x", "X", ScaleA: 0.0)]);

        Assert.Throws<ScenarioFormatException>(() => ScenarioBuilder.Build(definition));
    }

    [Fact]
    public void AnUnknownStimulusKindIsRejected()
    {
        var definition = new ScenarioDefinition(
            Name: "v",
            RateHz: 100,
            Channels: [new ChannelDeclaration("C")],
            Stimulus: [new StimulusDeclaration("C", "Chirp")]);

        ScenarioFormatException ex = Assert.Throws<ScenarioFormatException>(() =>
            ScenarioBuilder.Build(definition));

        Assert.Contains("Chirp", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingRequiredStimulusParameterIsRejected()
    {
        var definition = new ScenarioDefinition(
            Name: "v",
            RateHz: 100,
            Channels: [new ChannelDeclaration("C")],
            Stimulus: [new StimulusDeclaration("C", "Sine", new() { ["amplitude"] = 1.0 })]);

        ScenarioFormatException ex = Assert.Throws<ScenarioFormatException>(() =>
            ScenarioBuilder.Build(definition));

        Assert.Contains("frequencyHz", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StimulusOnAnUnknownChannelIsRejected()
    {
        var definition = new ScenarioDefinition(
            Name: "v",
            RateHz: 100,
            Channels: [new ChannelDeclaration("C")],
            Stimulus: [new StimulusDeclaration("Nope", "Constant", new() { ["value"] = 1.0 })]);

        Assert.Throws<ScenarioFormatException>(() => ScenarioBuilder.Build(definition));
    }

    [Fact]
    public void AMissingCsvFileAndAMissingColumnAreBothRejected()
    {
        var missingFile = new ScenarioDefinition(
            Name: "v",
            RateHz: 100,
            Channels: [new ChannelDeclaration("C")],
            Stimulus: [new StimulusDeclaration("C", "Csv", CsvPath: "does-not-exist.csv")]);

        Assert.Throws<ScenarioFormatException>(() => ScenarioBuilder.Build(missingFile));

        string csvPath = Path.Combine(Path.GetTempPath(), $"sil-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(csvPath, "t,a\n0,1\n1,2\n");
            var missingColumn = new ScenarioDefinition(
                Name: "v",
                RateHz: 100,
                Channels: [new ChannelDeclaration("C")],
                Stimulus: [new StimulusDeclaration("C", "Csv", CsvPath: csvPath, CsvColumn: "b")]);

            ScenarioFormatException ex = Assert.Throws<ScenarioFormatException>(() =>
                ScenarioBuilder.Build(missingColumn));

            Assert.Contains("Available: a", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(csvPath);
        }
    }

    [Fact]
    public void UnknownPlaybackOptionsAreRejected()
    {
        string csvPath = Path.Combine(Path.GetTempPath(), $"sil-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(csvPath, "t,a\n0,1\n1,2\n");

            ScenarioDefinition WithOption(string? interpolation, string? endBehaviour) => new(
                Name: "v",
                RateHz: 100,
                Channels: [new ChannelDeclaration("C")],
                Stimulus:
                [
                    new StimulusDeclaration("C", "Csv",
                        CsvPath: csvPath, Interpolation: interpolation, EndBehaviour: endBehaviour),
                ]);

            Assert.Throws<ScenarioFormatException>(() => ScenarioBuilder.Build(WithOption("Cubic", null)));
            Assert.Throws<ScenarioFormatException>(() => ScenarioBuilder.Build(WithOption(null, "Reverse")));
        }
        finally
        {
            File.Delete(csvPath);
        }
    }

    [Fact]
    public void AnInvertedLimitBandIsRejected()
    {
        var definition = new ScenarioDefinition(
            Name: "v",
            RateHz: 100,
            Channels: [new ChannelDeclaration("C")],
            Limits: [new LimitDeclaration("C", 5.0, 1.0)]);

        Assert.Throws<ScenarioFormatException>(() => ScenarioBuilder.Build(definition));
    }

    [Fact]
    public void ALimitOnAnUnknownChannelIsRejected()
    {
        var definition = new ScenarioDefinition(
            Name: "v",
            RateHz: 100,
            Channels: [new ChannelDeclaration("C")],
            Limits: [new LimitDeclaration("Missing", 0.0, 1.0)]);

        Assert.Throws<ScenarioFormatException>(() => ScenarioBuilder.Build(definition));
    }

    [Fact]
    public void ANativeModelWithoutALibraryPathIsRejected()
    {
        ScenarioFormatException ex = Assert.Throws<ScenarioFormatException>(() =>
            ScenarioBuilder.Build(WithModel(new ModelDefinition("m", "Native"))));

        Assert.Contains("libraryPath", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANativeModelWithAMissingLibraryIsRejected()
    {
        ScenarioFormatException ex = Assert.Throws<ScenarioFormatException>(() =>
            ScenarioBuilder.Build(WithModel(
                new ModelDefinition("m", "Native", LibraryPath: "no-such-library.dylib"))));

        Assert.Contains("no-such-library", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnnamedModelIsRejected()
    {
        Assert.Throws<ScenarioFormatException>(() =>
            ScenarioBuilder.Build(WithModel(new ModelDefinition("  ", "FirstOrderLag"))));
    }

    [Fact]
    public void UseAfterDisposeIsRejected()
    {
        RunnableScenario scenario = ScenarioBuilder.Build(ScenarioFixtures.Decay());
        scenario.Dispose();

        Assert.Throws<ObjectDisposedException>(() => scenario.CreateEngine());
        scenario.Dispose();
    }
}

[Collection(NativeModelCollection.Name)]
public class NativeScenarioTests(NativeModelFixture fixture)
{
    [Fact]
    public void ANativeModelCanBeDeclaredInAScenarioAndReachesTheGoldenVector()
    {
        var definition = new ScenarioDefinition(
            Name: "native-decay",
            RateHz: 10,
            Models: [new ModelDefinition("plant", "Native", LibraryPath: fixture.FirstOrderLibrary)],
            Channels: [new ChannelDeclaration("X")],
            Mappings: [new MappingDeclaration("plant", "x", "X")],
            Run: new RunSettings(EndTime: 1.0));

        using RunnableScenario scenario = ScenarioBuilder.Build(definition);
        scenario.RunToCompletion();

        Assert.Equal(GoldenVectors.Rk4Decay10Steps, scenario.System.Channels.Get("X"), GoldenVectors.Tolerance);
        Assert.IsType<Native.NativeModel>(scenario.System.Models[0]);
    }

    [Fact]
    public void ANativeLibraryPathResolvesRelativeToTheScenarioFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sil-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string libraryName = Path.GetFileName(fixture.FirstOrderLibrary);
            File.Copy(fixture.FirstOrderLibrary, Path.Combine(directory, libraryName));

            var definition = new ScenarioDefinition(
                Name: "relative-native",
                RateHz: 10,
                Models: [new ModelDefinition("plant", "Native", LibraryPath: libraryName)],
                Channels: [new ChannelDeclaration("X")],
                Mappings: [new MappingDeclaration("plant", "x", "X")],
                Run: new RunSettings(EndTime: 1.0));

            string scenarioPath = Path.Combine(directory, "s.json");
            ScenarioFile.Save(definition, scenarioPath);

            using RunnableScenario scenario = ScenarioBuilder.Load(scenarioPath);
            scenario.RunToCompletion();

            Assert.Equal(
                GoldenVectors.Rk4Decay10Steps,
                scenario.System.Channels.Get("X"),
                GoldenVectors.Tolerance);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
