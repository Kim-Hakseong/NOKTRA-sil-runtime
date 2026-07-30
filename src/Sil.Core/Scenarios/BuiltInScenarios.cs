namespace Sil.Core.Scenarios;

/// <summary>
/// Scenario documents the runtime ships with. They are constructed in code rather than shipped as
/// data files, and every number in them is a stated model parameter — nothing is a recorded
/// sample or a canned result.
/// </summary>
public static class BuiltInScenarios
{
    /// <summary>Channel carrying the commanded value.</summary>
    public const string SetpointChannel = "Setpoint";

    /// <summary>Channel carrying the plant's measured output.</summary>
    public const string MeasurementChannel = "Measurement";

    /// <summary>Channel carrying the controller's command to the plant.</summary>
    public const string CommandChannel = "Command";

    /// <summary>Channel carrying the controller's integrator state.</summary>
    public const string IntegralChannel = "Integral";

    /// <summary>Name given to the plant instance.</summary>
    public const string PlantName = "plant";

    /// <summary>Name given to the controller instance.</summary>
    public const string ControllerName = "controller";

    /// <summary>
    /// The closed loop from DESIGN.md: a first-order plant <c>dx/dt = (K*u - x) / tau</c> driven
    /// by a PI controller, with the setpoint stepped at <paramref name="stepTime"/>.
    /// </summary>
    /// <remarks>
    /// With any non-zero integral gain the analytic steady state is <c>x = setpoint</c> exactly,
    /// whatever the gains, because the integrator can only stop moving when the error is zero.
    /// With <paramref name="integralGain"/> at zero the loop is proportional-only and settles at
    /// <c>Kp*K/(1 + Kp*K) * setpoint</c>, leaving a known, non-zero steady-state error. Both are
    /// closed forms the runtime is checked against.
    /// </remarks>
    /// <param name="controllerLibraryPath">
    /// When given, the controller is the compiled C model at this path instead of the managed
    /// <c>PiController</c> — the SIL case, where a model plant is driven by real compiled control
    /// code. The C reference fixes Kp = 2 and Ki = 5, so the gain arguments are ignored.
    /// </param>
    /// <param name="timeConstant">Plant time constant tau, in seconds.</param>
    /// <param name="plantGain">Plant steady-state gain K.</param>
    /// <param name="proportionalGain">Controller Kp. Ignored for the native controller.</param>
    /// <param name="integralGain">Controller Ki. Ignored for the native controller.</param>
    /// <param name="setpoint">Commanded value after the step.</param>
    /// <param name="stepTime">Time of the setpoint step, in seconds.</param>
    /// <param name="rateHz">Execution rate.</param>
    /// <param name="endTime">Run duration, in seconds.</param>
    /// <param name="outputLimit">Symmetric controller output clamp.</param>
    public static ScenarioDefinition ClosedLoop(
        string? controllerLibraryPath = null,
        double timeConstant = 0.5,
        double plantGain = 1.0,
        double proportionalGain = 2.0,
        double integralGain = 5.0,
        double setpoint = 1.0,
        double stepTime = 0.0,
        int rateHz = 1000,
        double endTime = 10.0,
        double outputLimit = 50.0)
    {
        ModelDefinition controller = controllerLibraryPath is null
            ? new ModelDefinition(ControllerName, "PiController", Parameters: new()
            {
                ["proportionalGain"] = proportionalGain,
                ["integralGain"] = integralGain,
                ["outputMinimum"] = -outputLimit,
                ["outputMaximum"] = outputLimit,
            })
            : new ModelDefinition(ControllerName, "Native", LibraryPath: controllerLibraryPath);

        return new ScenarioDefinition(
            Name: controllerLibraryPath is null
                ? "closed-loop-pi-managed"
                : "closed-loop-pi-native",
            Description:
                "First-order plant dx/dt = (K*u - x)/tau closed around a PI controller. " +
                "Steady state is compared against the analytic solution.",
            RateHz: rateHz,
            Models:
            [
                new ModelDefinition(PlantName, "FirstOrderLag", "Rk4", new()
                {
                    ["timeConstant"] = timeConstant,
                    ["gain"] = plantGain,
                    ["initialValue"] = 0.0,
                }),
                controller,
            ],
            Channels:
            [
                new ChannelDeclaration(SetpointChannel, "eu", 0.0, "Commanded value"),
                new ChannelDeclaration(MeasurementChannel, "eu", 0.0, "Plant output"),
                new ChannelDeclaration(CommandChannel, "eu", 0.0, "Controller output"),
                new ChannelDeclaration(IntegralChannel, "eu", 0.0, "Controller integrator state"),
            ],
            Mappings:
            [
                new MappingDeclaration(ControllerName, "setpoint", SetpointChannel),
                new MappingDeclaration(PlantName, "x", MeasurementChannel),
                new MappingDeclaration(ControllerName, "u", CommandChannel),
                new MappingDeclaration(ControllerName, "integral", IntegralChannel),
            ],
            Links:
            [
                new LinkDeclaration(ControllerName, "u", PlantName, "u"),
                new LinkDeclaration(PlantName, "x", ControllerName, "measurement"),
            ],
            Stimulus:
            [
                new StimulusDeclaration(SetpointChannel, "Step", new()
                {
                    ["startTime"] = stepTime,
                    ["before"] = 0.0,
                    ["after"] = setpoint,
                }),
            ],
            Run: new RunSettings(EndTime: endTime, LogDecimation: 10));
    }

    /// <summary>
    /// Analytic steady-state output of <see cref="ClosedLoop"/>.
    /// </summary>
    /// <remarks>
    /// With integral action the loop has zero steady-state error, so the plant settles exactly on
    /// the setpoint. Proportional-only leaves the classic <c>1/(1 + loop gain)</c> offset.
    /// </remarks>
    public static double AnalyticSteadyState(
        double setpoint, double plantGain, double proportionalGain, double integralGain)
    {
        if (integralGain != 0.0)
        {
            return setpoint;
        }

        double loopGain = proportionalGain * plantGain;
        return loopGain / (1.0 + loopGain) * setpoint;
    }

    /// <summary>Analytic steady-state error of <see cref="ClosedLoop"/>.</summary>
    public static double AnalyticSteadyStateError(
        double setpoint, double plantGain, double proportionalGain, double integralGain)
        => setpoint - AnalyticSteadyState(setpoint, plantGain, proportionalGain, integralGain);
}
