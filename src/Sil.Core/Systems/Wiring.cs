using Sil.Core.Channels;
using Sil.Core.Models;

namespace Sil.Core.Systems;

/// <summary>
/// Binds one model port to one system channel. The direction is taken from the port's own
/// declaration, so a mapping cannot contradict the model.
/// </summary>
/// <param name="ModelName">Name of the model that owns the port.</param>
/// <param name="PortName">Port name within that model.</param>
/// <param name="ChannelName">Channel name within the system channel table.</param>
/// <param name="Scale">
/// Conversion between the model-side raw value and the channel-side engineering value.
/// Output ports use <see cref="LinearScale.ToEngineering"/>; input ports use the inverse.
/// </param>
public sealed record PortMapping(string ModelName, string PortName, string ChannelName, LinearScale Scale)
{
    /// <summary>Creates a pass-through mapping.</summary>
    public static PortMapping Direct(string modelName, string portName, string channelName)
        => new(modelName, portName, channelName, LinearScale.Identity);
}

/// <summary>
/// Connects one model's output port straight to another model's input port, bypassing the
/// channel table. This is how a plant and a controller are wired to each other.
/// </summary>
/// <param name="SourceModel">Model producing the value.</param>
/// <param name="SourcePort">Output port on the source model.</param>
/// <param name="TargetModel">Model consuming the value.</param>
/// <param name="TargetPort">Input port on the target model.</param>
/// <param name="Scale">Applied as <c>target = a * source + b</c>.</param>
public sealed record ModelLink(
    string SourceModel,
    string SourcePort,
    string TargetModel,
    string TargetPort,
    LinearScale Scale)
{
    /// <summary>Creates a pass-through link.</summary>
    public static ModelLink Direct(string sourceModel, string sourcePort, string targetModel, string targetPort)
        => new(sourceModel, sourcePort, targetModel, targetPort, LinearScale.Identity);
}

internal readonly record struct ResolvedMapping(IModel Model, int PortIndex, int ChannelIndex, LinearScale Scale);

internal readonly record struct ResolvedLink(
    IModel Source,
    int SourcePort,
    IModel Target,
    int TargetPort,
    LinearScale Scale);
