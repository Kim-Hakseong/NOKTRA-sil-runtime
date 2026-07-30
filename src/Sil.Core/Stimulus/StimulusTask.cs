using Sil.Core.Channels;
using Sil.Core.Engine;

namespace Sil.Core.Stimulus;

/// <summary>Binds one stimulus profile to one channel.</summary>
/// <param name="ChannelName">Target channel.</param>
/// <param name="Profile">Profile sampled at the cycle's simulation time.</param>
public sealed record StimulusBinding(string ChannelName, IStimulusProfile Profile);

/// <summary>
/// Writes stimulus values into channels at the head of each cycle, before mappings copy them
/// into model inputs. Profiles are sampled at the simulation time of the cycle about to run.
/// </summary>
public sealed class StimulusTask : ISimTask
{
    private readonly ChannelTable _channels;
    private readonly int[] _channelIndices;
    private readonly IStimulusProfile[] _profiles;

    public StimulusTask(ChannelTable channels, IReadOnlyList<StimulusBinding> bindings, string name = "stimulus")
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _channels = channels;
        _channelIndices = new int[bindings.Count];
        _profiles = new IStimulusProfile[bindings.Count];
        Name = name;

        var claimed = new Dictionary<int, string>();
        for (int i = 0; i < bindings.Count; i++)
        {
            StimulusBinding binding = bindings[i];
            ArgumentNullException.ThrowIfNull(binding);
            ArgumentNullException.ThrowIfNull(binding.Profile);

            int index = _channels.IndexOf(binding.ChannelName);
            if (index < 0)
            {
                throw new ArgumentException(
                    $"Stimulus targets unknown channel '{binding.ChannelName}'.", nameof(bindings));
            }

            if (claimed.TryGetValue(index, out string? owner))
            {
                throw new ArgumentException(
                    $"Channel '{binding.ChannelName}' already has a stimulus ({owner}).", nameof(bindings));
            }

            claimed[index] = binding.Profile.GetType().Name;
            _channelIndices[i] = index;
            _profiles[i] = binding.Profile;
        }
    }

    public string Name { get; }

    /// <summary>Number of channels this task drives.</summary>
    public int BindingCount => _profiles.Length;

    public void Initialize(in StepContext ctx) => Apply(0.0);

    public void Step(in StepContext ctx) => Apply(ctx.Time);

    private void Apply(double t)
    {
        for (int i = 0; i < _profiles.Length; i++)
        {
            _channels.Set(_channelIndices[i], _profiles[i].ValueAt(t));
        }
    }
}
