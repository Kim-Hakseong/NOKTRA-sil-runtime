using Sil.Core.Channels;
using Sil.Core.Models;

namespace Sil.Core.Systems;

/// <summary>
/// Raised when a system definition is inconsistent: an unknown name, a port used in the wrong
/// direction, or two writers fighting over one destination.
/// </summary>
public sealed class SystemWiringException : Exception
{
    public SystemWiringException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Collects models, channels, mappings and links, then validates and resolves them into a
/// <see cref="SilSystem"/>. All name resolution and conflict checking happens here so the cycle
/// itself is index-based and branch-free.
/// </summary>
public sealed class SilSystemBuilder
{
    private readonly List<IModel> _models = [];
    private readonly ChannelTableBuilder _channels = new();
    private readonly List<PortMapping> _mappings = [];
    private readonly List<ModelLink> _links = [];

    /// <summary>Adds a model. Model names must be unique within the system.</summary>
    public SilSystemBuilder AddModel(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        foreach (IModel existing in _models)
        {
            if (string.Equals(existing.Name, model.Name, StringComparison.Ordinal))
            {
                throw new SystemWiringException($"Duplicate model name '{model.Name}'.");
            }
        }

        _models.Add(model);
        return this;
    }

    /// <summary>Declares a channel and returns its index.</summary>
    public int AddChannel(string name, string unit = "", double initialValue = 0.0, string description = "")
        => _channels.Add(name, unit, initialValue, description);

    /// <summary>Binds a model port to a channel.</summary>
    public SilSystemBuilder Map(string modelName, string portName, string channelName, LinearScale scale)
    {
        _mappings.Add(new PortMapping(modelName, portName, channelName, scale));
        return this;
    }

    /// <summary>Binds a model port to a channel with a pass-through scale.</summary>
    public SilSystemBuilder Map(string modelName, string portName, string channelName)
        => Map(modelName, portName, channelName, LinearScale.Identity);

    /// <summary>Adds a declared mapping.</summary>
    public SilSystemBuilder Map(PortMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        _mappings.Add(mapping);
        return this;
    }

    /// <summary>Connects an output port of one model to an input port of another.</summary>
    public SilSystemBuilder Link(
        string sourceModel, string sourcePort, string targetModel, string targetPort, LinearScale scale)
    {
        _links.Add(new ModelLink(sourceModel, sourcePort, targetModel, targetPort, scale));
        return this;
    }

    /// <summary>Connects two model ports with a pass-through scale.</summary>
    public SilSystemBuilder Link(string sourceModel, string sourcePort, string targetModel, string targetPort)
        => Link(sourceModel, sourcePort, targetModel, targetPort, LinearScale.Identity);

    /// <summary>Adds a declared link.</summary>
    public SilSystemBuilder Link(ModelLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        _links.Add(link);
        return this;
    }

    /// <summary>Validates every reference and produces the runnable system.</summary>
    /// <exception cref="SystemWiringException">The definition is inconsistent.</exception>
    public SilSystem Build()
    {
        ChannelTable channels;
        try
        {
            channels = _channels.Build();
        }
        catch (ArgumentException ex)
        {
            throw new SystemWiringException(ex.Message);
        }

        IModel[] models = [.. _models];

        var inputMappings = new List<ResolvedMapping>();
        var outputMappings = new List<ResolvedMapping>();
        var inputWriters = new Dictionary<(string Model, int Port), string>();
        var channelWriters = new Dictionary<int, string>();

        foreach (PortMapping mapping in _mappings)
        {
            if (!mapping.Scale.IsValid)
            {
                throw new SystemWiringException(
                    $"Mapping {Describe(mapping)} uses a non-invertible scale ({mapping.Scale}).");
            }

            IModel model = ResolveModel(models, mapping.ModelName, Describe(mapping));
            int portIndex = ResolvePort(model, mapping.PortName, Describe(mapping));
            int channelIndex = channels.IndexOf(mapping.ChannelName);
            if (channelIndex < 0)
            {
                throw new SystemWiringException(
                    $"Mapping {Describe(mapping)} refers to unknown channel '{mapping.ChannelName}'.");
            }

            var resolved = new ResolvedMapping(model, portIndex, channelIndex, mapping.Scale);

            if (model.Ports[portIndex].Direction == PortDirection.Input)
            {
                ClaimInput(inputWriters, model.Name, portIndex, Describe(mapping));
                inputMappings.Add(resolved);
            }
            else
            {
                if (channelWriters.TryGetValue(channelIndex, out string? owner))
                {
                    throw new SystemWiringException(
                        $"Channel '{mapping.ChannelName}' is written by both {owner} and {Describe(mapping)}.");
                }

                channelWriters[channelIndex] = Describe(mapping);
                outputMappings.Add(resolved);
            }
        }

        var resolvedLinks = new List<ResolvedLink>();
        foreach (ModelLink link in _links)
        {
            if (!link.Scale.IsValid)
            {
                throw new SystemWiringException(
                    $"Link {Describe(link)} uses a non-invertible scale ({link.Scale}).");
            }

            IModel source = ResolveModel(models, link.SourceModel, Describe(link));
            IModel target = ResolveModel(models, link.TargetModel, Describe(link));
            int sourcePort = ResolvePort(source, link.SourcePort, Describe(link));
            int targetPort = ResolvePort(target, link.TargetPort, Describe(link));

            if (source.Ports[sourcePort].Direction != PortDirection.Output)
            {
                throw new SystemWiringException(
                    $"Link {Describe(link)} takes its value from '{link.SourcePort}', which is an input port.");
            }

            if (target.Ports[targetPort].Direction != PortDirection.Input)
            {
                throw new SystemWiringException(
                    $"Link {Describe(link)} writes '{link.TargetPort}', which is an output port.");
            }

            ClaimInput(inputWriters, target.Name, targetPort, Describe(link));
            resolvedLinks.Add(new ResolvedLink(source, sourcePort, target, targetPort, link.Scale));
        }

        return new SilSystem(
            channels,
            models,
            [.. _mappings],
            [.. _links],
            [.. inputMappings],
            [.. outputMappings],
            [.. resolvedLinks]);
    }

    private static void ClaimInput(
        Dictionary<(string Model, int Port), string> writers, string modelName, int portIndex, string claimant)
    {
        if (writers.TryGetValue((modelName, portIndex), out string? owner))
        {
            throw new SystemWiringException(
                $"Input port {modelName}.{portIndex} is written by both {owner} and {claimant}.");
        }

        writers[(modelName, portIndex)] = claimant;
    }

    private static IModel ResolveModel(IModel[] models, string name, string context)
    {
        foreach (IModel model in models)
        {
            if (string.Equals(model.Name, name, StringComparison.Ordinal))
            {
                return model;
            }
        }

        throw new SystemWiringException($"{context} refers to unknown model '{name}'.");
    }

    private static int ResolvePort(IModel model, string portName, string context)
    {
        int index = model.IndexOfPort(portName);
        if (index < 0)
        {
            throw new SystemWiringException(
                $"{context} refers to port '{portName}', which model '{model.Name}' does not declare.");
        }

        return index;
    }

    private static string Describe(PortMapping m) => $"{m.ModelName}.{m.PortName} <-> {m.ChannelName}";

    private static string Describe(ModelLink l)
        => $"{l.SourceModel}.{l.SourcePort} -> {l.TargetModel}.{l.TargetPort}";
}
