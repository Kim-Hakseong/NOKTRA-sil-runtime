namespace Sil.Core.Timing;

/// <summary>
/// The virtual clock that defines simulation time. Time is always derived from the integer
/// step index (<c>time = stepIndex * dt</c>) rather than accumulated by addition, so it never
/// drifts and a given step index always maps to exactly the same double value.
/// </summary>
public sealed class SimClock
{
    public SimClock(SimRate rate)
    {
        Rate = rate;
    }

    /// <summary>Configured fixed rate.</summary>
    public SimRate Rate { get; }

    /// <summary>Step size in seconds.</summary>
    public double Dt => Rate.Dt;

    /// <summary>Number of completed steps since the last reset.</summary>
    public long StepIndex { get; private set; }

    /// <summary>Current simulation time in seconds.</summary>
    public double Time => TimeAt(StepIndex);

    /// <summary>Simulation time of an arbitrary step index.</summary>
    public double TimeAt(long stepIndex) => stepIndex * Dt;

    /// <summary>Advances the clock by exactly one step.</summary>
    public void Advance()
    {
        StepIndex = checked(StepIndex + 1);
    }

    /// <summary>Rewinds the clock to <c>t = 0</c>.</summary>
    public void Reset()
    {
        StepIndex = 0;
    }
}
