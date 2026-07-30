using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sil.App.Simulation;
using Sil.Core.Channels;
using Sil.Core.Engine;
using Sil.Core.Logging;
using Sil.Core.Models;
using Sil.Core.Runtime;
using Sil.Core.Timing;

namespace Sil.App.ViewModels;

/// <summary>A node of the project tree.</summary>
public sealed partial class ProjectNodeViewModel : ObservableObject
{
    public ProjectNodeViewModel(string title, string detail = "", IEnumerable<ProjectNodeViewModel>? children = null)
    {
        Title = title;
        Detail = detail;
        Children = new ObservableCollection<ProjectNodeViewModel>(children ?? []);
    }

    public string Title { get; }

    public string Detail { get; }

    public ObservableCollection<ProjectNodeViewModel> Children { get; }

    public bool HasDetail => Detail.Length > 0;
}

/// <summary>One live channel row: current value plus a bar gauge against its limit band.</summary>
public sealed partial class ChannelRowViewModel : ObservableObject
{
    private readonly ChannelLimit? _limit;

    [ObservableProperty]
    private double _value;

    [ObservableProperty]
    private bool _inViolation;

    public ChannelRowViewModel(ChannelDefinition definition, ChannelLimit? limit)
    {
        Index = definition.Index;
        Name = definition.Name;
        Unit = definition.Unit;
        _limit = limit;

        // A gauge needs a span even when the channel has no declared band.
        GaugeMinimum = limit?.Low ?? -1.0;
        GaugeMaximum = limit?.High ?? 1.0;
        LimitText = limit is null
            ? "no limit"
            : string.Create(CultureInfo.InvariantCulture, $"[{limit.Low:0.###}, {limit.High:0.###}]");
    }

    public int Index { get; }

    public string Name { get; }

    public string Unit { get; }

    public string LimitText { get; }

    public double GaugeMinimum { get; }

    public double GaugeMaximum { get; }

    public string Header => Unit.Length == 0 ? Name : $"{Name} [{Unit}]";

    public string ValueText => Value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Value position within the gauge span, clamped to 0..1.</summary>
    public double GaugeFraction
    {
        get
        {
            double span = GaugeMaximum - GaugeMinimum;
            if (span <= 0.0)
            {
                return 0.0;
            }

            return Math.Clamp((Value - GaugeMinimum) / span, 0.0, 1.0);
        }
    }

    public void Update(double value)
    {
        Value = value;
        InViolation = _limit?.Check(value) is not null;
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(GaugeFraction));
    }
}

/// <summary>Shell view model: owns the scenario, the host and everything the window shows.</summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private static readonly int[] AvailableRates = [10, 50, 100, 200, 500, 1000];

    private SimulationScenario _scenario;
    private SimulationHost _host;
    private bool _disposed;

    [ObservableProperty]
    private int _rateHz = 200;

    [ObservableProperty]
    private bool _syncToWallClock = true;

    [ObservableProperty]
    private string _stateText = "Idle";

    [ObservableProperty]
    private string _simulationTimeText = "0.000 s";

    [ObservableProperty]
    private string _stepCountText = "0";

    [ObservableProperty]
    private string _verdictText = "PASS";

    [ObservableProperty]
    private bool _isFailing;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _overrunText = "0";

    [ObservableProperty]
    private string _samplesText = "0";

    [ObservableProperty]
    private string _violationCountText = "0";

    [ObservableProperty]
    private string _rateText = "200 Hz";

    [ObservableProperty]
    private ChannelRowViewModel? _primaryChannel;

    public MainWindowViewModel()
    {
        _scenario = DemoSystemFactory.CreateDefault();
        _host = CreateHost(_scenario, _rateHz, _syncToWallClock);

        Channels = [];
        ProjectTree = [];
        Violations = [];

        RebuildPresentation();
        Refresh();
    }

    /// <summary>Rates offered in the toolbar, within the engine's 1..1000 Hz range.</summary>
    public IReadOnlyList<int> Rates => AvailableRates;

    public ObservableCollection<ChannelRowViewModel> Channels { get; }

    public ObservableCollection<ProjectNodeViewModel> ProjectTree { get; }

    public ObservableCollection<string> Violations { get; }

    /// <summary>Title of the loaded scenario.</summary>
    public string ScenarioTitle => _scenario.Title;

    /// <summary>Description of the loaded scenario.</summary>
    public string ScenarioDescription => _scenario.Description;

    /// <summary>Traces backing the live chart.</summary>
    public IReadOnlyList<ChannelTrace> Traces => _scenario.Recorder.Traces;

    /// <summary>Timing mode caption for the toolbar chip.</summary>
    public string TimingModeText => SyncToWallClock ? "WALL CLOCK" : "VIRTUAL TIME";

    /// <summary>Step size caption derived from the selected rate.</summary>
    public string StepSizeText
        => string.Create(CultureInfo.InvariantCulture, $"dt = {1.0 / RateHz * 1000.0:0.###} ms");

    /// <summary>True when the run has recorded at least one limit excursion.</summary>
    public bool HasViolations => Violations.Count > 0;

    /// <summary>Starts a free run on the background host.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    public void Start()
    {
        if (_host.IsRunning)
        {
            return;
        }

        _ = _host.StartAsync();
        Refresh();
    }

    /// <summary>Stops the run at the next cycle boundary.</summary>
    [RelayCommand(CanExecute = nameof(CanPause))]
    public async Task PauseAsync()
    {
        await _host.PauseAsync().ConfigureAwait(true);
        _scenario.Monitor.Finish();
        Refresh();
    }

    /// <summary>Executes exactly one cycle.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    public void StepOnce()
    {
        if (_host.IsRunning)
        {
            return;
        }

        _host.StepOnce();
        Refresh();
    }

    /// <summary>Returns the simulation to t=0.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    public void Reset()
    {
        if (_host.IsRunning)
        {
            return;
        }

        _host.Reset();
        Refresh();
    }

    /// <summary>
    /// Pulls the current simulation state into the bound properties. Called by the view on a
    /// display timer rather than per cycle: at 1 kHz the UI must not try to follow every step.
    /// </summary>
    public void Refresh()
    {
        IsRunning = _host.IsRunning;
        StateText = _host.IsRunning ? "Running" : _host.Engine.State.ToString();
        SimulationTimeText = string.Create(CultureInfo.InvariantCulture, $"{_host.Time:0.000} s");
        StepCountText = _host.StepIndex.ToString(CultureInfo.InvariantCulture);

        ChannelTable channels = _scenario.System.Channels;
        foreach (ChannelRowViewModel row in Channels)
        {
            row.Update(channels.Get(row.Index));
        }

        ScenarioResult result = _scenario.Monitor.Result;
        IsFailing = !result.Passed;
        VerdictText = result.Passed ? "PASS" : "FAIL";

        OverrunText = _host.Engine.OverrunCount.ToString(CultureInfo.InvariantCulture);
        SamplesText = result.SamplesEvaluated.ToString(CultureInfo.InvariantCulture);
        ViolationCountText = result.TotalViolationCount.ToString(CultureInfo.InvariantCulture);
        RateText = string.Create(CultureInfo.InvariantCulture, $"{RateHz} Hz");

        SyncViolations(result);
        OnPropertyChanged(nameof(HasViolations));

        StartCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StepOnceCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.Dispose();
        _scenario.Dispose();
    }

    partial void OnRateHzChanged(int value)
    {
        OnPropertyChanged(nameof(StepSizeText));
        Rebuild();
    }

    partial void OnSyncToWallClockChanged(bool value)
    {
        OnPropertyChanged(nameof(TimingModeText));
        Rebuild();
    }

    private bool CanStart() => !IsRunning;

    private bool CanPause() => IsRunning;

    /// <summary>
    /// Rate and timing mode are properties of the engine, not of a run, so changing either
    /// rebuilds the host from t=0 rather than mutating a live loop.
    /// </summary>
    private void Rebuild()
    {
        if (_disposed || _host.IsRunning)
        {
            return;
        }

        _host.Dispose();
        _host = CreateHost(_scenario, RateHz, SyncToWallClock);
        Refresh();
    }

    private void RebuildPresentation()
    {
        Channels.Clear();
        ProjectTree.Clear();

        ChannelTable channels = _scenario.System.Channels;
        IReadOnlyList<ChannelLimit> limits = _scenario.Monitor.Limits;

        foreach (ChannelDefinition definition in channels.Definitions)
        {
            ChannelLimit? limit = limits.FirstOrDefault(
                l => string.Equals(l.ChannelName, definition.Name, StringComparison.Ordinal));
            Channels.Add(new ChannelRowViewModel(definition, limit));
        }

        // The dial gets the first channel that actually has a band to read against; a dial with
        // no limits has no meaningful sweep.
        PrimaryChannel = Channels.FirstOrDefault(c => c.LimitText != "no limit") ?? Channels.FirstOrDefault();

        ProjectTree.Add(new ProjectNodeViewModel(
            "Models",
            $"{_scenario.System.Models.Count} model(s)",
            _scenario.System.Models.Select(DescribeModel)));

        ProjectTree.Add(new ProjectNodeViewModel(
            "Channels",
            $"{channels.Count} channel(s)",
            channels.Definitions.Select(d => new ProjectNodeViewModel(
                d.Name, d.Unit.Length == 0 ? d.Description : $"{d.Unit} — {d.Description}"))));

        ProjectTree.Add(new ProjectNodeViewModel(
            "Stimulus",
            $"{_scenario.StimulusSummary.Count} binding(s)",
            _scenario.StimulusSummary.Select(s => new ProjectNodeViewModel(s))));

        ProjectTree.Add(new ProjectNodeViewModel(
            "Limits",
            $"{limits.Count} band(s)",
            limits.Select(l => new ProjectNodeViewModel(
                l.ChannelName,
                string.Create(CultureInfo.InvariantCulture, $"[{l.Low:0.###}, {l.High:0.###}]")))));

        OnPropertyChanged(nameof(ScenarioTitle));
        OnPropertyChanged(nameof(ScenarioDescription));
        OnPropertyChanged(nameof(Traces));
    }

    private void SyncViolations(ScenarioResult result)
    {
        if (Violations.Count == result.Violations.Count)
        {
            return;
        }

        Violations.Clear();
        foreach (LimitViolation violation in result.Violations)
        {
            Violations.Add(violation.ToString());
        }
    }

    private static ProjectNodeViewModel DescribeModel(IModel model) => new(
        model.Name,
        $"{model.Ports.Count} port(s)",
        model.Ports.Select(p => new ProjectNodeViewModel(p.Name, $"{p.Direction}{(p.Unit.Length == 0 ? "" : " · " + p.Unit)}")));

    private static SimulationHost CreateHost(SimulationScenario scenario, int rateHz, bool syncToWallClock)
    {
        var options = new EngineOptions
        {
            TimingMode = syncToWallClock ? TimingMode.WallClockSynced : TimingMode.Virtual,
        };

        SimEngine engine = scenario.System.CreateEngine(
            SimRate.FromHz(rateHz),
            options,
            stimulus: [scenario.Stimulus],
            recorders: [scenario.Monitor, scenario.Recorder]);

        return new SimulationHost(engine);
    }
}
