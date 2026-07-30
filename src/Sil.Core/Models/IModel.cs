namespace Sil.Core.Models;

/// <summary>Direction of a model port as seen from inside the model.</summary>
public enum PortDirection
{
    /// <summary>The model reads this port; the runtime writes it.</summary>
    Input = 0,

    /// <summary>The model writes this port; the runtime reads it.</summary>
    Output = 1,
}

/// <summary>
/// Declaration of one model port. Ports are addressed by contiguous zero-based index so that the
/// managed model API and the native C ABI (spec/native-abi.md) describe the same table.
/// </summary>
/// <param name="Index">Zero-based port index, unique within the model.</param>
/// <param name="Name">Port name, unique within the model.</param>
/// <param name="Direction">Whether the model reads or writes the port.</param>
/// <param name="Unit">Engineering unit, or an empty string when dimensionless.</param>
public readonly record struct PortDescriptor(int Index, string Name, PortDirection Direction, string Unit)
{
    public override string ToString() => $"{Index}:{Name}[{Direction}]{(Unit.Length == 0 ? string.Empty : " " + Unit)}";
}

/// <summary>
/// A steppable simulation model. The contract is deliberately the smallest thing that both a
/// managed model and a compiled C model can satisfy: initialise, declare ports, step by dt, and
/// exchange port values as doubles.
/// </summary>
/// <remarks>
/// Implementations must be deterministic: for a given initial state and a given sequence of
/// input writes, the sequence of output values must be identical on every run.
/// </remarks>
public interface IModel : IDisposable
{
    /// <summary>Instance name, unique inside a system. Used in channel paths and logs.</summary>
    string Name { get; }

    /// <summary>The port table. Index <c>i</c> of the list is the port with <c>Index == i</c>.</summary>
    IReadOnlyList<PortDescriptor> Ports { get; }

    /// <summary>Brings the model to its t=0 condition. May be called more than once.</summary>
    void Initialize();

    /// <summary>Advances the model by one fixed step of <paramref name="dt"/> seconds.</summary>
    void Step(double dt);

    /// <summary>Reads a port value.</summary>
    double GetPort(int portIndex);

    /// <summary>Writes a port value.</summary>
    void SetPort(int portIndex, double value);
}

/// <summary>Convenience helpers over <see cref="IModel"/>.</summary>
public static class ModelExtensions
{
    /// <summary>Returns the index of a named port, or -1 when the model has no such port.</summary>
    public static int IndexOfPort(this IModel model, string portName)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(portName);

        IReadOnlyList<PortDescriptor> ports = model.Ports;
        for (int i = 0; i < ports.Count; i++)
        {
            if (string.Equals(ports[i].Name, portName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Returns the index of a named port, or throws when it is absent.</summary>
    public static int RequirePort(this IModel model, string portName)
    {
        int index = model.IndexOfPort(portName);
        if (index < 0)
        {
            throw new ArgumentException(
                $"Model '{model.Name}' has no port named '{portName}'.", nameof(portName));
        }

        return index;
    }

    /// <summary>Reads a port by name.</summary>
    public static double GetPort(this IModel model, string portName)
        => model.GetPort(model.RequirePort(portName));

    /// <summary>Writes a port by name.</summary>
    public static void SetPort(this IModel model, string portName, double value)
        => model.SetPort(model.RequirePort(portName), value);
}
