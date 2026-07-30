using Sil.Core.Numerics;

namespace Sil.Core.Models;

/// <summary>
/// Base class for models defined by an ODE. It owns the state vector and the integrator; a
/// subclass supplies the port table, the initial state and the derivatives.
/// </summary>
public abstract class ContinuousModel : ModelBase
{
    private readonly double[] _state;
    private readonly IIntegrator _integrator;
    private readonly StateDerivative _derivative;

    protected ContinuousModel(
        string name,
        IReadOnlyList<PortDescriptor> ports,
        int stateSize,
        IntegratorKind integrator = IntegratorKind.Rk4)
        : base(name, ports)
    {
        if (stateSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stateSize), stateSize, "State size must not be negative.");
        }

        _state = new double[stateSize];
        _integrator = Integrators.Create(integrator, stateSize);
        _derivative = Derivatives;
    }

    /// <summary>Integration scheme in use.</summary>
    public IntegratorKind Integrator => _integrator.Kind;

    /// <summary>The continuous state vector.</summary>
    protected Span<double> State => _state;

    /// <summary>Evaluates <c>dx/dt = f(t, x)</c> using the current input port values.</summary>
    protected abstract void Derivatives(double t, ReadOnlySpan<double> state, Span<double> derivatives);

    protected sealed override void OnStep(double t, double dt)
        => _integrator.Step(_derivative, t, dt, _state);

    protected sealed override void OnResetState() => Array.Clear(_state);
}
