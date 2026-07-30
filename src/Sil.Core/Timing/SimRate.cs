using System.Globalization;

namespace Sil.Core.Timing;

/// <summary>
/// A validated fixed execution rate. The runtime is limited to 1..1000 Hz (PRD S-01).
/// </summary>
public readonly struct SimRate : IEquatable<SimRate>
{
    /// <summary>Lowest permitted rate in hertz.</summary>
    public const int MinHz = 1;

    /// <summary>Highest permitted rate in hertz.</summary>
    public const int MaxHz = 1000;

    private SimRate(int hz)
    {
        Hz = hz;
    }

    /// <summary>Execution frequency in hertz.</summary>
    public int Hz { get; }

    /// <summary>Nominal step size in seconds (<c>1 / Hz</c>).</summary>
    public double Dt => Hz == 0 ? 0.0 : 1.0 / Hz;

    public static SimRate FromHz(int hz)
    {
        if (hz < MinHz || hz > MaxHz)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hz), hz, $"Rate must be between {MinHz} and {MaxHz} Hz.");
        }

        return new SimRate(hz);
    }

    public static bool TryFromHz(int hz, out SimRate rate)
    {
        if (hz < MinHz || hz > MaxHz)
        {
            rate = default;
            return false;
        }

        rate = new SimRate(hz);
        return true;
    }

    public bool Equals(SimRate other) => Hz == other.Hz;

    public override bool Equals(object? obj) => obj is SimRate other && Equals(other);

    public override int GetHashCode() => Hz;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Hz} Hz");

    public static bool operator ==(SimRate left, SimRate right) => left.Equals(right);

    public static bool operator !=(SimRate left, SimRate right) => !left.Equals(right);
}
