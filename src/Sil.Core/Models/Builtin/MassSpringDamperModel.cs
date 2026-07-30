using Sil.Core.Numerics;

namespace Sil.Core.Models.Builtin;

/// <summary>
/// Second-order mechanical plant: <c>m*x'' + c*x' + k*x = F</c>.
/// State vector is <c>[x, v]</c>.
/// </summary>
public sealed class MassSpringDamperModel : ContinuousModel
{
    /// <summary>Input port index: applied force F.</summary>
    public const int PortForce = 0;

    /// <summary>Output port index: displacement x.</summary>
    public const int PortPosition = 1;

    /// <summary>Output port index: velocity v.</summary>
    public const int PortVelocity = 2;

    private static readonly PortDescriptor[] PortTable =
    [
        new(PortForce, "F", PortDirection.Input, "N"),
        new(PortPosition, "x", PortDirection.Output, "m"),
        new(PortVelocity, "v", PortDirection.Output, "m/s"),
    ];

    private readonly double _mass;
    private readonly double _damping;
    private readonly double _stiffness;
    private readonly double _initialPosition;
    private readonly double _initialVelocity;

    /// <param name="name">Instance name.</param>
    /// <param name="mass">Mass m in kg; must be positive.</param>
    /// <param name="damping">Viscous damping c in N*s/m; must not be negative.</param>
    /// <param name="stiffness">Spring rate k in N/m; must not be negative.</param>
    /// <param name="initialPosition">Displacement at t=0, in m.</param>
    /// <param name="initialVelocity">Velocity at t=0, in m/s.</param>
    /// <param name="integrator">Integration scheme.</param>
    public MassSpringDamperModel(
        string name = "msd",
        double mass = 1.0,
        double damping = 0.0,
        double stiffness = 1.0,
        double initialPosition = 0.0,
        double initialVelocity = 0.0,
        IntegratorKind integrator = IntegratorKind.Rk4)
        : base(name, PortTable, stateSize: 2, integrator)
    {
        if (!double.IsFinite(mass) || mass <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(mass), mass, "Mass must be finite and positive.");
        }

        if (!double.IsFinite(damping) || damping < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(damping), damping, "Damping must be finite and non-negative.");
        }

        if (!double.IsFinite(stiffness) || stiffness < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stiffness), stiffness, "Stiffness must be finite and non-negative.");
        }

        if (!double.IsFinite(initialPosition) || !double.IsFinite(initialVelocity))
        {
            throw new ArgumentOutOfRangeException(nameof(initialPosition), "Initial conditions must be finite.");
        }

        _mass = mass;
        _damping = damping;
        _stiffness = stiffness;
        _initialPosition = initialPosition;
        _initialVelocity = initialVelocity;
    }

    /// <summary>Mass m in kg.</summary>
    public double Mass => _mass;

    /// <summary>Viscous damping c in N*s/m.</summary>
    public double Damping => _damping;

    /// <summary>Spring rate k in N/m.</summary>
    public double Stiffness => _stiffness;

    /// <summary>Undamped natural frequency in rad/s.</summary>
    public double NaturalFrequency => Math.Sqrt(_stiffness / _mass);

    /// <summary>Damping ratio zeta. Zero when the spring rate is zero.</summary>
    public double DampingRatio
    {
        get
        {
            double denominator = 2.0 * Math.Sqrt(_stiffness * _mass);
            return denominator == 0.0 ? 0.0 : _damping / denominator;
        }
    }

    protected override void OnInitialize()
    {
        State[0] = _initialPosition;
        State[1] = _initialVelocity;
    }

    protected override void Derivatives(double t, ReadOnlySpan<double> state, Span<double> derivatives)
    {
        derivatives[0] = state[1];
        derivatives[1] = (Input(PortForce) - (_damping * state[1]) - (_stiffness * state[0])) / _mass;
    }

    protected override void UpdateOutputs(double t)
    {
        Output(PortPosition, State[0]);
        Output(PortVelocity, State[1]);
    }
}
