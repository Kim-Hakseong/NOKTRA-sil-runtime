namespace Sil.Core.Stimulus;

/// <summary>
/// A stimulus profile is a pure function of simulation time. Purity is what makes injection
/// replayable: sampling the same profile at the same t always yields the same value, so a run is
/// reproducible no matter where it is paused, stepped or restarted.
/// </summary>
public interface IStimulusProfile
{
    /// <summary>Value of the profile at simulation time <paramref name="t"/> seconds.</summary>
    double ValueAt(double t);
}

/// <summary>A profile that holds one value for all time.</summary>
public sealed class ConstantProfile : IStimulusProfile
{
    public ConstantProfile(double value)
    {
        StimulusGuard.Finite(value, nameof(value));
        Value = value;
    }

    public double Value { get; }

    public double ValueAt(double t) => Value;
}

/// <summary>
/// A step: <c>before</c> until <c>StartTime</c>, then <c>after</c>. The transition is inclusive
/// of the start time, so <c>t == StartTime</c> already reads the stepped value.
/// </summary>
public sealed class StepProfile : IStimulusProfile
{
    public StepProfile(double startTime, double before, double after)
    {
        StimulusGuard.Finite(startTime, nameof(startTime));
        StimulusGuard.Finite(before, nameof(before));
        StimulusGuard.Finite(after, nameof(after));

        StartTime = startTime;
        Before = before;
        After = after;
    }

    public double StartTime { get; }

    public double Before { get; }

    public double After { get; }

    public double ValueAt(double t) => t >= StartTime ? After : Before;
}

/// <summary>
/// A linear ramp from <c>From</c> to <c>To</c> over <c>Duration</c> seconds, starting at
/// <c>StartTime</c>. It holds <c>From</c> before the start and <c>To</c> after the end.
/// </summary>
public sealed class RampProfile : IStimulusProfile
{
    public RampProfile(double startTime, double duration, double from, double to)
    {
        StimulusGuard.Finite(startTime, nameof(startTime));
        StimulusGuard.Finite(from, nameof(from));
        StimulusGuard.Finite(to, nameof(to));

        if (!double.IsFinite(duration) || duration <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Duration must be finite and positive.");
        }

        StartTime = startTime;
        Duration = duration;
        From = from;
        To = to;
    }

    public double StartTime { get; }

    public double Duration { get; }

    public double From { get; }

    public double To { get; }

    /// <summary>Time at which the ramp reaches <see cref="To"/>.</summary>
    public double EndTime => StartTime + Duration;

    public double ValueAt(double t)
    {
        if (t <= StartTime)
        {
            return From;
        }

        if (t >= EndTime)
        {
            return To;
        }

        double fraction = (t - StartTime) / Duration;
        return From + (fraction * (To - From));
    }
}

/// <summary>
/// A sine: <c>Offset + Amplitude * sin(2*pi*Frequency*(t - StartTime) + Phase)</c>.
/// Holds <c>Offset</c> before <c>StartTime</c>.
/// </summary>
public sealed class SineProfile : IStimulusProfile
{
    public SineProfile(
        double amplitude,
        double frequencyHz,
        double phaseRadians = 0.0,
        double offset = 0.0,
        double startTime = 0.0)
    {
        StimulusGuard.Finite(amplitude, nameof(amplitude));
        StimulusGuard.Finite(phaseRadians, nameof(phaseRadians));
        StimulusGuard.Finite(offset, nameof(offset));
        StimulusGuard.Finite(startTime, nameof(startTime));

        if (!double.IsFinite(frequencyHz) || frequencyHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frequencyHz), frequencyHz, "Frequency must be finite and positive.");
        }

        Amplitude = amplitude;
        FrequencyHz = frequencyHz;
        PhaseRadians = phaseRadians;
        Offset = offset;
        StartTime = startTime;
    }

    public double Amplitude { get; }

    public double FrequencyHz { get; }

    public double PhaseRadians { get; }

    public double Offset { get; }

    public double StartTime { get; }

    public double ValueAt(double t)
    {
        if (t < StartTime)
        {
            return Offset;
        }

        return Offset + (Amplitude * Math.Sin((2.0 * Math.PI * FrequencyHz * (t - StartTime)) + PhaseRadians));
    }
}

internal static class StimulusGuard
{
    internal static void Finite(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(name, value, "Value must be finite.");
        }
    }
}
