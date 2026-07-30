using Sil.Core.Numerics;

namespace Sil.Core.Models.Builtin;

/// <summary>
/// First-order lag: <c>dx/dt = (K*u - x) / tau</c>.
/// The reference plant used by the closed-loop verification in DESIGN.md.
/// </summary>
public sealed class FirstOrderLagModel : ContinuousModel
{
    /// <summary>Input port index: command <c>u</c>.</summary>
    public const int PortU = 0;

    /// <summary>Output port index: state <c>x</c>.</summary>
    public const int PortX = 1;

    private static readonly PortDescriptor[] PortTable =
    [
        new(PortU, "u", PortDirection.Input, string.Empty),
        new(PortX, "x", PortDirection.Output, string.Empty),
    ];

    private readonly double _tau;
    private readonly double _gain;
    private readonly double _initialValue;

    /// <param name="name">Instance name.</param>
    /// <param name="timeConstant">Time constant tau in seconds; must be positive.</param>
    /// <param name="gain">Steady-state gain K.</param>
    /// <param name="initialValue">Value of x at t=0.</param>
    /// <param name="integrator">Integration scheme.</param>
    public FirstOrderLagModel(
        string name = "plant",
        double timeConstant = 1.0,
        double gain = 1.0,
        double initialValue = 0.0,
        IntegratorKind integrator = IntegratorKind.Rk4)
        : base(name, PortTable, stateSize: 1, integrator)
    {
        if (!double.IsFinite(timeConstant) || timeConstant <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeConstant), timeConstant, "Time constant must be finite and positive.");
        }

        if (!double.IsFinite(gain))
        {
            throw new ArgumentOutOfRangeException(nameof(gain), gain, "Gain must be finite.");
        }

        if (!double.IsFinite(initialValue))
        {
            throw new ArgumentOutOfRangeException(nameof(initialValue), initialValue, "Initial value must be finite.");
        }

        _tau = timeConstant;
        _gain = gain;
        _initialValue = initialValue;
    }

    /// <summary>Time constant tau in seconds.</summary>
    public double TimeConstant => _tau;

    /// <summary>Steady-state gain K.</summary>
    public double Gain => _gain;

    /// <summary>Value of x at t=0.</summary>
    public double InitialValue => _initialValue;

    protected override void OnInitialize()
    {
        State[0] = _initialValue;
    }

    protected override void Derivatives(double t, ReadOnlySpan<double> state, Span<double> derivatives)
    {
        derivatives[0] = ((_gain * Input(PortU)) - state[0]) / _tau;
    }

    protected override void UpdateOutputs(double t)
    {
        Output(PortX, State[0]);
    }
}
