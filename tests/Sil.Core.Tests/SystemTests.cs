using Sil.Core.Channels;
using Sil.Core.Engine;
using Sil.Core.Models;
using Sil.Core.Models.Builtin;
using Sil.Core.Numerics;
using Sil.Core.Systems;
using Sil.Core.Timing;
using Xunit;

namespace Sil.Core.Tests;

public class LinearScaleTests
{
    [Fact]
    public void ToEngineering_MatchesGoldenVector()
    {
        LinearScale scale = LinearScale.Create(GoldenVectors.MappingScaleA, GoldenVectors.MappingScaleB);

        Assert.Equal(GoldenVectors.MappingExpected, scale.ToEngineering(GoldenVectors.MappingRawValue), 1e-12);
    }

    [Fact]
    public void ToRaw_IsTheExactInverse()
    {
        LinearScale scale = LinearScale.Create(GoldenVectors.MappingScaleA, GoldenVectors.MappingScaleB);

        Assert.Equal(GoldenVectors.MappingRawValue, scale.ToRaw(GoldenVectors.MappingExpected), 1e-12);
    }

    [Fact]
    public void Identity_PassesValuesThrough()
    {
        Assert.Equal(3.25, LinearScale.Identity.ToEngineering(3.25));
        Assert.Equal(3.25, LinearScale.Identity.ToRaw(3.25));
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    public void Create_RejectsNonInvertibleSlope(double a, double b)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LinearScale.Create(a, b));
    }

    [Fact]
    public void Create_RejectsNonFiniteOffset()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LinearScale.Create(1.0, double.NaN));
    }

    [Fact]
    public void IsValid_RejectsDefaultConstructedScale()
    {
        Assert.False(default(LinearScale).IsValid);
        Assert.True(LinearScale.Identity.IsValid);
    }
}

public class ChannelTableTests
{
    [Fact]
    public void Build_AssignsSequentialIndicesAndResolvesNames()
    {
        var builder = new ChannelTableBuilder();
        int speed = builder.Add("Speed", "m/s");
        int temp = builder.Add("Temp", "degC", initialValue: 20.0);
        ChannelTable table = builder.Build();

        Assert.Equal(0, speed);
        Assert.Equal(1, temp);
        Assert.Equal(2, table.Count);
        Assert.Equal(1, table.IndexOf("Temp"));
        Assert.Equal(-1, table.IndexOf("Missing"));
        Assert.Equal("degC", table.Definition(temp).Unit);
    }

    [Fact]
    public void Reset_RestoresDeclaredInitialValues()
    {
        var builder = new ChannelTableBuilder();
        builder.Add("A", initialValue: 7.0);
        ChannelTable table = builder.Build();

        Assert.Equal(7.0, table.Get("A"));
        table.Set("A", 1.0);
        table.Reset();

        Assert.Equal(7.0, table.Get("A"));
    }

    [Fact]
    public void CopyValuesTo_SnapshotsCurrentValues()
    {
        var builder = new ChannelTableBuilder();
        builder.Add("A");
        builder.Add("B");
        ChannelTable table = builder.Build();
        table.Set("A", 1.5);
        table.Set("B", -2.5);

        double[] snapshot = new double[2];
        table.CopyValuesTo(snapshot);

        Assert.Equal([1.5, -2.5], snapshot);
        Assert.Throws<ArgumentException>(() => table.CopyValuesTo(new double[1]));
    }

    [Fact]
    public void DuplicateNames_AreRejected()
    {
        var builder = new ChannelTableBuilder();
        builder.Add("A");
        builder.Add("A");

        Assert.Throws<ArgumentException>(() => builder.Build());
    }

    [Fact]
    public void BlankNamesAndNonFiniteInitialValues_AreRejected()
    {
        var blank = new ChannelTableBuilder();
        blank.Add(" ");
        Assert.Throws<ArgumentException>(() => blank.Build());

        var infinite = new ChannelTableBuilder();
        infinite.Add("A", initialValue: double.NaN);
        Assert.Throws<ArgumentException>(() => infinite.Build());
    }

    [Fact]
    public void OutOfRangeIndexAndUnknownName_AreRejected()
    {
        var builder = new ChannelTableBuilder();
        builder.Add("A");
        ChannelTable table = builder.Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => table.Get(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Set(-1, 0.0));
        Assert.Throws<ArgumentException>(() => table.Get("B"));
    }
}

public class SilSystemTests
{
    [Fact]
    public void OutputMapping_AppliesTheScaleFromModelToChannel()
    {
        // Plant holds x = 10 with no input, so the mapped channel must show 2*10 + 1 = 21.
        var builder = new SilSystemBuilder();
        builder.AddModel(new FirstOrderLagModel("plant", timeConstant: 1e6, initialValue: GoldenVectors.MappingRawValue));
        builder.AddChannel("PlantOut", "eu");
        builder.Map("plant", "x", "PlantOut",
            LinearScale.Create(GoldenVectors.MappingScaleA, GoldenVectors.MappingScaleB));

        using SilSystem system = builder.Build();
        SimEngine engine = system.CreateEngine(SimRate.FromHz(100));

        Assert.Equal(GoldenVectors.MappingExpected, system.Channels.Get("PlantOut"), 1e-9);

        engine.StepOnce();

        Assert.Equal(GoldenVectors.MappingExpected, system.Channels.Get("PlantOut"), 1e-6);
    }

    [Fact]
    public void InputMapping_AppliesTheInverseScaleFromChannelToModel()
    {
        var builder = new SilSystemBuilder();
        builder.AddModel(new FirstOrderLagModel("plant"));
        builder.AddChannel("Command", "eu");
        builder.Map("plant", "u", "Command",
            LinearScale.Create(GoldenVectors.MappingScaleA, GoldenVectors.MappingScaleB));

        using SilSystem system = builder.Build();
        SimEngine engine = system.CreateEngine(SimRate.FromHz(100));

        system.Channels.Set("Command", GoldenVectors.MappingExpected);
        engine.StepOnce();

        Assert.Equal(GoldenVectors.MappingRawValue, system.Models[0].GetPort("u"), 1e-12);
    }

    [Fact]
    public void MappedSystem_ReachesTheScaledSteadyState()
    {
        var builder = new SilSystemBuilder();
        builder.AddModel(new FirstOrderLagModel("plant", timeConstant: 0.1));
        builder.AddChannel("Command");
        builder.AddChannel("Feedback");
        builder.Map("plant", "u", "Command");
        builder.Map("plant", "x", "Feedback", LinearScale.Create(2.0, 1.0));

        using SilSystem system = builder.Build();
        SimEngine engine = system.CreateEngine(SimRate.FromHz(1000));
        system.Channels.Set("Command", 10.0);

        engine.RunUntil(5.0);

        // x settles at 10, so the channel settles at 2*10 + 1 = 21.
        Assert.Equal(21.0, system.Channels.Get("Feedback"), 1e-6);
    }

    [Fact]
    public void ModelLink_CarriesValuesBetweenModels()
    {
        var builder = new SilSystemBuilder();
        builder.AddModel(new FirstOrderLagModel("a", timeConstant: 0.1, initialValue: 1.0));
        builder.AddModel(new FirstOrderLagModel("b", timeConstant: 0.1));
        builder.Link("a", "x", "b", "u");

        using SilSystem system = builder.Build();
        SimEngine engine = system.CreateEngine(SimRate.FromHz(1000));

        // Link is applied at reset, so b already sees a's initial output.
        Assert.Equal(1.0, system.Models[1].GetPort("u"), 1e-12);

        engine.RunUntil(2.0);

        // a decays to 0, so b follows it down to 0.
        Assert.Equal(0.0, system.Models[0].GetPort("x"), 1e-6);
        Assert.Equal(0.0, system.Models[1].GetPort("x"), 1e-6);
    }

    [Fact]
    public void ModelLink_AppliesItsScaleForward()
    {
        var builder = new SilSystemBuilder();
        builder.AddModel(new FirstOrderLagModel("a", timeConstant: 1e6, initialValue: GoldenVectors.MappingRawValue));
        builder.AddModel(new FirstOrderLagModel("b"));
        builder.Link("a", "x", "b", "u",
            LinearScale.Create(GoldenVectors.MappingScaleA, GoldenVectors.MappingScaleB));

        using SilSystem system = builder.Build();
        system.CreateEngine(SimRate.FromHz(100));

        Assert.Equal(GoldenVectors.MappingExpected, system.Models[1].GetPort("u"), 1e-9);
    }

    [Fact]
    public void CycleOrder_IsStimulusLinksInputsModelsOutputs()
    {
        var builder = new SilSystemBuilder();
        builder.AddModel(new FirstOrderLagModel("a"));
        builder.AddModel(new FirstOrderLagModel("b"));
        builder.AddChannel("C");
        builder.Map("a", "u", "C");
        builder.Map("b", "x", "C2");
        builder.AddChannel("C2");

        using SilSystem system = builder.Build();
        var stim = new DelegateSimTask("stim", _ => { });
        var rec = new DelegateSimTask("rec", _ => { });

        string[] names = [.. system.BuildCycle([stim], [rec]).Select(t => t.Name)];

        Assert.Equal(
            ["system-init", "stim", "channels->inputs", "a", "b", "outputs->channels", "rec"],
            names);
    }

    [Fact]
    public void LinkTaskIsPresentOnlyWhenLinksExist()
    {
        var builder = new SilSystemBuilder();
        builder.AddModel(new FirstOrderLagModel("a"));
        builder.AddModel(new FirstOrderLagModel("b"));
        builder.Link("a", "x", "b", "u");

        using SilSystem system = builder.Build();

        Assert.Contains("model-links", system.BuildCycle().Select(t => t.Name));
    }

    [Fact]
    public void Reset_RestoresChannelsAndModelsTogether()
    {
        var builder = new SilSystemBuilder();
        builder.AddModel(new FirstOrderLagModel("plant", timeConstant: 0.1, initialValue: 5.0));
        builder.AddChannel("Command", initialValue: 2.0);
        builder.AddChannel("Feedback");
        builder.Map("plant", "u", "Command");
        builder.Map("plant", "x", "Feedback");

        using SilSystem system = builder.Build();
        SimEngine engine = system.CreateEngine(SimRate.FromHz(100));
        engine.RunSteps(50);
        system.Channels.Set("Command", 99.0);

        engine.Reset();

        Assert.Equal(2.0, system.Channels.Get("Command"));
        Assert.Equal(5.0, system.Channels.Get("Feedback"));
        Assert.Equal(5.0, system.Models[0].GetPort("x"));
    }

    [Fact]
    public void SameSystemRunTwice_ProducesIdenticalChannelValues()
    {
        static double Run()
        {
            var builder = new SilSystemBuilder();
            builder.AddModel(new MassSpringDamperModel("msd", mass: 2.0, damping: 0.7, stiffness: 5.0));
            builder.AddChannel("Force", "N");
            builder.AddChannel("Pos", "m");
            builder.Map("msd", "F", "Force");
            builder.Map("msd", "x", "Pos", LinearScale.Create(1000.0, 0.0));

            using SilSystem system = builder.Build();
            SimEngine engine = system.CreateEngine(SimRate.FromHz(200));
            system.Channels.Set("Force", 3.0);
            engine.RunSteps(1000);
            return system.Channels.Get("Pos");
        }

        Assert.Equal(BitConverter.DoubleToInt64Bits(Run()), BitConverter.DoubleToInt64Bits(Run()));
    }

    [Fact]
    public void FindModel_ResolvesByName()
    {
        var builder = new SilSystemBuilder();
        builder.AddModel(new FirstOrderLagModel("plant"));

        using SilSystem system = builder.Build();

        Assert.NotNull(system.FindModel("plant"));
        Assert.Null(system.FindModel("nope"));
    }

    [Fact]
    public void Dispose_DisposesEveryModel()
    {
        var model = new TrackingModel("m");
        var builder = new SilSystemBuilder();
        builder.AddModel(model);

        builder.Build().Dispose();

        Assert.True(model.Disposed);
    }

    private sealed class TrackingModel(string name) : ContinuousModel(
        name,
        [new PortDescriptor(0, "y", PortDirection.Output, string.Empty)],
        stateSize: 1,
        IntegratorKind.Euler)
    {
        public bool Disposed { get; private set; }

        protected override void OnInitialize()
        {
        }

        protected override void Derivatives(double t, ReadOnlySpan<double> state, Span<double> derivatives)
        {
            derivatives[0] = 0.0;
        }

        protected override void UpdateOutputs(double t) => Output(0, State[0]);

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}

public class SilSystemValidationTests
{
    private static SilSystemBuilder TwoPlants()
    {
        var builder = new SilSystemBuilder();
        builder.AddModel(new FirstOrderLagModel("a"));
        builder.AddModel(new FirstOrderLagModel("b"));
        builder.AddChannel("C");
        return builder;
    }

    [Fact]
    public void DuplicateModelNames_AreRejected()
    {
        var builder = new SilSystemBuilder();
        builder.AddModel(new FirstOrderLagModel("a"));

        Assert.Throws<SystemWiringException>(() => builder.AddModel(new FirstOrderLagModel("a")));
    }

    [Fact]
    public void UnknownModelInMapping_IsRejected()
    {
        SilSystemBuilder builder = TwoPlants();
        builder.Map("ghost", "u", "C");

        SystemWiringException ex = Assert.Throws<SystemWiringException>(() => builder.Build());
        Assert.Contains("ghost", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownPortInMapping_IsRejected()
    {
        SilSystemBuilder builder = TwoPlants();
        builder.Map("a", "zzz", "C");

        Assert.Throws<SystemWiringException>(() => builder.Build());
    }

    [Fact]
    public void UnknownChannelInMapping_IsRejected()
    {
        SilSystemBuilder builder = TwoPlants();
        builder.Map("a", "u", "NoSuchChannel");

        Assert.Throws<SystemWiringException>(() => builder.Build());
    }

    [Fact]
    public void TwoWritersOnOneInputPort_AreRejected()
    {
        SilSystemBuilder builder = TwoPlants();
        builder.AddChannel("D");
        builder.Map("a", "u", "C");
        builder.Map("a", "u", "D");

        SystemWiringException ex = Assert.Throws<SystemWiringException>(() => builder.Build());
        Assert.Contains("written by both", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MappingAndLinkFightingOverOneInput_AreRejected()
    {
        SilSystemBuilder builder = TwoPlants();
        builder.Map("b", "u", "C");
        builder.Link("a", "x", "b", "u");

        Assert.Throws<SystemWiringException>(() => builder.Build());
    }

    [Fact]
    public void TwoOutputsMappedToOneChannel_AreRejected()
    {
        SilSystemBuilder builder = TwoPlants();
        builder.Map("a", "x", "C");
        builder.Map("b", "x", "C");

        Assert.Throws<SystemWiringException>(() => builder.Build());
    }

    [Fact]
    public void LinkFromAnInputPort_IsRejected()
    {
        SilSystemBuilder builder = TwoPlants();
        builder.Link("a", "u", "b", "u");

        SystemWiringException ex = Assert.Throws<SystemWiringException>(() => builder.Build());
        Assert.Contains("input port", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LinkIntoAnOutputPort_IsRejected()
    {
        SilSystemBuilder builder = TwoPlants();
        builder.Link("a", "x", "b", "x");

        SystemWiringException ex = Assert.Throws<SystemWiringException>(() => builder.Build());
        Assert.Contains("output port", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonInvertibleScale_IsRejected()
    {
        SilSystemBuilder builder = TwoPlants();
        builder.Map(new PortMapping("a", "u", "C", new LinearScale(0.0, 1.0)));

        SystemWiringException ex = Assert.Throws<SystemWiringException>(() => builder.Build());
        Assert.Contains("non-invertible", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateChannelNames_SurfaceAsWiringErrors()
    {
        var builder = new SilSystemBuilder();
        builder.AddChannel("C");
        builder.AddChannel("C");

        Assert.Throws<SystemWiringException>(() => builder.Build());
    }

    [Fact]
    public void BuildCycle_AfterDispose_IsRejected()
    {
        SilSystem system = TwoPlants().Build();
        system.Dispose();

        Assert.Throws<ObjectDisposedException>(() => system.BuildCycle());
    }
}
