namespace Sil.Core.Models.Builtin;

/// <summary>
/// Discrete PI controller with conditional-integration anti-windup:
/// <code>
/// e      = setpoint - measurement
/// I(k)   = I(k-1) + Ki * e * dt      (held when the output is saturated into the error)
/// u      = Kp * e + I(k),  clamped to [OutputMinimum, OutputMaximum]
/// </code>
/// </summary>
/// <remarks>
/// The arithmetic is written in the same operation order as
/// <c>src/Sil.NativeSpec/src/sil_pi_controller.c</c> so the managed and compiled controllers
/// agree bit-for-bit. That is what makes the native one testable: any divergence is a real
/// difference in the C, not a rounding artefact of comparing two different formulations.
/// </remarks>
public sealed class PiControllerModel : DiscreteModel
{
    /// <summary>Input port index: commanded value.</summary>
    public const int PortSetpoint = 0;

    /// <summary>Input port index: measured value fed back from the plant.</summary>
    public const int PortMeasurement = 1;

    /// <summary>Output port index: control command.</summary>
    public const int PortCommand = 2;

    /// <summary>Output port index: integrator state.</summary>
    public const int PortIntegral = 3;

    private static readonly PortDescriptor[] PortTable =
    [
        new(PortSetpoint, "setpoint", PortDirection.Input, string.Empty),
        new(PortMeasurement, "measurement", PortDirection.Input, string.Empty),
        new(PortCommand, "u", PortDirection.Output, string.Empty),
        new(PortIntegral, "integral", PortDirection.Output, string.Empty),
    ];

    private readonly double _kp;
    private readonly double _ki;
    private readonly double _outputMinimum;
    private readonly double _outputMaximum;

    private double _integral;
    private double _command;

    /// <param name="name">Instance name.</param>
    /// <param name="proportionalGain">Kp.</param>
    /// <param name="integralGain">Ki. Zero gives a pure proportional controller.</param>
    /// <param name="outputMinimum">Lower output clamp.</param>
    /// <param name="outputMaximum">Upper output clamp.</param>
    public PiControllerModel(
        string name = "controller",
        double proportionalGain = 1.0,
        double integralGain = 0.0,
        double outputMinimum = double.NegativeInfinity,
        double outputMaximum = double.PositiveInfinity)
        : base(name, PortTable)
    {
        if (!double.IsFinite(proportionalGain))
        {
            throw new ArgumentOutOfRangeException(
                nameof(proportionalGain), proportionalGain, "Proportional gain must be finite.");
        }

        if (!double.IsFinite(integralGain))
        {
            throw new ArgumentOutOfRangeException(
                nameof(integralGain), integralGain, "Integral gain must be finite.");
        }

        if (double.IsNaN(outputMinimum) || double.IsNaN(outputMaximum))
        {
            throw new ArgumentOutOfRangeException(nameof(outputMinimum), "Output limits must not be NaN.");
        }

        if (outputMinimum > outputMaximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputMinimum), outputMinimum,
                $"Output minimum must not exceed the maximum ({outputMaximum}).");
        }

        _kp = proportionalGain;
        _ki = integralGain;
        _outputMinimum = outputMinimum;
        _outputMaximum = outputMaximum;
    }

    /// <summary>Proportional gain Kp.</summary>
    public double ProportionalGain => _kp;

    /// <summary>Integral gain Ki.</summary>
    public double IntegralGain => _ki;

    /// <summary>Lower output clamp.</summary>
    public double OutputMinimum => _outputMinimum;

    /// <summary>Upper output clamp.</summary>
    public double OutputMaximum => _outputMaximum;

    protected override void OnInitialize()
    {
        _integral = 0.0;
        _command = 0.0;
    }

    protected override void Update(double t, double dt)
    {
        double error = Input(PortSetpoint) - Input(PortMeasurement);
        double candidate = _integral + (_ki * error * dt);
        double command = (_kp * error) + candidate;

        if (command > _outputMaximum)
        {
            command = _outputMaximum;
            if (error > 0.0)
            {
                candidate = _integral;   // do not wind up further into the limit
            }
        }
        else if (command < _outputMinimum)
        {
            command = _outputMinimum;
            if (error < 0.0)
            {
                candidate = _integral;
            }
        }

        _integral = candidate;
        _command = command;
    }

    protected override void UpdateOutputs(double t)
    {
        Output(PortCommand, _command);
        Output(PortIntegral, _integral);
    }
}
