namespace Sil.Core.Channels;

/// <summary>
/// Declaration of one system channel. Channels are the runtime's shared value space: stimulus
/// writes them, models are mapped onto them, and logging and limit checks read them.
/// </summary>
/// <param name="Index">Zero-based index, unique within the table.</param>
/// <param name="Name">Channel name, unique within the table.</param>
/// <param name="Unit">Engineering unit, or an empty string when dimensionless.</param>
/// <param name="InitialValue">Value the channel is reset to.</param>
/// <param name="Description">Free-text description for the UI.</param>
public readonly record struct ChannelDefinition(
    int Index,
    string Name,
    string Unit,
    double InitialValue = 0.0,
    string Description = "")
{
    public override string ToString() => Unit.Length == 0 ? Name : $"{Name} [{Unit}]";
}

/// <summary>
/// A fixed set of channels plus their current values. The definition list is immutable after
/// construction; only the values change during a run.
/// </summary>
public sealed class ChannelTable
{
    private readonly ChannelDefinition[] _definitions;
    private readonly double[] _values;
    private readonly Dictionary<string, int> _byName;

    public ChannelTable(IReadOnlyList<ChannelDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        _definitions = new ChannelDefinition[definitions.Count];
        _values = new double[definitions.Count];
        _byName = new Dictionary<string, int>(definitions.Count, StringComparer.Ordinal);

        for (int i = 0; i < definitions.Count; i++)
        {
            ChannelDefinition def = definitions[i];
            if (def.Index != i)
            {
                throw new ArgumentException(
                    $"Channel '{def.Name}' declares index {def.Index} but sits at position {i}.",
                    nameof(definitions));
            }

            if (string.IsNullOrWhiteSpace(def.Name))
            {
                throw new ArgumentException($"Channel at index {i} has no name.", nameof(definitions));
            }

            if (!double.IsFinite(def.InitialValue))
            {
                throw new ArgumentException(
                    $"Channel '{def.Name}' has a non-finite initial value.", nameof(definitions));
            }

            if (!_byName.TryAdd(def.Name, i))
            {
                throw new ArgumentException($"Duplicate channel name '{def.Name}'.", nameof(definitions));
            }

            _definitions[i] = def;
        }

        Reset();
    }

    /// <summary>Number of channels.</summary>
    public int Count => _definitions.Length;

    /// <summary>The channel declarations, in index order.</summary>
    public IReadOnlyList<ChannelDefinition> Definitions => _definitions;

    /// <summary>Current values, in index order. Read-only view for logging and display.</summary>
    public ReadOnlySpan<double> Values => _values;

    /// <summary>Returns the index of a named channel, or -1 when absent.</summary>
    public int IndexOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.TryGetValue(name, out int index) ? index : -1;
    }

    /// <summary>Returns the index of a named channel, or throws when absent.</summary>
    public int Require(string name)
    {
        int index = IndexOf(name);
        if (index < 0)
        {
            throw new ArgumentException($"No channel named '{name}'.", nameof(name));
        }

        return index;
    }

    /// <summary>The declaration at an index.</summary>
    public ChannelDefinition Definition(int index)
    {
        CheckIndex(index);
        return _definitions[index];
    }

    /// <summary>Reads a channel value.</summary>
    public double Get(int index)
    {
        CheckIndex(index);
        return _values[index];
    }

    /// <summary>Writes a channel value.</summary>
    public void Set(int index, double value)
    {
        CheckIndex(index);
        _values[index] = value;
    }

    /// <summary>Reads a channel value by name.</summary>
    public double Get(string name) => _values[Require(name)];

    /// <summary>Writes a channel value by name.</summary>
    public void Set(string name, double value) => _values[Require(name)] = value;

    /// <summary>Restores every channel to its declared initial value.</summary>
    public void Reset()
    {
        for (int i = 0; i < _values.Length; i++)
        {
            _values[i] = _definitions[i].InitialValue;
        }
    }

    /// <summary>Copies the current values into <paramref name="destination"/>.</summary>
    public void CopyValuesTo(Span<double> destination)
    {
        if (destination.Length < _values.Length)
        {
            throw new ArgumentException(
                $"Destination holds {destination.Length} values but the table has {_values.Length}.",
                nameof(destination));
        }

        _values.AsSpan().CopyTo(destination);
    }

    private void CheckIndex(int index)
    {
        if ((uint)index >= (uint)_definitions.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), index, $"The channel table holds {_definitions.Length} channels.");
        }
    }
}

/// <summary>Accumulates channel declarations and assigns their indices.</summary>
public sealed class ChannelTableBuilder
{
    private readonly List<ChannelDefinition> _definitions = [];

    /// <summary>Number of channels declared so far.</summary>
    public int Count => _definitions.Count;

    /// <summary>Declares a channel and returns its assigned index.</summary>
    public int Add(string name, string unit = "", double initialValue = 0.0, string description = "")
    {
        int index = _definitions.Count;
        _definitions.Add(new ChannelDefinition(index, name, unit, initialValue, description));
        return index;
    }

    /// <summary>Builds the table, validating names and indices.</summary>
    public ChannelTable Build() => new(_definitions);
}
