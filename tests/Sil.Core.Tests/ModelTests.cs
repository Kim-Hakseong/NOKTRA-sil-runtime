using Sil.Core.Engine;
using Sil.Core.Models;
using Sil.Core.Models.Builtin;
using Sil.Core.Numerics;
using Sil.Core.Timing;
using Xunit;

namespace Sil.Core.Tests;

public class FirstOrderLagModelTests
{
    /// <summary>
    /// With tau = 1, u = 0 and x0 = 1 the plant reduces to dx/dt = -x, so the model layer must
    /// reproduce the frozen integrator golden vectors exactly.
    /// </summary>
    [Theory]
    [InlineData(IntegratorKind.Euler, GoldenVectors.EulerDecay10Steps)]
    [InlineData(IntegratorKind.Rk4, GoldenVectors.Rk4Decay10Steps)]
    public void UnforcedDecay_MatchesGoldenVector(IntegratorKind kind, double expected)
    {
        using var model = new FirstOrderLagModel(
            timeConstant: 1.0, gain: 1.0, initialValue: 1.0, integrator: kind);
        model.Initialize();

        for (int i = 0; i < 10; i++)
        {
            model.Step(0.1);
        }

        Assert.Equal(expected, model.GetPort(FirstOrderLagModel.PortX), GoldenVectors.Tolerance);
    }

    [Fact]
    public void PortTable_IsDeclaredAsOneInputAndOneOutput()
    {
        using var model = new FirstOrderLagModel("plant");

        Assert.Equal(2, model.Ports.Count);
        Assert.Equal(new PortDescriptor(0, "u", PortDirection.Input, string.Empty), model.Ports[0]);
        Assert.Equal(new PortDescriptor(1, "x", PortDirection.Output, string.Empty), model.Ports[1]);
        Assert.Equal(0, model.RequirePort("u"));
        Assert.Equal(1, model.RequirePort("x"));
        Assert.Equal(-1, model.IndexOfPort("nope"));
    }

    [Fact]
    public void StepResponse_ApproachesGainTimesInput()
    {
        using var model = new FirstOrderLagModel(timeConstant: 0.5, gain: 3.0);
        model.Initialize();
        model.SetPort(FirstOrderLagModel.PortU, 2.0);

        // 20 tau is far past settling.
        for (int i = 0; i < 10_000; i++)
        {
            model.Step(0.001);
        }

        Assert.Equal(6.0, model.GetPort(FirstOrderLagModel.PortX), 1e-6);
    }

    [Fact]
    public void StepResponse_ReachesOneMinusOneOverEAtOneTimeConstant()
    {
        using var model = new FirstOrderLagModel(timeConstant: 2.0, gain: 1.0);
        model.Initialize();
        model.SetPort("u", 1.0);

        for (int i = 0; i < 2000; i++)
        {
            model.Step(0.001);
        }

        Assert.Equal(1.0 - Math.Exp(-1.0), model.GetPort("x"), 1e-9);
    }

    [Fact]
    public void Initialize_RestoresTheInitialCondition()
    {
        using var model = new FirstOrderLagModel(initialValue: 1.0);
        model.Initialize();
        model.Step(0.1);
        Assert.NotEqual(1.0, model.GetPort("x"));

        model.Initialize();

        Assert.Equal(1.0, model.GetPort("x"));
        Assert.Equal(0.0, model.Time);
    }

    [Fact]
    public void Initialize_PublishesOutputsBeforeAnyStep()
    {
        using var model = new FirstOrderLagModel(initialValue: 7.5);

        model.Initialize();

        Assert.Equal(7.5, model.GetPort(FirstOrderLagModel.PortX));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void Constructor_RejectsNonPositiveTimeConstant(double tau)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FirstOrderLagModel(timeConstant: tau));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(double.PositiveInfinity)]
    public void Step_RejectsNonPositiveOrNonFiniteDt(double dt)
    {
        using var model = new FirstOrderLagModel();
        model.Initialize();

        Assert.Throws<ArgumentOutOfRangeException>(() => model.Step(dt));
    }

    [Fact]
    public void GetPort_RejectsOutOfRangeIndex()
    {
        using var model = new FirstOrderLagModel();
        model.Initialize();

        Assert.Throws<ArgumentOutOfRangeException>(() => model.GetPort(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.SetPort(-1, 0.0));
    }

    [Fact]
    public void SetPortByName_RejectsUnknownName()
    {
        using var model = new FirstOrderLagModel();

        Assert.Throws<ArgumentException>(() => model.SetPort("missing", 1.0));
    }

    [Fact]
    public void Time_TracksStepCountTimesDt()
    {
        using var model = new FirstOrderLagModel();
        model.Initialize();

        for (int i = 0; i < 1000; i++)
        {
            model.Step(0.001);
        }

        Assert.Equal(1.0, model.Time, 1e-12);
    }
}

public class MassSpringDamperModelTests
{
    [Fact]
    public void PortTable_DeclaresForceInputAndTwoOutputs()
    {
        using var model = new MassSpringDamperModel();

        Assert.Equal(3, model.Ports.Count);
        Assert.Equal(PortDirection.Input, model.Ports[MassSpringDamperModel.PortForce].Direction);
        Assert.Equal("N", model.Ports[MassSpringDamperModel.PortForce].Unit);
        Assert.Equal(PortDirection.Output, model.Ports[MassSpringDamperModel.PortPosition].Direction);
        Assert.Equal("m/s", model.Ports[MassSpringDamperModel.PortVelocity].Unit);
    }

    [Fact]
    public void UndampedRelease_FollowsCosineAtNaturalFrequency()
    {
        // m = 1, k = 4 => wn = 2 rad/s. Released from x = 1 with no damping: x(t) = cos(wn t).
        using var model = new MassSpringDamperModel(
            mass: 1.0, damping: 0.0, stiffness: 4.0, initialPosition: 1.0);
        model.Initialize();

        Assert.Equal(2.0, model.NaturalFrequency, 1e-12);
        Assert.Equal(0.0, model.DampingRatio, 1e-12);

        const double dt = 0.0005;
        const int steps = 2000;
        for (int i = 0; i < steps; i++)
        {
            model.Step(dt);
        }

        double t = steps * dt;
        Assert.Equal(Math.Cos(2.0 * t), model.GetPort("x"), 1e-9);
        Assert.Equal(-2.0 * Math.Sin(2.0 * t), model.GetPort("v"), 1e-9);
    }

    [Fact]
    public void DampedStepResponse_SettlesAtForceOverStiffness()
    {
        using var model = new MassSpringDamperModel(mass: 2.0, damping: 5.0, stiffness: 10.0);
        model.Initialize();
        model.SetPort(MassSpringDamperModel.PortForce, 20.0);

        for (int i = 0; i < 100_000; i++)
        {
            model.Step(0.0005);
        }

        Assert.Equal(2.0, model.GetPort("x"), 1e-6);
        Assert.Equal(0.0, model.GetPort("v"), 1e-6);
    }

    [Fact]
    public void DampingRatio_IsComputedFromParameters()
    {
        using var critical = new MassSpringDamperModel(mass: 1.0, damping: 2.0, stiffness: 1.0);
        using var free = new MassSpringDamperModel(mass: 1.0, damping: 0.5, stiffness: 0.0);

        Assert.Equal(1.0, critical.DampingRatio, 1e-12);
        Assert.Equal(0.0, free.DampingRatio);
    }

    [Theory]
    [InlineData(0.0, 1.0, 1.0)]
    [InlineData(1.0, -1.0, 1.0)]
    [InlineData(1.0, 1.0, -1.0)]
    public void Constructor_RejectsInvalidParameters(double mass, double damping, double stiffness)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MassSpringDamperModel(mass: mass, damping: damping, stiffness: stiffness));
    }
}

public class ModelTaskTests
{
    [Fact]
    public void EngineDrivenModel_MatchesStandaloneStepping()
    {
        using var driven = new FirstOrderLagModel(
            timeConstant: 1.0, initialValue: 1.0, integrator: IntegratorKind.Euler);
        var engine = new SimEngine(new SimClock(SimRate.FromHz(10)), [new ModelTask(driven)]);
        engine.RunSteps(10);

        using var standalone = new FirstOrderLagModel(
            timeConstant: 1.0, initialValue: 1.0, integrator: IntegratorKind.Euler);
        standalone.Initialize();
        for (int i = 0; i < 10; i++)
        {
            standalone.Step(0.1);
        }

        Assert.Equal(
            BitConverter.DoubleToInt64Bits(standalone.GetPort("x")),
            BitConverter.DoubleToInt64Bits(driven.GetPort("x")));
        Assert.Equal(GoldenVectors.EulerDecay10Steps, driven.GetPort("x"), GoldenVectors.Tolerance);
    }

    [Fact]
    public void EngineReset_ReinitializesTheModel()
    {
        using var model = new FirstOrderLagModel(initialValue: 3.0);
        var engine = new SimEngine(new SimClock(SimRate.FromHz(10)), [new ModelTask(model)]);
        engine.RunSteps(5);

        engine.Reset();

        Assert.Equal(3.0, model.GetPort("x"));
    }

    [Fact]
    public void TaskNameFollowsTheModelName()
    {
        using var model = new FirstOrderLagModel("plant-a");
        var task = new ModelTask(model);

        Assert.Equal("plant-a", task.Name);
    }
}

public class ContinuousModelValidationTests
{
    private sealed class BadPortsModel : ContinuousModel
    {
        public BadPortsModel(IReadOnlyList<PortDescriptor> ports)
            : base("bad", ports, stateSize: 0)
        {
        }

        protected override void OnInitialize()
        {
        }

        protected override void Derivatives(double t, ReadOnlySpan<double> state, Span<double> derivatives)
        {
        }

        protected override void UpdateOutputs(double t)
        {
        }
    }

    [Fact]
    public void PortIndicesMustMatchTheirPosition()
    {
        PortDescriptor[] ports = [new(5, "a", PortDirection.Input, string.Empty)];

        Assert.Throws<ArgumentException>(() => new BadPortsModel(ports));
    }

    [Fact]
    public void PortNamesMustBeUnique()
    {
        PortDescriptor[] ports =
        [
            new(0, "a", PortDirection.Input, string.Empty),
            new(1, "a", PortDirection.Output, string.Empty),
        ];

        Assert.Throws<ArgumentException>(() => new BadPortsModel(ports));
    }

    [Fact]
    public void PortNamesMustNotBeBlank()
    {
        PortDescriptor[] ports = [new(0, "  ", PortDirection.Input, string.Empty)];

        Assert.Throws<ArgumentException>(() => new BadPortsModel(ports));
    }
}
