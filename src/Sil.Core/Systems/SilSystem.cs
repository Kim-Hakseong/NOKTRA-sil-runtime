using Sil.Core.Channels;
using Sil.Core.Engine;
using Sil.Core.Models;
using Sil.Core.Timing;

namespace Sil.Core.Systems;

/// <summary>
/// A validated, wired-up set of models and channels ready to be handed to a
/// <see cref="SimEngine"/>. Building a system resolves every name to an index and rejects
/// contradictory wiring, so nothing has to be looked up or checked inside the cycle.
/// </summary>
public sealed class SilSystem : IDisposable
{
    private readonly IModel[] _models;
    private readonly ResolvedMapping[] _inputMappings;
    private readonly ResolvedMapping[] _outputMappings;
    private readonly ResolvedLink[] _links;
    private bool _disposed;

    internal SilSystem(
        ChannelTable channels,
        IModel[] models,
        IReadOnlyList<PortMapping> mappings,
        IReadOnlyList<ModelLink> links,
        ResolvedMapping[] inputMappings,
        ResolvedMapping[] outputMappings,
        ResolvedLink[] resolvedLinks)
    {
        Channels = channels;
        _models = models;
        Mappings = mappings;
        Links = links;
        _inputMappings = inputMappings;
        _outputMappings = outputMappings;
        _links = resolvedLinks;
    }

    /// <summary>The system channel table.</summary>
    public ChannelTable Channels { get; }

    /// <summary>Models in declaration order — the order they step in.</summary>
    public IReadOnlyList<IModel> Models => _models;

    /// <summary>Port-to-channel mappings as declared.</summary>
    public IReadOnlyList<PortMapping> Mappings { get; }

    /// <summary>Model-to-model links as declared.</summary>
    public IReadOnlyList<ModelLink> Links { get; }

    /// <summary>Looks up a model by name, or returns null.</summary>
    public IModel? FindModel(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (IModel model in _models)
        {
            if (string.Equals(model.Name, name, StringComparison.Ordinal))
            {
                return model;
            }
        }

        return null;
    }

    /// <summary>
    /// Composes one cycle in data-flow order:
    /// stimulus -&gt; links -&gt; channels to inputs -&gt; models step -&gt; outputs to channels -&gt; recorders.
    /// </summary>
    /// <param name="stimulus">Tasks that write channels before the models read them.</param>
    /// <param name="recorders">Tasks that read channels after the models have written them.</param>
    public IReadOnlyList<ISimTask> BuildCycle(
        IEnumerable<ISimTask>? stimulus = null,
        IEnumerable<ISimTask>? recorders = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tasks = new List<ISimTask>
        {
            new SystemInitTask(Channels, _models),
        };

        if (stimulus is not null)
        {
            tasks.AddRange(stimulus);
        }

        if (_links.Length > 0)
        {
            tasks.Add(new ModelLinkTask(_links));
        }

        if (_inputMappings.Length > 0)
        {
            tasks.Add(new ChannelToInputTask(Channels, _inputMappings));
        }

        foreach (IModel model in _models)
        {
            tasks.Add(new ModelTask(model, initializeOnReset: false));
        }

        if (_outputMappings.Length > 0)
        {
            tasks.Add(new OutputToChannelTask(Channels, _outputMappings));
        }

        if (recorders is not null)
        {
            tasks.AddRange(recorders);
        }

        return tasks;
    }

    /// <summary>Creates an engine that runs this system at the given rate.</summary>
    public SimEngine CreateEngine(
        SimRate rate,
        EngineOptions? options = null,
        IEnumerable<ISimTask>? stimulus = null,
        IEnumerable<ISimTask>? recorders = null)
    {
        IReadOnlyList<ISimTask> cycle = BuildCycle(stimulus, recorders);
        var engine = new SimEngine(new SimClock(rate), cycle, options);

        // Channels carry state across a run, so they reset with the engine.
        engine.Reset();
        return engine;
    }

    /// <summary>Restores channels to their initial values and re-initializes every model.</summary>
    public void ResetState()
    {
        Channels.Reset();
        foreach (IModel model in _models)
        {
            model.Initialize();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (IModel model in _models)
        {
            model.Dispose();
        }
    }
}
