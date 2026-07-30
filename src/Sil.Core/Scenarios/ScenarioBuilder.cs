using System.Globalization;
using Sil.Core.Channels;
using Sil.Core.Engine;
using Sil.Core.Logging;
using Sil.Core.Models;
using Sil.Core.Models.Builtin;
using Sil.Core.Native;
using Sil.Core.Numerics;
using Sil.Core.Runtime;
using Sil.Core.Stimulus;
using Sil.Core.Systems;
using Sil.Core.Timing;

namespace Sil.Core.Scenarios;

/// <summary>
/// A scenario turned into runnable objects: the wired system plus the tasks that drive and judge
/// it. Disposing this disposes the system and its models.
/// </summary>
public sealed class RunnableScenario : IDisposable
{
    private bool _disposed;

    internal RunnableScenario(
        ScenarioDefinition definition,
        SilSystem system,
        StimulusTask? stimulus,
        LimitMonitor monitor,
        SimRate rate,
        TimingMode timingMode)
    {
        Definition = definition;
        System = system;
        Stimulus = stimulus;
        Monitor = monitor;
        Rate = rate;
        TimingMode = timingMode;
    }

    /// <summary>The document this was built from.</summary>
    public ScenarioDefinition Definition { get; }

    /// <summary>The wired system.</summary>
    public SilSystem System { get; }

    /// <summary>Stimulus task, or null when the scenario declares none.</summary>
    public StimulusTask? Stimulus { get; }

    /// <summary>Limit monitor. Always present, with no bands when none are declared.</summary>
    public LimitMonitor Monitor { get; }

    /// <summary>Validated execution rate.</summary>
    public SimRate Rate { get; }

    /// <summary>Validated timing mode.</summary>
    public TimingMode TimingMode { get; }

    /// <summary>
    /// Creates an engine for this scenario. Extra recorders (a CSV logger, live traces) are
    /// appended after the limit monitor.
    /// </summary>
    public SimEngine CreateEngine(IEnumerable<ISimTask>? extraRecorders = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var recorders = new List<ISimTask> { Monitor };
        if (extraRecorders is not null)
        {
            recorders.AddRange(extraRecorders);
        }

        var options = new EngineOptions { TimingMode = TimingMode };
        IEnumerable<ISimTask>? stimulus = Stimulus is null ? null : [Stimulus];

        return System.CreateEngine(Rate, options, stimulus, recorders);
    }

    /// <summary>
    /// Runs the scenario to its declared end time and returns the verdict. Requires the document
    /// to declare <c>run.endTime</c>.
    /// </summary>
    public ScenarioResult RunToCompletion(
        IEnumerable<ISimTask>? extraRecorders = null,
        CancellationToken cancellationToken = default)
    {
        double endTime = Definition.Run?.EndTime
            ?? throw new ScenarioFormatException(
                $"Scenario '{Definition.Name}' has no run.endTime, so it cannot be run as a batch.");

        SimEngine engine = CreateEngine(extraRecorders);
        engine.RunUntil(endTime, cancellationToken);
        return Monitor.Finish();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        System.Dispose();
    }
}

/// <summary>
/// Turns a <see cref="ScenarioDefinition"/> into runnable objects, validating as it goes.
/// </summary>
/// <remarks>
/// Unknown kinds and unknown parameter names are errors, never defaults. A scenario that quietly
/// ignored a misspelled gain would produce numbers that look plausible and are wrong, which is
/// exactly the failure this product exists to prevent.
/// </remarks>
public static class ScenarioBuilder
{
    /// <summary>Model kinds this build understands.</summary>
    public static readonly string[] ModelKinds =
        ["FirstOrderLag", "MassSpringDamper", "PiController", "Native"];

    /// <summary>Stimulus kinds this build understands.</summary>
    public static readonly string[] StimulusKinds = ["Constant", "Step", "Ramp", "Sine", "Csv"];

    /// <summary>
    /// Builds a runnable scenario.
    /// </summary>
    /// <param name="definition">The scenario document.</param>
    /// <param name="baseDirectory">
    /// Directory that relative paths (native libraries, stimulus CSVs) resolve against — normally
    /// the folder holding the scenario file. Defaults to the current directory.
    /// </param>
    public static RunnableScenario Build(ScenarioDefinition definition, string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        string root = baseDirectory ?? Directory.GetCurrentDirectory();

        if (!SimRate.TryFromHz(definition.RateHz, out SimRate rate))
        {
            throw new ScenarioFormatException(
                $"rateHz {definition.RateHz} is outside the supported range " +
                $"{SimRate.MinHz}..{SimRate.MaxHz}.");
        }

        TimingMode timingMode = definition.TimingMode switch
        {
            "Virtual" => TimingMode.Virtual,
            "WallClockSynced" => TimingMode.WallClockSynced,
            _ => throw new ScenarioFormatException(
                $"Unknown timingMode '{definition.TimingMode}'. Expected 'Virtual' or 'WallClockSynced'."),
        };

        var builder = new SilSystemBuilder();
        var created = new List<IModel>();

        try
        {
            foreach (ModelDefinition model in definition.Models ?? [])
            {
                IModel instance = CreateModel(model, root);
                created.Add(instance);
                builder.AddModel(instance);
            }

            foreach (ChannelDeclaration channel in definition.Channels ?? [])
            {
                builder.AddChannel(channel.Name, channel.Unit, channel.InitialValue, channel.Description);
            }

            foreach (MappingDeclaration mapping in definition.Mappings ?? [])
            {
                builder.Map(mapping.Model, mapping.Port, mapping.Channel,
                    Scale(mapping.ScaleA, mapping.ScaleB, $"mapping {mapping.Model}.{mapping.Port}"));
            }

            foreach (LinkDeclaration link in definition.Links ?? [])
            {
                builder.Link(link.SourceModel, link.SourcePort, link.TargetModel, link.TargetPort,
                    Scale(link.ScaleA, link.ScaleB,
                        $"link {link.SourceModel}.{link.SourcePort} -> {link.TargetModel}.{link.TargetPort}"));
            }

            SilSystem system;
            try
            {
                system = builder.Build();
            }
            catch (SystemWiringException ex)
            {
                throw new ScenarioFormatException($"Scenario '{definition.Name}': {ex.Message}", ex);
            }

            // The system owns the models from here; a later failure disposes it, not the list.
            created.Clear();

            try
            {
                StimulusTask? stimulus = CreateStimulus(definition, system.Channels, root);
                LimitMonitor monitor = CreateMonitor(definition, system.Channels);

                return new RunnableScenario(definition, system, stimulus, monitor, rate, timingMode);
            }
            catch
            {
                system.Dispose();
                throw;
            }
        }
        catch
        {
            foreach (IModel model in created)
            {
                model.Dispose();
            }

            throw;
        }
    }

    /// <summary>Loads a scenario file and builds it, resolving relative paths against its folder.</summary>
    public static RunnableScenario Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        ScenarioDefinition definition = ScenarioFile.Load(path);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        return Build(definition, directory);
    }

    /// <summary>Creates ring-buffer traces for every channel a scenario declares.</summary>
    public static IReadOnlyList<ChannelTrace> CreateTraces(RunnableScenario scenario, int capacity)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        return [.. scenario.System.Channels.Definitions.Select(d => new ChannelTrace(d.Name, capacity))];
    }

    private static IModel CreateModel(ModelDefinition definition, string root)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            throw new ScenarioFormatException("A model has no name.");
        }

        var parameters = new ParameterBag(definition.Parameters, $"model '{definition.Name}'");

        IModel model = definition.Kind switch
        {
            "FirstOrderLag" => new FirstOrderLagModel(
                definition.Name,
                parameters.Optional("timeConstant", 1.0),
                parameters.Optional("gain", 1.0),
                parameters.Optional("initialValue", 0.0),
                ParseIntegrator(definition)),

            "MassSpringDamper" => new MassSpringDamperModel(
                definition.Name,
                parameters.Optional("mass", 1.0),
                parameters.Optional("damping", 0.0),
                parameters.Optional("stiffness", 1.0),
                parameters.Optional("initialPosition", 0.0),
                parameters.Optional("initialVelocity", 0.0),
                ParseIntegrator(definition)),

            "PiController" => new PiControllerModel(
                definition.Name,
                parameters.Optional("proportionalGain", 1.0),
                parameters.Optional("integralGain", 0.0),
                parameters.Optional("outputMinimum", double.NegativeInfinity),
                parameters.Optional("outputMaximum", double.PositiveInfinity)),

            "Native" => LoadNative(definition, root),

            _ => throw new ScenarioFormatException(
                $"Model '{definition.Name}' has unknown kind '{definition.Kind}'. " +
                $"Expected one of: {string.Join(", ", ModelKinds)}."),
        };

        try
        {
            parameters.RejectUnused();
        }
        catch
        {
            model.Dispose();
            throw;
        }

        return model;
    }

    private static IModel LoadNative(ModelDefinition definition, string root)
    {
        if (string.IsNullOrWhiteSpace(definition.LibraryPath))
        {
            throw new ScenarioFormatException(
                $"Native model '{definition.Name}' has no libraryPath.");
        }

        string path = Path.IsPathRooted(definition.LibraryPath)
            ? definition.LibraryPath
            : Path.Combine(root, definition.LibraryPath);

        try
        {
            return NativeModelLoader.Load(path, definition.Name);
        }
        catch (SilNativeException ex)
        {
            throw new ScenarioFormatException(
                $"Native model '{definition.Name}': {ex.Message}", ex);
        }
    }

    private static IntegratorKind ParseIntegrator(ModelDefinition definition) => definition.Integrator switch
    {
        null or "" or "Rk4" => IntegratorKind.Rk4,
        "Euler" => IntegratorKind.Euler,
        _ => throw new ScenarioFormatException(
            $"Model '{definition.Name}' requests unknown integrator '{definition.Integrator}'. " +
            "Expected 'Euler' or 'Rk4'."),
    };

    private static StimulusTask? CreateStimulus(
        ScenarioDefinition definition, ChannelTable channels, string root)
    {
        IReadOnlyList<StimulusDeclaration> declarations = definition.Stimulus ?? [];
        if (declarations.Count == 0)
        {
            return null;
        }

        var bindings = new List<StimulusBinding>(declarations.Count);
        foreach (StimulusDeclaration declaration in declarations)
        {
            bindings.Add(new StimulusBinding(declaration.Channel, CreateProfile(declaration, root)));
        }

        try
        {
            return new StimulusTask(channels, bindings);
        }
        catch (ArgumentException ex)
        {
            throw new ScenarioFormatException($"Scenario '{definition.Name}' stimulus: {ex.Message}", ex);
        }
    }

    private static IStimulusProfile CreateProfile(StimulusDeclaration declaration, string root)
    {
        var parameters = new ParameterBag(
            declaration.Parameters, $"stimulus on channel '{declaration.Channel}'");

        IStimulusProfile profile;
        try
        {
            profile = declaration.Kind switch
            {
                "Constant" => new ConstantProfile(parameters.Required("value")),

                "Step" => new StepProfile(
                    parameters.Optional("startTime", 0.0),
                    parameters.Optional("before", 0.0),
                    parameters.Required("after")),

                "Ramp" => new RampProfile(
                    parameters.Optional("startTime", 0.0),
                    parameters.Required("duration"),
                    parameters.Optional("from", 0.0),
                    parameters.Required("to")),

                "Sine" => new SineProfile(
                    parameters.Required("amplitude"),
                    parameters.Required("frequencyHz"),
                    parameters.Optional("phaseRadians", 0.0),
                    parameters.Optional("offset", 0.0),
                    parameters.Optional("startTime", 0.0)),

                "Csv" => LoadCsvProfile(declaration, root),

                _ => throw new ScenarioFormatException(
                    $"Stimulus on channel '{declaration.Channel}' has unknown kind " +
                    $"'{declaration.Kind}'. Expected one of: {string.Join(", ", StimulusKinds)}."),
            };
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new ScenarioFormatException(
                $"Stimulus on channel '{declaration.Channel}': {ex.Message}", ex);
        }

        parameters.RejectUnused();
        return profile;
    }

    private static IStimulusProfile LoadCsvProfile(StimulusDeclaration declaration, string root)
    {
        if (string.IsNullOrWhiteSpace(declaration.CsvPath))
        {
            throw new ScenarioFormatException(
                $"Csv stimulus on channel '{declaration.Channel}' has no csvPath.");
        }

        string path = Path.IsPathRooted(declaration.CsvPath)
            ? declaration.CsvPath
            : Path.Combine(root, declaration.CsvPath);

        PlaybackInterpolation interpolation = declaration.Interpolation switch
        {
            null or "" or "Linear" => PlaybackInterpolation.Linear,
            "Hold" => PlaybackInterpolation.Hold,
            _ => throw new ScenarioFormatException(
                $"Csv stimulus on channel '{declaration.Channel}' requests unknown interpolation " +
                $"'{declaration.Interpolation}'. Expected 'Hold' or 'Linear'."),
        };

        PlaybackEndBehaviour endBehaviour = declaration.EndBehaviour switch
        {
            null or "" or "HoldLast" => PlaybackEndBehaviour.HoldLast,
            "Loop" => PlaybackEndBehaviour.Loop,
            _ => throw new ScenarioFormatException(
                $"Csv stimulus on channel '{declaration.Channel}' requests unknown endBehaviour " +
                $"'{declaration.EndBehaviour}'. Expected 'HoldLast' or 'Loop'."),
        };

        if (!File.Exists(path))
        {
            throw new ScenarioFormatException(
                $"Csv stimulus on channel '{declaration.Channel}' refers to a missing file " +
                $"'{Path.GetFullPath(path)}'.");
        }

        IReadOnlyList<CsvStimulusColumn> columns;
        try
        {
            columns = CsvStimulusReader.ReadFile(path, interpolation, endBehaviour);
        }
        catch (StimulusFormatException ex)
        {
            throw new ScenarioFormatException(
                $"Csv stimulus on channel '{declaration.Channel}': {ex.Message}", ex);
        }

        if (declaration.CsvColumn is null or "")
        {
            return columns[0].Profile;
        }

        foreach (CsvStimulusColumn column in columns)
        {
            if (string.Equals(column.Name, declaration.CsvColumn, StringComparison.Ordinal))
            {
                return column.Profile;
            }
        }

        throw new ScenarioFormatException(
            $"Csv stimulus on channel '{declaration.Channel}' asks for column " +
            $"'{declaration.CsvColumn}', which '{Path.GetFileName(path)}' does not contain. " +
            $"Available: {string.Join(", ", columns.Select(c => c.Name))}.");
    }

    private static LimitMonitor CreateMonitor(ScenarioDefinition definition, ChannelTable channels)
    {
        var limits = new List<ChannelLimit>();
        foreach (LimitDeclaration limit in definition.Limits ?? [])
        {
            limits.Add(new ChannelLimit(limit.Channel, limit.Low, limit.High));
        }

        try
        {
            return new LimitMonitor(channels, limits);
        }
        catch (ArgumentException ex)
        {
            throw new ScenarioFormatException($"Scenario '{definition.Name}' limits: {ex.Message}", ex);
        }
    }

    private static LinearScale Scale(double a, double b, string context)
    {
        try
        {
            return LinearScale.Create(a, b);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new ScenarioFormatException($"{context}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Tracks which declared parameters were actually consumed so an unrecognised name can be
    /// reported instead of silently ignored.
    /// </summary>
    private sealed class ParameterBag(IReadOnlyDictionary<string, double>? values, string context)
    {
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);

        public double Required(string name)
        {
            _used.Add(name);

            if (values is null || !values.TryGetValue(name, out double value))
            {
                throw new ScenarioFormatException($"{context} is missing required parameter '{name}'.");
            }

            Validate(name, value);
            return value;
        }

        public double Optional(string name, double fallback)
        {
            _used.Add(name);

            if (values is null || !values.TryGetValue(name, out double value))
            {
                return fallback;
            }

            Validate(name, value);
            return value;
        }

        public void RejectUnused()
        {
            if (values is null)
            {
                return;
            }

            List<string> unknown = [.. values.Keys.Where(key => !_used.Contains(key)).Order(StringComparer.Ordinal)];
            if (unknown.Count > 0)
            {
                throw new ScenarioFormatException(
                    $"{context} declares unknown parameter(s): {string.Join(", ", unknown)}. " +
                    $"Recognised: {string.Join(", ", _used.Order(StringComparer.Ordinal))}.");
            }
        }

        private void Validate(string name, double value)
        {
            if (!double.IsFinite(value))
            {
                throw new ScenarioFormatException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{context} parameter '{name}' is not finite ({value})."));
            }
        }
    }
}
