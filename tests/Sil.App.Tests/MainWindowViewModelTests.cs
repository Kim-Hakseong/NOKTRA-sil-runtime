using Sil.App.Simulation;
using Sil.App.ViewModels;
using Sil.Core.Logging;
using Sil.Core.Runtime;
using Xunit;

namespace Sil.App.Tests;

public class DemoSystemFactoryTests
{
    [Fact]
    public void TheDefaultScenarioIsFullyWired()
    {
        using SimulationScenario scenario = DemoSystemFactory.CreateDefault();

        Assert.Equal(3, scenario.System.Channels.Count);
        Assert.Single(scenario.System.Models);
        Assert.Equal(1, scenario.Stimulus.BindingCount);
        Assert.Equal(2, scenario.Monitor.Limits.Count);
        Assert.Equal(3, scenario.Recorder.Traces.Count);
        Assert.NotEmpty(scenario.Title);
        Assert.NotEmpty(scenario.Description);
    }

    [Fact]
    public void EveryTraceIsSizedToTheDeclaredCapacity()
    {
        using SimulationScenario scenario = DemoSystemFactory.CreateDefault();

        Assert.All(scenario.Recorder.Traces,
            trace => Assert.Equal(DemoSystemFactory.TraceCapacity, trace.Capacity));
    }
}

public class MainWindowViewModelTests
{
    private static MainWindowViewModel CreateVirtualTimeViewModel()
        => new() { SyncToWallClock = false };

    [Fact]
    public void ConstructionBuildsTheChannelRowsAndProjectTree()
    {
        using MainWindowViewModel vm = CreateVirtualTimeViewModel();

        Assert.Equal(3, vm.Channels.Count);
        Assert.Equal(["Models", "Channels", "Stimulus", "Limits"], vm.ProjectTree.Select(n => n.Title));
        Assert.NotEmpty(vm.ProjectTree[0].Children);
        Assert.Equal("0.000 s", vm.SimulationTimeText);
        Assert.Equal("PASS", vm.VerdictText);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public void ChannelRowsCarryTheirUnitAndLimitBand()
    {
        using MainWindowViewModel vm = CreateVirtualTimeViewModel();

        ChannelRowViewModel position = vm.Channels.Single(c => c.Name == "Position");
        ChannelRowViewModel force = vm.Channels.Single(c => c.Name == "Force");

        Assert.Equal("Position [m]", position.Header);
        Assert.Equal("[-1, 1]", position.LimitText);
        Assert.Equal("no limit", force.LimitText);
    }

    [Fact]
    public void SteppingAdvancesTimeAndTheReadouts()
    {
        using MainWindowViewModel vm = CreateVirtualTimeViewModel();

        for (int i = 0; i < 200; i++)
        {
            vm.StepOnce();
        }

        Assert.Equal("200", vm.StepCountText);
        Assert.Equal("1.000 s", vm.SimulationTimeText);
        Assert.All(vm.Traces, trace => Assert.True(trace.Count > 0));
    }

    [Fact]
    public void ResetReturnsTheShellToZero()
    {
        using MainWindowViewModel vm = CreateVirtualTimeViewModel();
        for (int i = 0; i < 50; i++)
        {
            vm.StepOnce();
        }

        vm.Reset();

        Assert.Equal("0", vm.StepCountText);
        Assert.Equal("0.000 s", vm.SimulationTimeText);
        Assert.Equal("PASS", vm.VerdictText);
        Assert.All(vm.Traces, trace => Assert.Equal(0, trace.Count));
    }

    [Fact]
    public void ChangingTheRateRebuildsFromZeroWithTheNewStepSize()
    {
        using MainWindowViewModel vm = CreateVirtualTimeViewModel();
        for (int i = 0; i < 100; i++)
        {
            vm.StepOnce();
        }

        vm.RateHz = 100;

        Assert.Equal("0", vm.StepCountText);

        for (int i = 0; i < 100; i++)
        {
            vm.StepOnce();
        }

        // 100 cycles at 100 Hz is one second, where 100 cycles at the default 200 Hz was half.
        Assert.Equal("1.000 s", vm.SimulationTimeText);
    }

    [Fact]
    public void OnlyRatesInsideTheEngineRangeAreOffered()
    {
        using MainWindowViewModel vm = CreateVirtualTimeViewModel();

        Assert.All(vm.Rates, hz => Assert.InRange(hz, 1, 1000));
        Assert.Contains(1000, vm.Rates);
    }

    [Fact]
    public void TheResonantScenarioEventuallyFailsItsDisplacementBand()
    {
        using MainWindowViewModel vm = CreateVirtualTimeViewModel();

        // 30 s at 200 Hz: long enough for the near-resonant response to grow past +/-1 m.
        for (int i = 0; i < 6000; i++)
        {
            vm.StepOnce();
        }

        Assert.True(vm.IsFailing, $"Expected a limit failure but the verdict was '{vm.VerdictText}'.");
        Assert.StartsWith("FAIL", vm.VerdictText, StringComparison.Ordinal);
        Assert.NotEmpty(vm.Violations);
        Assert.Contains("Position", vm.Violations[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAndPauseDriveTheBackgroundHost()
    {
        // Wall-clock paced, as the shell actually runs: a free run on virtual time would burn
        // through minutes of simulation in the gap before the pause lands.
        using MainWindowViewModel vm = new();

        vm.Start();
        Assert.True(vm.IsRunning);

        await vm.PauseAsync();

        Assert.False(vm.IsRunning);
        Assert.Contains(vm.Channels, c => c.Value != 0.0);
    }

    [Fact]
    public async Task CommandAvailabilityFollowsTheRunState()
    {
        using MainWindowViewModel vm = new();

        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.False(vm.PauseCommand.CanExecute(null));

        vm.Start();

        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.False(vm.StepOnceCommand.CanExecute(null));
        Assert.True(vm.PauseCommand.CanExecute(null));

        await vm.PauseAsync();

        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.True(vm.ResetCommand.CanExecute(null));
    }

    [Fact]
    public async Task StartingTwiceIsIgnoredRatherThanThrowing()
    {
        using MainWindowViewModel vm = new();

        vm.Start();
        vm.Start();

        await vm.PauseAsync();

        Assert.False(vm.IsRunning);
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var vm = CreateVirtualTimeViewModel();

        vm.Dispose();
        vm.Dispose();
    }
}

public class ChannelRowViewModelTests
{
    private static ChannelRowViewModel Row(double low, double high)
        => new(new Sil.Core.Channels.ChannelDefinition(0, "C", "m"), new ChannelLimit("C", low, high));

    [Fact]
    public void TheGaugeFractionMapsTheBandOntoZeroToOne()
    {
        ChannelRowViewModel row = Row(-1.0, 1.0);

        row.Update(-1.0);
        Assert.Equal(0.0, row.GaugeFraction, 1e-12);

        row.Update(0.0);
        Assert.Equal(0.5, row.GaugeFraction, 1e-12);

        row.Update(1.0);
        Assert.Equal(1.0, row.GaugeFraction, 1e-12);
    }

    [Fact]
    public void ValuesOutsideTheBandClampTheGaugeAndRaiseTheViolationFlag()
    {
        ChannelRowViewModel row = Row(-1.0, 1.0);

        row.Update(9.0);

        Assert.Equal(1.0, row.GaugeFraction);
        Assert.True(row.InViolation);

        row.Update(0.0);

        Assert.False(row.InViolation);
    }

    [Fact]
    public void AChannelWithoutALimitStillGetsAUsableGaugeSpan()
    {
        var row = new ChannelRowViewModel(
            new Sil.Core.Channels.ChannelDefinition(0, "C", ""), limit: null);

        row.Update(0.0);

        Assert.Equal("no limit", row.LimitText);
        Assert.Equal("C", row.Header);
        Assert.Equal(0.5, row.GaugeFraction, 1e-12);
        Assert.False(row.InViolation);
    }

    [Fact]
    public void TheValueTextIsInvariantAndTrimmed()
    {
        ChannelRowViewModel row = Row(-10.0, 10.0);

        row.Update(1.5);

        Assert.Equal("1.5", row.ValueText);
    }
}

public class ProjectNodeViewModelTests
{
    [Fact]
    public void NodesExposeTheirChildrenAndDetailVisibility()
    {
        var leaf = new ProjectNodeViewModel("leaf");
        var parent = new ProjectNodeViewModel("parent", "2 items", [leaf]);

        Assert.False(leaf.HasDetail);
        Assert.True(parent.HasDetail);
        Assert.Single(parent.Children);
    }
}
