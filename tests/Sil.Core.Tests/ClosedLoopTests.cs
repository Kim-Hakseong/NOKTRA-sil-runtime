using Sil.Core.Logging;
using Sil.Core.Models;
using Sil.Core.Models.Builtin;
using Sil.Core.Native;
using Sil.Core.Scenarios;
using Xunit;

namespace Sil.Core.Tests;

public class PiControllerModelTests
{
    [Fact]
    public void PortTableDeclaresTwoInputsAndTwoOutputs()
    {
        using var pi = new PiControllerModel("ctrl", 2.0, 5.0);

        Assert.Equal(4, pi.Ports.Count);
        Assert.Equal("setpoint", pi.Ports[0].Name);
        Assert.Equal("measurement", pi.Ports[1].Name);
        Assert.Equal("u", pi.Ports[2].Name);
        Assert.Equal("integral", pi.Ports[3].Name);
        Assert.Equal(Models.PortDirection.Input, pi.Ports[1].Direction);
        Assert.Equal(Models.PortDirection.Output, pi.Ports[3].Direction);
    }

    [Fact]
    public void TheFirstStepIsProportionalPlusOneIntegralIncrement()
    {
        using var pi = new PiControllerModel("ctrl", proportionalGain: 2.0, integralGain: 5.0);
        pi.Initialize();
        pi.SetPort("setpoint", 1.0);

        pi.Step(0.01);

        Assert.Equal(2.05, pi.GetPort("u"), 1e-12);
        Assert.Equal(0.05, pi.GetPort("integral"), 1e-12);
    }

    [Fact]
    public void ProportionalOnlyNeverAccumulates()
    {
        using var pi = new PiControllerModel("ctrl", proportionalGain: 3.0, integralGain: 0.0);
        pi.Initialize();
        pi.SetPort("setpoint", 2.0);

        for (int i = 0; i < 100; i++)
        {
            pi.Step(0.01);
        }

        Assert.Equal(6.0, pi.GetPort("u"), 1e-12);
        Assert.Equal(0.0, pi.GetPort("integral"));
    }

    [Fact]
    public void OutputIsClampedAndTheIntegratorStopsWindingUp()
    {
        using var pi = new PiControllerModel(
            "ctrl", proportionalGain: 2.0, integralGain: 5.0,
            outputMinimum: -50.0, outputMaximum: 50.0);
        pi.Initialize();
        pi.SetPort("setpoint", 1000.0);

        for (int i = 0; i < 1000; i++)
        {
            pi.Step(0.01);
        }

        Assert.Equal(50.0, pi.GetPort("u"), 1e-12);
        Assert.True(pi.GetPort("integral") <= 50.0);
    }

    [Fact]
    public void TheNegativeClampBehavesSymmetrically()
    {
        using var pi = new PiControllerModel(
            "ctrl", 2.0, 5.0, outputMinimum: -10.0, outputMaximum: 10.0);
        pi.Initialize();
        pi.SetPort("setpoint", -1000.0);

        for (int i = 0; i < 500; i++)
        {
            pi.Step(0.01);
        }

        Assert.Equal(-10.0, pi.GetPort("u"), 1e-12);
        Assert.True(pi.GetPort("integral") >= -10.0);
    }

    [Fact]
    public void InitializeClearsTheIntegrator()
    {
        using var pi = new PiControllerModel("ctrl", 1.0, 10.0);
        pi.Initialize();
        pi.SetPort("setpoint", 1.0);
        pi.Step(0.1);
        Assert.NotEqual(0.0, pi.GetPort("integral"));

        pi.Initialize();

        Assert.Equal(0.0, pi.GetPort("integral"));
        Assert.Equal(0.0, pi.GetPort("u"));
    }

    [Theory]
    [InlineData(double.NaN, 0.0)]
    [InlineData(1.0, double.NaN)]
    public void NonFiniteGainsAreRejected(double kp, double ki)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PiControllerModel("c", kp, ki));
    }

    [Fact]
    public void InvertedOutputLimitsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PiControllerModel("c", 1.0, 1.0, outputMinimum: 5.0, outputMaximum: 1.0));
    }
}

/// <summary>
/// The DESIGN.md closed-loop verification: a first-order plant wrapped in a PI controller,
/// checked against the analytic steady state rather than against a recorded trace.
/// </summary>
public class ClosedLoopVerificationTests
{
    private const double Tau = 0.5;
    private const double PlantGain = 1.0;
    private const double Kp = 2.0;
    private const double Ki = 5.0;
    private const double Setpoint = 1.0;

    private static ScenarioResult Run(ScenarioDefinition definition, out double measurement, out double command)
    {
        using RunnableScenario scenario = ScenarioBuilder.Build(definition);
        ScenarioResult result = scenario.RunToCompletion();

        measurement = scenario.System.Channels.Get(BuiltInScenarios.MeasurementChannel);
        command = scenario.System.Channels.Get(BuiltInScenarios.CommandChannel);
        return result;
    }

    [Fact]
    public void WithIntegralActionTheSteadyStateErrorIsZero()
    {
        ScenarioDefinition definition = BuiltInScenarios.ClosedLoop(
            timeConstant: Tau, plantGain: PlantGain,
            proportionalGain: Kp, integralGain: Ki,
            setpoint: Setpoint, endTime: 10.0);

        Run(definition, out double measurement, out double command);

        double expected = BuiltInScenarios.AnalyticSteadyState(Setpoint, PlantGain, Kp, Ki);
        double expectedError = BuiltInScenarios.AnalyticSteadyStateError(Setpoint, PlantGain, Kp, Ki);

        Assert.Equal(Setpoint, expected);
        Assert.Equal(0.0, expectedError);

        // 10 s is 20 tau and far past the 4/3 s settling time of these poles.
        Assert.Equal(expected, measurement, 1e-9);
        Assert.Equal(Setpoint - measurement, expectedError, 1e-9);

        // At steady state the plant needs u = x/K to hold its output.
        Assert.Equal(Setpoint / PlantGain, command, 1e-9);
    }

    [Fact]
    public void WithoutIntegralActionTheSteadyStateErrorMatchesTheClosedForm()
    {
        ScenarioDefinition definition = BuiltInScenarios.ClosedLoop(
            timeConstant: Tau, plantGain: PlantGain,
            proportionalGain: Kp, integralGain: 0.0,
            setpoint: Setpoint, endTime: 10.0);

        Run(definition, out double measurement, out _);

        // Proportional only: x = Kp*K/(1 + Kp*K) * r = 2/3.
        double expected = BuiltInScenarios.AnalyticSteadyState(Setpoint, PlantGain, Kp, 0.0);
        Assert.Equal(2.0 / 3.0, expected, 1e-12);
        Assert.Equal(expected, measurement, 1e-9);

        double expectedError = BuiltInScenarios.AnalyticSteadyStateError(Setpoint, PlantGain, Kp, 0.0);
        Assert.Equal(1.0 / 3.0, expectedError, 1e-12);
        Assert.Equal(expectedError, Setpoint - measurement, 1e-9);
    }

    /// <summary>
    /// Dominant time constant of the closed loop <c>tau*s^2 + (1 + Kp*K)*s + Ki*K</c>: the
    /// reciprocal of the pole nearest the origin. Run lengths are derived from this rather than
    /// guessed, because a badly tuned but perfectly stable loop can take minutes to settle.
    /// </summary>
    private static double DominantTimeConstant(double tau, double plantGain, double kp, double ki)
    {
        double a = tau;
        double b = 1.0 + (kp * plantGain);
        double c = ki * plantGain;

        double discriminant = (b * b) - (4.0 * a * c);

        // Real poles: the slow one sets the pace. Complex pair: the real part does.
        double slowestPoleMagnitude = discriminant >= 0.0
            ? (b - Math.Sqrt(discriminant)) / (2.0 * a)
            : b / (2.0 * a);

        return 1.0 / slowestPoleMagnitude;
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(4.0, 0.5)]
    [InlineData(0.5, 20.0)]
    public void ZeroSteadyStateErrorHoldsForAnyStableGainPair(double kp, double ki)
    {
        // 25 dominant time constants leaves e^-25 of the initial error, far inside the tolerance.
        double endTime = 25.0 * DominantTimeConstant(Tau, PlantGain, kp, ki);

        ScenarioDefinition definition = BuiltInScenarios.ClosedLoop(
            timeConstant: Tau, plantGain: PlantGain,
            proportionalGain: kp, integralGain: ki,
            setpoint: Setpoint, endTime: endTime);

        Run(definition, out double measurement, out _);

        // Integral action drives the error to zero regardless of the gains, so the analytic
        // answer does not depend on tuning — only on having waited long enough.
        Assert.Equal(Setpoint, measurement, 1e-7);
    }

    [Fact]
    public void ADeliberatelySluggishButStableLoopStillReachesTheSetpoint()
    {
        // Kp = 4, Ki = 0.5 puts a pole at about -0.101, a ~9.9 s time constant. A fixed 40 s run
        // would report a 3e-3 error and look like a defect; it is simply not settled yet.
        const double kp = 4.0;
        const double ki = 0.5;
        double dominant = DominantTimeConstant(Tau, PlantGain, kp, ki);

        Assert.InRange(dominant, 9.0, 11.0);

        double Measure(double endTime)
        {
            ScenarioDefinition definition = BuiltInScenarios.ClosedLoop(
                timeConstant: Tau, plantGain: PlantGain,
                proportionalGain: kp, integralGain: ki,
                setpoint: Setpoint, endTime: endTime);
            Run(definition, out double measurement, out _);
            return measurement;
        }

        double early = Measure(4.0 * dominant);
        double late = Measure(25.0 * dominant);

        // Monotone approach from below, converging on the analytic answer.
        Assert.True(early < late, $"Expected the loop to keep converging: {early} then {late}.");
        Assert.Equal(Setpoint, late, 1e-7);
    }

    [Fact]
    public void ADifferentPlantGainStillSettlesOnTheSetpoint()
    {
        ScenarioDefinition definition = BuiltInScenarios.ClosedLoop(
            timeConstant: 0.2, plantGain: 4.0,
            proportionalGain: Kp, integralGain: Ki,
            setpoint: 3.0, endTime: 20.0);

        Run(definition, out double measurement, out double command);

        Assert.Equal(3.0, measurement, 1e-9);

        // Holding x = 3 with K = 4 needs u = 0.75.
        Assert.Equal(0.75, command, 1e-9);
    }

    [Fact]
    public void TheLoopTracksAStepInTheSetpoint()
    {
        ScenarioDefinition definition = BuiltInScenarios.ClosedLoop(
            timeConstant: Tau, plantGain: PlantGain,
            proportionalGain: Kp, integralGain: Ki,
            setpoint: Setpoint, stepTime: 1.0, endTime: 10.0);

        using RunnableScenario scenario = ScenarioBuilder.Build(definition);
        Engine.SimEngine engine = scenario.CreateEngine();

        engine.RunUntil(0.9);
        Assert.Equal(0.0, scenario.System.Channels.Get(BuiltInScenarios.MeasurementChannel), 1e-12);

        engine.RunUntil(10.0);
        Assert.Equal(Setpoint, scenario.System.Channels.Get(BuiltInScenarios.MeasurementChannel), 1e-9);
    }

    [Fact]
    public void AnAcceptanceBandAroundTheSettledValuePasses()
    {
        // Judge only after the transient: the band starts at the step, so it is applied to the
        // settled portion of the run.
        ScenarioDefinition definition = BuiltInScenarios.ClosedLoop(
            timeConstant: Tau, plantGain: PlantGain,
            proportionalGain: Kp, integralGain: Ki,
            setpoint: Setpoint, endTime: 10.0) with
        {
            Limits = [new LimitDeclaration(BuiltInScenarios.MeasurementChannel, -0.05, 1.25)],
        };

        ScenarioResult result = Run(definition, out _, out _);

        Assert.Equal(ScenarioVerdict.Pass, result.Verdict);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void ATooTightBandCatchesTheOvershoot()
    {
        ScenarioDefinition definition = BuiltInScenarios.ClosedLoop(
            timeConstant: Tau, plantGain: PlantGain,
            proportionalGain: Kp, integralGain: Ki,
            setpoint: Setpoint, endTime: 10.0) with
        {
            Limits = [new LimitDeclaration(BuiltInScenarios.MeasurementChannel, -0.05, 1.0)],
        };

        ScenarioResult result = Run(definition, out _, out _);

        Assert.Equal(ScenarioVerdict.Fail, result.Verdict);
        Assert.Equal(LimitViolationKind.High, result.Violations[0].Kind);
    }

    [Fact]
    public void TheClosedLoopIsRateIndependentAtSteadyState()
    {
        double MeasureAt(int rateHz)
        {
            ScenarioDefinition definition = BuiltInScenarios.ClosedLoop(
                timeConstant: Tau, plantGain: PlantGain,
                proportionalGain: Kp, integralGain: Ki,
                setpoint: Setpoint, rateHz: rateHz, endTime: 10.0);

            Run(definition, out double measurement, out _);
            return measurement;
        }

        // The one-cycle transport delay in the feedback link changes the transient, never the
        // steady state, so every rate must land on the same analytic answer.
        Assert.Equal(Setpoint, MeasureAt(100), 1e-9);
        Assert.Equal(Setpoint, MeasureAt(500), 1e-9);
        Assert.Equal(Setpoint, MeasureAt(1000), 1e-9);
    }

    [Fact]
    public void TheClosedLoopScenarioSurvivesAFileRoundTrip()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sil-{Guid.NewGuid():N}{ScenarioFile.Extension}");
        try
        {
            ScenarioDefinition definition = BuiltInScenarios.ClosedLoop(
                timeConstant: Tau, plantGain: PlantGain,
                proportionalGain: Kp, integralGain: Ki,
                setpoint: Setpoint, endTime: 10.0);
            ScenarioFile.Save(definition, path);

            using RunnableScenario scenario = ScenarioBuilder.Load(path);
            scenario.RunToCompletion();

            Assert.Equal(
                Setpoint,
                scenario.System.Channels.Get(BuiltInScenarios.MeasurementChannel),
                1e-9);
            Assert.Equal(2, scenario.System.Links.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RunningTheClosedLoopTwiceGivesBitIdenticalResults()
    {
        double Once()
        {
            ScenarioDefinition definition = BuiltInScenarios.ClosedLoop(
                timeConstant: Tau, plantGain: PlantGain,
                proportionalGain: Kp, integralGain: Ki,
                setpoint: Setpoint, endTime: 5.0);
            Run(definition, out double measurement, out _);
            return measurement;
        }

        Assert.Equal(BitConverter.DoubleToInt64Bits(Once()), BitConverter.DoubleToInt64Bits(Once()));
    }
}

/// <summary>
/// The same closed loop with the controller replaced by the compiled C model — the actual SIL
/// case: a plant model driven by real compiled control code.
/// </summary>
[Collection(NativeModelCollection.Name)]
public class NativeClosedLoopTests(NativeModelFixture fixture)
{
    // The C reference fixes these; see src/Sil.NativeSpec/src/sil_pi_controller.c.
    private const double NativeKp = 2.0;
    private const double NativeKi = 5.0;

    [Fact]
    public void TheCompiledControllerDrivesThePlantToZeroSteadyStateError()
    {
        ScenarioDefinition definition = BuiltInScenarios.ClosedLoop(
            controllerLibraryPath: fixture.PiControllerLibrary,
            timeConstant: 0.5, plantGain: 1.0, setpoint: 1.0, endTime: 10.0);

        using RunnableScenario scenario = ScenarioBuilder.Build(definition);
        ScenarioResult result = scenario.RunToCompletion();

        double expected = BuiltInScenarios.AnalyticSteadyState(1.0, 1.0, NativeKp, NativeKi);

        Assert.Equal(expected, scenario.System.Channels.Get(BuiltInScenarios.MeasurementChannel), 1e-9);
        Assert.Equal(1.0, scenario.System.Channels.Get(BuiltInScenarios.CommandChannel), 1e-9);
        Assert.Equal(ScenarioVerdict.Pass, result.Verdict);
        Assert.IsType<NativeModel>(scenario.System.FindModel(BuiltInScenarios.ControllerName));
    }

    /// <summary>
    /// The compiled controller and the managed one must agree bit-for-bit inside a closed loop.
    /// Both are written in the same operation order for exactly this reason, so any divergence
    /// here is a real difference in the C rather than a rounding artefact.
    /// </summary>
    [Fact]
    public void TheCompiledAndManagedControllersAgreeBitForBitInTheLoop()
    {
        double MeasureWith(string? libraryPath)
        {
            ScenarioDefinition definition = BuiltInScenarios.ClosedLoop(
                controllerLibraryPath: libraryPath,
                timeConstant: 0.5, plantGain: 1.0,
                proportionalGain: NativeKp, integralGain: NativeKi,
                setpoint: 1.0, endTime: 3.0);

            using RunnableScenario scenario = ScenarioBuilder.Build(definition);
            scenario.RunToCompletion();
            return scenario.System.Channels.Get(BuiltInScenarios.MeasurementChannel);
        }

        double native = MeasureWith(fixture.PiControllerLibrary);
        double managed = MeasureWith(null);

        Assert.Equal(BitConverter.DoubleToInt64Bits(managed), BitConverter.DoubleToInt64Bits(native));
    }

    [Fact]
    public void TheCompiledControllerSaturatesAgainstAnUnreachableSetpoint()
    {
        // The C controller clamps at +/-50 and the plant gain is 1, so x can only reach 50.
        ScenarioDefinition definition = BuiltInScenarios.ClosedLoop(
            controllerLibraryPath: fixture.PiControllerLibrary,
            timeConstant: 0.5, plantGain: 1.0, setpoint: 500.0, endTime: 60.0);

        using RunnableScenario scenario = ScenarioBuilder.Build(definition);
        scenario.RunToCompletion();

        Assert.Equal(50.0, scenario.System.Channels.Get(BuiltInScenarios.CommandChannel), 1e-9);
        Assert.Equal(50.0, scenario.System.Channels.Get(BuiltInScenarios.MeasurementChannel), 1e-6);
    }

    [Fact]
    public void TheNativeClosedLoopScenarioSurvivesAFileRoundTrip()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sil-{Guid.NewGuid():N}{ScenarioFile.Extension}");
        try
        {
            ScenarioFile.Save(
                BuiltInScenarios.ClosedLoop(
                    controllerLibraryPath: fixture.PiControllerLibrary, endTime: 10.0),
                path);

            using RunnableScenario scenario = ScenarioBuilder.Load(path);
            ScenarioResult result = scenario.RunToCompletion();

            Assert.Equal(1.0, scenario.System.Channels.Get(BuiltInScenarios.MeasurementChannel), 1e-9);
            Assert.Equal(ScenarioVerdict.Pass, result.Verdict);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
