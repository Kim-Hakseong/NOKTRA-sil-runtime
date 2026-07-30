using Sil.Core.Engine;
using Sil.Core.Models;
using Sil.Core.Models.Builtin;
using Sil.Core.Native;
using Sil.Core.Numerics;
using Sil.Core.Systems;
using Sil.Core.Timing;
using Xunit;

namespace Sil.Core.Tests;

[Collection(NativeModelCollection.Name)]
public class NativeModelTests(NativeModelFixture fixture)
{
    [Fact]
    public void ReferenceModel_ReproducesTheRk4GoldenVector()
    {
        // The C model is dx/dt = -x from x0 = 1 when u is left at 0 — the frozen decay vector,
        // now integrated on the other side of the ABI boundary.
        using NativeModel model = NativeModelLoader.Load(fixture.FirstOrderLibrary, "plant");
        model.Initialize();

        for (int i = 0; i < 10; i++)
        {
            model.Step(0.1);
        }

        Assert.Equal(GoldenVectors.Rk4Decay10Steps, model.GetPort("x"), GoldenVectors.Tolerance);
    }

    [Fact]
    public void ReferenceModel_AgreesWithTheManagedIntegratorBitForBit()
    {
        using NativeModel native = NativeModelLoader.Load(fixture.FirstOrderLibrary, "native");
        using var managed = new FirstOrderLagModel(
            "managed", timeConstant: 1.0, gain: 1.0, initialValue: 1.0, integrator: IntegratorKind.Rk4);

        native.Initialize();
        managed.Initialize();

        for (int i = 0; i < 200; i++)
        {
            double command = Math.Sin(i * 0.05);
            native.SetPort("u", command);
            managed.SetPort("u", command);
            native.Step(0.01);
            managed.Step(0.01);
        }

        Assert.Equal(
            BitConverter.DoubleToInt64Bits(managed.GetPort("x")),
            BitConverter.DoubleToInt64Bits(native.GetPort("x")));
    }

    [Fact]
    public void PortTable_IsReadThroughTheAbi()
    {
        using NativeModel model = NativeModelLoader.Load(fixture.FirstOrderLibrary, "plant");

        Assert.Equal(2, model.Ports.Count);
        Assert.Equal(new PortDescriptor(0, "u", PortDirection.Input, string.Empty), model.Ports[0]);
        Assert.Equal(new PortDescriptor(1, "x", PortDirection.Output, string.Empty), model.Ports[1]);
    }

    [Fact]
    public void ControllerPortTable_CarriesFourPortsInDeclaredOrder()
    {
        using NativeModel model = NativeModelLoader.Load(fixture.PiControllerLibrary, "pi");

        Assert.Equal(4, model.Ports.Count);
        Assert.Equal("setpoint", model.Ports[0].Name);
        Assert.Equal("measurement", model.Ports[1].Name);
        Assert.Equal("u", model.Ports[2].Name);
        Assert.Equal("integral", model.Ports[3].Name);
        Assert.Equal(PortDirection.Input, model.Ports[1].Direction);
        Assert.Equal(PortDirection.Output, model.Ports[2].Direction);
    }

    [Fact]
    public void Controller_ProducesProportionalActionOnTheFirstStep()
    {
        using NativeModel pi = NativeModelLoader.Load(fixture.PiControllerLibrary, "pi");
        pi.Initialize();
        pi.SetPort("setpoint", 1.0);
        pi.SetPort("measurement", 0.0);

        pi.Step(0.01);

        // Kp = 2, Ki = 5 (compile-time constants): u = 2*1 + 5*1*0.01 = 2.05.
        Assert.Equal(2.05, pi.GetPort("u"), 1e-12);
        Assert.Equal(0.05, pi.GetPort("integral"), 1e-12);
    }

    [Fact]
    public void Controller_ClampsItsOutputAndStopsWindingUp()
    {
        using NativeModel pi = NativeModelLoader.Load(fixture.PiControllerLibrary, "pi");
        pi.Initialize();
        pi.SetPort("setpoint", 1000.0);
        pi.SetPort("measurement", 0.0);

        for (int i = 0; i < 1000; i++)
        {
            pi.Step(0.01);
        }

        Assert.Equal(50.0, pi.GetPort("u"), 1e-12);

        // Anti-windup: the integrator must not have run away while saturated.
        Assert.True(
            pi.GetPort("integral") <= 50.0,
            $"Integrator wound up to {pi.GetPort("integral")} while the output was saturated.");
    }

    [Fact]
    public void Initialize_ReturnsTheInstanceToItsZeroTimeState()
    {
        using NativeModel model = NativeModelLoader.Load(fixture.FirstOrderLibrary, "plant");
        model.Initialize();
        model.SetPort("u", 5.0);
        model.Step(0.1);
        Assert.NotEqual(1.0, model.GetPort("x"));

        model.Initialize();

        Assert.Equal(1.0, model.GetPort("x"));
        Assert.Equal(0.0, model.GetPort("u"));
    }

    [Fact]
    public void SeparateInstances_DoNotShareState()
    {
        using NativeModel first = NativeModelLoader.Load(fixture.FirstOrderLibrary, "a");
        using NativeModel second = NativeModelLoader.LoadAnotherInstance(first, "b");
        first.Initialize();
        second.Initialize();

        first.SetPort("u", 10.0);
        for (int i = 0; i < 100; i++)
        {
            first.Step(0.01);
            second.Step(0.01);
        }

        Assert.True(first.GetPort("x") > 1.0);
        Assert.True(second.GetPort("x") < 1.0);
        Assert.Equal("b", second.Name);
    }

    [Fact]
    public void RepeatedRuns_ProduceIdenticalResults()
    {
        static double Run(string path)
        {
            using NativeModel model = NativeModelLoader.Load(path, "plant");
            model.Initialize();
            for (int i = 0; i < 500; i++)
            {
                model.SetPort("u", Math.Sin(i * 0.02));
                model.Step(0.002);
            }

            return model.GetPort("x");
        }

        Assert.Equal(
            BitConverter.DoubleToInt64Bits(Run(fixture.FirstOrderLibrary)),
            BitConverter.DoubleToInt64Bits(Run(fixture.FirstOrderLibrary)));
    }

    [Fact]
    public void NativeModelRunsInsideASystemAlongsideManagedModels()
    {
        var builder = new SilSystemBuilder();
        builder.AddModel(NativeModelLoader.Load(fixture.FirstOrderLibrary, "native-plant"));
        builder.AddModel(new FirstOrderLagModel("managed-plant", timeConstant: 1.0, initialValue: 1.0));
        builder.AddChannel("Command");
        builder.AddChannel("NativeX");
        builder.AddChannel("ManagedX");
        builder.Map("native-plant", "u", "Command");
        builder.Map("native-plant", "x", "NativeX");
        builder.Map("managed-plant", "x", "ManagedX");

        using SilSystem system = builder.Build();
        SimEngine engine = system.CreateEngine(SimRate.FromHz(10));

        engine.RunSteps(10);

        Assert.Equal(GoldenVectors.Rk4Decay10Steps, system.Channels.Get("NativeX"), GoldenVectors.Tolerance);
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(system.Channels.Get("ManagedX")),
            BitConverter.DoubleToInt64Bits(system.Channels.Get("NativeX")));
    }

    [Fact]
    public void EngineResetReinitializesTheNativeInstance()
    {
        var builder = new SilSystemBuilder();
        builder.AddModel(NativeModelLoader.Load(fixture.FirstOrderLibrary, "plant"));
        builder.AddChannel("X");
        builder.Map("plant", "x", "X");

        using SilSystem system = builder.Build();
        SimEngine engine = system.CreateEngine(SimRate.FromHz(10));
        engine.RunSteps(10);
        Assert.NotEqual(1.0, system.Channels.Get("X"));

        engine.Reset();

        Assert.Equal(1.0, system.Channels.Get("X"));
    }

    [Fact]
    public void Validate_AcceptsAConformingLibrary()
    {
        Assert.Null(NativeModelLoader.Validate(fixture.FirstOrderLibrary));
        Assert.Null(NativeModelLoader.Validate(fixture.PiControllerLibrary));
    }
}

[Collection(NativeModelCollection.Name)]
public class NativeModelFailureTests(NativeModelFixture fixture)
{
    [Fact]
    public void MissingFile_IsRejectedWithThePath()
    {
        string path = fixture.ScratchPath("not-here.dylib");

        SilNativeException ex = Assert.Throws<SilNativeException>(() => NativeModelLoader.Load(path, "m"));

        Assert.Contains("not-here", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsNotALibrary_IsRejected()
    {
        string path = fixture.ScratchPath("garbage.dylib");
        File.WriteAllText(path, "this is not a shared library");

        Assert.Throws<SilNativeException>(() => NativeModelLoader.Load(path, "m"));
        Assert.NotNull(NativeModelLoader.Validate(path));
    }

    [Fact]
    public void ALibraryWithoutTheRequiredExports_IsRejected()
    {
        // The host test binary is a real loadable image that exports none of the ABI.
        string hostLibrary = Path.Combine(AppContext.BaseDirectory, "Sil.Core.Tests.dll");
        Assert.True(File.Exists(hostLibrary));

        string? reason = NativeModelLoader.Validate(hostLibrary);

        Assert.NotNull(reason);
    }

    [Fact]
    public void OutOfRangePortAccess_IsRejectedBeforeReachingNativeCode()
    {
        using NativeModel model = NativeModelLoader.Load(fixture.FirstOrderLibrary, "plant");

        Assert.Throws<ArgumentOutOfRangeException>(() => model.GetPort(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.SetPort(-1, 0.0));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.01)]
    [InlineData(double.NaN)]
    public void NonPositiveStepSizes_AreRejected(double dt)
    {
        using NativeModel model = NativeModelLoader.Load(fixture.FirstOrderLibrary, "plant");

        Assert.Throws<ArgumentOutOfRangeException>(() => model.Step(dt));
    }

    [Fact]
    public void UseAfterDispose_IsRejected()
    {
        NativeModel model = NativeModelLoader.Load(fixture.FirstOrderLibrary, "plant");
        model.Dispose();

        Assert.Throws<ObjectDisposedException>(() => model.Step(0.1));
        Assert.Throws<ObjectDisposedException>(() => model.GetPort(0));
        Assert.Throws<ObjectDisposedException>(() => model.SetPort(0, 1.0));
        Assert.Throws<ObjectDisposedException>(model.Initialize);

        model.Dispose();   // second dispose is a no-op
    }

    [Fact]
    public void EmptyArguments_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => NativeModelLoader.Load(" ", "m"));
        Assert.Throws<ArgumentException>(() => NativeModelLoader.Load(fixture.FirstOrderLibrary, " "));
    }
}

public class NativeAbiTests
{
    [Fact]
    public void LibraryFileName_FollowsThePlatformConvention()
    {
        string name = NativeAbi.LibraryFileName("sil_first_order");

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("sil_first_order.dll", name);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Equal("libsil_first_order.dylib", name);
        }
        else
        {
            Assert.Equal("libsil_first_order.so", name);
        }
    }

    [Fact]
    public void RequiredExports_MatchTheFrozenAbi()
    {
        Assert.Equal(
            [
                "sil_abi_version", "sil_init", "sil_step", "sil_port_count",
                "sil_port_info", "sil_get", "sil_set", "sil_free",
            ],
            NativeAbi.RequiredExports);
        Assert.Equal(1, NativeAbi.Version);
    }

    [Fact]
    public void StatusCodesHaveReadableNames()
    {
        Assert.Equal("SIL_OK", NativeAbi.DescribeStatus(NativeAbi.Ok));
        Assert.Equal("SIL_ERR_RANGE", NativeAbi.DescribeStatus(NativeAbi.ErrRange));
        Assert.Contains("unknown", NativeAbi.DescribeStatus(99), StringComparison.Ordinal);
    }
}
