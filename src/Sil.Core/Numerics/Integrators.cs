namespace Sil.Core.Numerics;

/// <summary>
/// Evaluates the derivative of a state vector: <c>derivative = f(t, state)</c>.
/// Implementations must be pure with respect to <paramref name="state"/> so that
/// repeated evaluation at the same point yields identical results (determinism rule).
/// </summary>
public delegate void StateDerivative(double t, ReadOnlySpan<double> state, Span<double> derivative);

/// <summary>Fixed-step integration schemes supported by the runtime.</summary>
public enum IntegratorKind
{
    /// <summary>Explicit forward Euler. First order.</summary>
    Euler = 0,

    /// <summary>Classical explicit Runge-Kutta. Fourth order.</summary>
    Rk4 = 1,
}

/// <summary>
/// A fixed-step integrator over a state vector of a known, constant size.
/// Working buffers are allocated once at construction: stepping never allocates.
/// </summary>
public interface IIntegrator
{
    IntegratorKind Kind { get; }

    /// <summary>Length of the state vector this instance was built for.</summary>
    int StateSize { get; }

    /// <summary>
    /// Advances <paramref name="state"/> in place from <paramref name="t"/> to <c>t + dt</c>.
    /// </summary>
    void Step(StateDerivative f, double t, double dt, Span<double> state);
}

/// <summary>Explicit forward Euler: <c>x[n+1] = x[n] + dt * f(t, x[n])</c>.</summary>
public sealed class EulerIntegrator : IIntegrator
{
    private readonly double[] _k;

    public EulerIntegrator(int stateSize)
    {
        if (stateSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stateSize), stateSize, "State size must not be negative.");
        }

        StateSize = stateSize;
        _k = new double[stateSize];
    }

    public IntegratorKind Kind => IntegratorKind.Euler;

    public int StateSize { get; }

    public void Step(StateDerivative f, double t, double dt, Span<double> state)
    {
        ArgumentNullException.ThrowIfNull(f);
        IntegratorGuard.CheckState(state, StateSize);

        Span<double> k = _k;
        f(t, state, k);
        for (int i = 0; i < state.Length; i++)
        {
            state[i] += dt * k[i];
        }
    }
}

/// <summary>Classical fourth-order Runge-Kutta.</summary>
public sealed class Rk4Integrator : IIntegrator
{
    private readonly double[] _k1;
    private readonly double[] _k2;
    private readonly double[] _k3;
    private readonly double[] _k4;
    private readonly double[] _tmp;

    public Rk4Integrator(int stateSize)
    {
        if (stateSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stateSize), stateSize, "State size must not be negative.");
        }

        StateSize = stateSize;
        _k1 = new double[stateSize];
        _k2 = new double[stateSize];
        _k3 = new double[stateSize];
        _k4 = new double[stateSize];
        _tmp = new double[stateSize];
    }

    public IntegratorKind Kind => IntegratorKind.Rk4;

    public int StateSize { get; }

    public void Step(StateDerivative f, double t, double dt, Span<double> state)
    {
        ArgumentNullException.ThrowIfNull(f);
        IntegratorGuard.CheckState(state, StateSize);

        Span<double> k1 = _k1;
        Span<double> k2 = _k2;
        Span<double> k3 = _k3;
        Span<double> k4 = _k4;
        Span<double> tmp = _tmp;

        double half = dt * 0.5;

        f(t, state, k1);

        for (int i = 0; i < state.Length; i++)
        {
            tmp[i] = state[i] + half * k1[i];
        }

        f(t + half, tmp, k2);

        for (int i = 0; i < state.Length; i++)
        {
            tmp[i] = state[i] + half * k2[i];
        }

        f(t + half, tmp, k3);

        for (int i = 0; i < state.Length; i++)
        {
            tmp[i] = state[i] + dt * k3[i];
        }

        f(t + dt, tmp, k4);

        double sixth = dt / 6.0;
        for (int i = 0; i < state.Length; i++)
        {
            state[i] += sixth * (k1[i] + 2.0 * k2[i] + 2.0 * k3[i] + k4[i]);
        }
    }
}

/// <summary>Creates integrator instances by kind.</summary>
public static class Integrators
{
    public static IIntegrator Create(IntegratorKind kind, int stateSize) => kind switch
    {
        IntegratorKind.Euler => new EulerIntegrator(stateSize),
        IntegratorKind.Rk4 => new Rk4Integrator(stateSize),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown integrator kind."),
    };
}

internal static class IntegratorGuard
{
    internal static void CheckState(Span<double> state, int expected)
    {
        if (state.Length != expected)
        {
            throw new ArgumentException(
                $"State vector length {state.Length} does not match integrator state size {expected}.",
                nameof(state));
        }
    }
}
