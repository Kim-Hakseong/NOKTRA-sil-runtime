namespace Sil.Core.Scenarios;

/// <summary>Raised when a scenario document is malformed or refers to something unknown.</summary>
public sealed class ScenarioFormatException : Exception
{
    public ScenarioFormatException(string message)
        : base(message)
    {
    }

    public ScenarioFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A declared model instance.
/// </summary>
/// <remarks>
/// Parameters are carried as a name/value map rather than as a field per model type. That keeps
/// the document free of polymorphic JSON and lets a new model kind ship without changing the
/// schema — the builder validates the names it needs and rejects the ones it does not recognise,
/// so a typo is an error rather than a silently ignored setting.
/// </remarks>
/// <param name="Name">Instance name, unique in the scenario.</param>
/// <param name="Kind">Model kind: <c>FirstOrderLag</c>, <c>MassSpringDamper</c> or <c>Native</c>.</param>
/// <param name="Integrator">Integration scheme name (<c>Euler</c> or <c>Rk4</c>), for ODE models.</param>
/// <param name="Parameters">Model parameters by name.</param>
/// <param name="LibraryPath">
/// Shared-library path for <c>Native</c> models, relative to the scenario file's directory or
/// absolute.
/// </param>
public sealed record ModelDefinition(
    string Name,
    string Kind,
    string? Integrator = null,
    Dictionary<string, double>? Parameters = null,
    string? LibraryPath = null);

/// <summary>A declared channel.</summary>
/// <param name="Name">Channel name, unique in the scenario.</param>
/// <param name="Unit">Engineering unit, or empty.</param>
/// <param name="InitialValue">Value the channel resets to.</param>
/// <param name="Description">Free-text description.</param>
public sealed record ChannelDeclaration(
    string Name,
    string Unit = "",
    double InitialValue = 0.0,
    string Description = "");

/// <summary>A declared port-to-channel mapping.</summary>
/// <param name="Model">Model that owns the port.</param>
/// <param name="Port">Port name.</param>
/// <param name="Channel">Channel name.</param>
/// <param name="ScaleA">Slope of <c>engineering = a * raw + b</c>.</param>
/// <param name="ScaleB">Offset of <c>engineering = a * raw + b</c>.</param>
public sealed record MappingDeclaration(
    string Model,
    string Port,
    string Channel,
    double ScaleA = 1.0,
    double ScaleB = 0.0);

/// <summary>A declared model-to-model link.</summary>
/// <param name="SourceModel">Model producing the value.</param>
/// <param name="SourcePort">Output port on the source.</param>
/// <param name="TargetModel">Model consuming the value.</param>
/// <param name="TargetPort">Input port on the target.</param>
/// <param name="ScaleA">Slope applied across the link.</param>
/// <param name="ScaleB">Offset applied across the link.</param>
public sealed record LinkDeclaration(
    string SourceModel,
    string SourcePort,
    string TargetModel,
    string TargetPort,
    double ScaleA = 1.0,
    double ScaleB = 0.0);

/// <summary>A declared stimulus binding.</summary>
/// <param name="Channel">Channel to drive.</param>
/// <param name="Kind">
/// Profile kind: <c>Constant</c>, <c>Step</c>, <c>Ramp</c>, <c>Sine</c> or <c>Csv</c>.
/// </param>
/// <param name="Parameters">Profile parameters by name.</param>
/// <param name="CsvPath">CSV file for <c>Csv</c> playback, relative to the scenario file or absolute.</param>
/// <param name="CsvColumn">Column within that file. Defaults to the first signal column.</param>
/// <param name="Interpolation">Gap-filling rule for playback: <c>Hold</c> or <c>Linear</c>.</param>
/// <param name="EndBehaviour">Behaviour past the last sample: <c>HoldLast</c> or <c>Loop</c>.</param>
public sealed record StimulusDeclaration(
    string Channel,
    string Kind,
    Dictionary<string, double>? Parameters = null,
    string? CsvPath = null,
    string? CsvColumn = null,
    string? Interpolation = null,
    string? EndBehaviour = null);

/// <summary>A declared acceptance band.</summary>
/// <param name="Channel">Channel to watch.</param>
/// <param name="Low">Lower limit, inclusive.</param>
/// <param name="High">Upper limit, inclusive.</param>
public sealed record LimitDeclaration(string Channel, double Low, double High);

/// <summary>Run settings that belong to the scenario rather than to a session.</summary>
/// <param name="EndTime">
/// Simulation time in seconds at which a batch run stops. Null means run until stopped.
/// </param>
/// <param name="LogDecimation">Log every Nth cycle.</param>
/// <param name="LogChannels">Channels to log, in column order. Null logs every channel.</param>
public sealed record RunSettings(
    double? EndTime = null,
    int LogDecimation = 1,
    IReadOnlyList<string>? LogChannels = null);

/// <summary>
/// The complete, serialisable description of a scenario: what runs, how it is wired, what drives
/// it, and what makes it pass. This is the only file format the runtime persists, and it is meant
/// to be readable and diffable by hand.
/// </summary>
/// <param name="FormatVersion">Document schema version.</param>
/// <param name="Name">Scenario name.</param>
/// <param name="Description">Free-text description.</param>
/// <param name="RateHz">Fixed execution rate, 1..1000 Hz.</param>
/// <param name="TimingMode"><c>Virtual</c> or <c>WallClockSynced</c>.</param>
/// <param name="Models">Model instances, in execution order.</param>
/// <param name="Channels">Channel table.</param>
/// <param name="Mappings">Port-to-channel bindings.</param>
/// <param name="Links">Model-to-model connections.</param>
/// <param name="Stimulus">Channel stimulus bindings.</param>
/// <param name="Limits">Acceptance bands.</param>
/// <param name="Run">Run settings.</param>
public sealed record ScenarioDefinition(
    int FormatVersion = ScenarioDefinition.CurrentFormatVersion,
    string Name = "scenario",
    string Description = "",
    int RateHz = 100,
    string TimingMode = "Virtual",
    IReadOnlyList<ModelDefinition>? Models = null,
    IReadOnlyList<ChannelDeclaration>? Channels = null,
    IReadOnlyList<MappingDeclaration>? Mappings = null,
    IReadOnlyList<LinkDeclaration>? Links = null,
    IReadOnlyList<StimulusDeclaration>? Stimulus = null,
    IReadOnlyList<LimitDeclaration>? Limits = null,
    RunSettings? Run = null)
{
    /// <summary>Schema version this build reads and writes.</summary>
    public const int CurrentFormatVersion = 1;
}
