using Sil.Core.Channels;
using Sil.Core.Engine;
using Sil.Core.Models;

namespace Sil.Core.Systems;

/// <summary>
/// Brings channels and models to their t=0 condition. It runs first in the cycle and does nothing
/// on a step. Initializing a model clears its ports, so this has to happen before any wiring or
/// stimulus is applied — which it does simply by sitting at the head of the list, since tasks are
/// initialized in cycle order.
/// </summary>
internal sealed class SystemInitTask : ISimTask
{
    private readonly ChannelTable _channels;
    private readonly IModel[] _models;

    internal SystemInitTask(ChannelTable channels, IModel[] models)
    {
        _channels = channels;
        _models = models;
    }

    public string Name => "system-init";

    public void Initialize(in StepContext ctx)
    {
        _channels.Reset();

        foreach (IModel model in _models)
        {
            model.Initialize();
        }
    }

    public void Step(in StepContext ctx)
    {
    }
}

/// <summary>Copies channel values into the model input ports bound to them.</summary>
internal sealed class ChannelToInputTask : ISimTask
{
    private readonly ResolvedMapping[] _mappings;
    private readonly ChannelTable _channels;

    internal ChannelToInputTask(ChannelTable channels, ResolvedMapping[] mappings)
    {
        _channels = channels;
        _mappings = mappings;
    }

    public string Name => "channels->inputs";

    public void Initialize(in StepContext ctx) => Apply();

    public void Step(in StepContext ctx) => Apply();

    private void Apply()
    {
        foreach (ResolvedMapping m in _mappings)
        {
            m.Model.SetPort(m.PortIndex, m.Scale.ToRaw(_channels.Get(m.ChannelIndex)));
        }
    }
}

/// <summary>Copies model output ports out to the channels bound to them.</summary>
internal sealed class OutputToChannelTask : ISimTask
{
    private readonly ResolvedMapping[] _mappings;
    private readonly ChannelTable _channels;

    internal OutputToChannelTask(ChannelTable channels, ResolvedMapping[] mappings)
    {
        _channels = channels;
        _mappings = mappings;
    }

    public string Name => "outputs->channels";

    public void Initialize(in StepContext ctx) => Apply();

    public void Step(in StepContext ctx) => Apply();

    private void Apply()
    {
        foreach (ResolvedMapping m in _mappings)
        {
            _channels.Set(m.ChannelIndex, m.Scale.ToEngineering(m.Model.GetPort(m.PortIndex)));
        }
    }
}

/// <summary>
/// Propagates model-to-model links. It runs before the models step, so a value crossing a link
/// is the one its source published at the end of the previous cycle. In a feedback loop that is
/// a deliberate one-cycle transport delay: it breaks the algebraic loop and keeps the cycle
/// order fixed and deterministic.
/// </summary>
internal sealed class ModelLinkTask : ISimTask
{
    private readonly ResolvedLink[] _links;

    internal ModelLinkTask(ResolvedLink[] links)
    {
        _links = links;
    }

    public string Name => "model-links";

    public void Initialize(in StepContext ctx) => Apply();

    public void Step(in StepContext ctx) => Apply();

    private void Apply()
    {
        foreach (ResolvedLink link in _links)
        {
            double source = link.Source.GetPort(link.SourcePort);
            link.Target.SetPort(link.TargetPort, link.Scale.ToEngineering(source));
        }
    }
}
