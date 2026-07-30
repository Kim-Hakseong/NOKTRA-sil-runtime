namespace Sil.Core.Tests;

/// <summary>
/// The verified reference values from DESIGN.md. These are frozen: they may be read by any test
/// but must never be edited or deleted.
/// </summary>
public static class GoldenVectors
{
    /// <summary>Tolerance used for all numeric golden comparisons.</summary>
    public const double Tolerance = 1e-9;

    // Euler, dx/dt = -x, x0 = 1, dt = 0.1, 10 steps.
    public const double EulerDecay10Steps = 0.3486784401;

    // RK4, dx/dt = -x, x0 = 1, dt = 0.1.
    public const double Rk4Decay1Step = 0.9048375000;
    public const double Rk4Decay10Steps = 0.3678797744;

    /// <summary>Analytic solution of dx/dt = -x at t = 1 with x0 = 1.</summary>
    public const double AnalyticDecayAtT1 = 0.3678794412;

    // Channel mapping: raw port value 10 with scale a = 2, b = 1 -> engineering value 21.
    public const double MappingRawValue = 10.0;
    public const double MappingScaleA = 2.0;
    public const double MappingScaleB = 1.0;
    public const double MappingExpected = 21.0;

    // Stimulus: step(t0 = 1.0, 0 -> 5).
    public const double StepStimulusT0 = 1.0;
    public const double StepStimulusBefore = 0.0;
    public const double StepStimulusAfter = 5.0;

    // Stimulus: ramp 0 -> 10 over 2 s, sampled at t = 1.0.
    public const double RampStimulusDuration = 2.0;
    public const double RampStimulusTo = 10.0;
    public const double RampStimulusAtT1 = 5.0;

    // Limit judgement: channel sequence [4.9, 5.6] against limits [4.5, 5.5].
    public static readonly double[] LimitSequence = [4.9, 5.6];
    public const double LimitLow = 4.5;
    public const double LimitHigh = 5.5;
    public const int LimitExpectedViolationCount = 1;
    public const long LimitExpectedViolationStepIndex = 1; // second sample
}
