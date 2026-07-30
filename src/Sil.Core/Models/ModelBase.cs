namespace Sil.Core.Models;

/// <summary>
/// Shared implementation for managed models: the validated port table, port value storage and
/// the local step/time bookkeeping. Subclasses decide what a step actually does.
/// </summary>
public abstract class ModelBase : IModel
{
    private readonly double[] _inputs;
    private readonly double[] _outputs;
    private readonly PortDescriptor[] _ports;

    private long _stepCount;
    private double _lastDt;

    protected ModelBase(string name, IReadOnlyList<PortDescriptor> ports)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(ports);

        Name = name;
        _ports = ValidatePorts(ports);
        _inputs = new double[_ports.Length];
        _outputs = new double[_ports.Length];
    }

    public string Name { get; }

    public IReadOnlyList<PortDescriptor> Ports => _ports;

    /// <summary>
    /// Local model time in seconds, derived from the completed step count rather than accumulated,
    /// so a model stepped standalone and the same model driven by the engine see identical values.
    /// </summary>
    public double Time => _stepCount * _lastDt;

    /// <summary>Number of completed steps since the last initialize.</summary>
    protected long StepCount => _stepCount;

    public void Initialize()
    {
        Array.Clear(_inputs);
        Array.Clear(_outputs);
        _stepCount = 0;
        _lastDt = 0.0;

        OnResetState();
        OnInitialize();
        UpdateOutputs(0.0);
    }

    public void Step(double dt)
    {
        if (!double.IsFinite(dt) || dt <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(dt), dt, "Step size must be finite and positive.");
        }

        if (_lastDt != 0.0 && _lastDt != dt)
        {
            // A changed dt invalidates the stepCount * dt time basis; rebase so time stays monotonic.
            _stepCount = (long)Math.Round(Time / dt);
        }

        _lastDt = dt;

        OnStep(Time, dt);
        _stepCount++;

        UpdateOutputs(Time);
    }

    public double GetPort(int portIndex)
    {
        PortDescriptor port = PortAt(portIndex);
        return port.Direction == PortDirection.Input ? _inputs[portIndex] : _outputs[portIndex];
    }

    public void SetPort(int portIndex, double value)
    {
        PortDescriptor port = PortAt(portIndex);
        if (port.Direction == PortDirection.Input)
        {
            _inputs[portIndex] = value;
        }
        else
        {
            _outputs[portIndex] = value;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Reads the current value of an input port from inside the model.</summary>
    protected double Input(int portIndex) => _inputs[portIndex];

    /// <summary>Writes an output port from inside the model.</summary>
    protected void Output(int portIndex, double value) => _outputs[portIndex] = value;

    /// <summary>
    /// Clears whatever internal state the base kind owns, before the subclass applies its initial
    /// conditions. Kept separate from <see cref="OnInitialize"/> so a subclass overriding that
    /// does not have to remember to chain to the base to get a clean slate.
    /// </summary>
    protected virtual void OnResetState()
    {
    }

    /// <summary>Sets the t=0 state. Ports and base state are already cleared when this is called.</summary>
    protected abstract void OnInitialize();

    /// <summary>Advances the model's own state by <paramref name="dt"/>, starting at time <paramref name="t"/>.</summary>
    protected abstract void OnStep(double t, double dt);

    /// <summary>Publishes output ports from the current state. Called after init and each step.</summary>
    protected abstract void UpdateOutputs(double t);

    protected virtual void Dispose(bool disposing)
    {
    }

    private PortDescriptor PortAt(int portIndex)
    {
        if ((uint)portIndex >= (uint)_ports.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(portIndex), portIndex, $"Model '{Name}' has {_ports.Length} ports.");
        }

        return _ports[portIndex];
    }

    private static PortDescriptor[] ValidatePorts(IReadOnlyList<PortDescriptor> ports)
    {
        var result = new PortDescriptor[ports.Count];
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < ports.Count; i++)
        {
            PortDescriptor port = ports[i];
            if (port.Index != i)
            {
                throw new ArgumentException(
                    $"Port '{port.Name}' declares index {port.Index} but sits at position {i}.", nameof(ports));
            }

            if (string.IsNullOrWhiteSpace(port.Name))
            {
                throw new ArgumentException($"Port at index {i} has no name.", nameof(ports));
            }

            if (!names.Add(port.Name))
            {
                throw new ArgumentException($"Duplicate port name '{port.Name}'.", nameof(ports));
            }

            result[i] = port;
        }

        return result;
    }
}

/// <summary>
/// Base class for models whose state advances by a difference equation rather than an ODE —
/// discrete controllers, filters, state machines. The step size is supplied so a scenario can be
/// re-rated without editing the model.
/// </summary>
public abstract class DiscreteModel(string name, IReadOnlyList<PortDescriptor> ports)
    : ModelBase(name, ports)
{
    /// <summary>Computes one discrete update using the current input port values.</summary>
    protected abstract void Update(double t, double dt);

    protected sealed override void OnStep(double t, double dt) => Update(t, dt);
}
