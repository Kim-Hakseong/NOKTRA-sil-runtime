using System.Globalization;

namespace Sil.Core.Channels;

/// <summary>
/// Affine conversion between a model-side raw value and a system-side engineering value:
/// <c>engineering = a * raw + b</c>.
/// </summary>
/// <param name="A">Slope. Must be finite and non-zero so the conversion stays invertible.</param>
/// <param name="B">Offset. Must be finite.</param>
public readonly record struct LinearScale(double A, double B)
{
    /// <summary>The pass-through conversion <c>a = 1, b = 0</c>.</summary>
    public static LinearScale Identity => new(1.0, 0.0);

    /// <summary>Creates a validated scale.</summary>
    public static LinearScale Create(double a, double b)
    {
        if (!double.IsFinite(a) || a == 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(a), a, "Scale slope must be finite and non-zero.");
        }

        if (!double.IsFinite(b))
        {
            throw new ArgumentOutOfRangeException(nameof(b), b, "Scale offset must be finite.");
        }

        return new LinearScale(a, b);
    }

    /// <summary>True when this scale is invertible and usable.</summary>
    public bool IsValid => double.IsFinite(A) && A != 0.0 && double.IsFinite(B);

    /// <summary>Converts a model-side raw value to an engineering value.</summary>
    public double ToEngineering(double raw) => (A * raw) + B;

    /// <summary>Converts an engineering value back to a model-side raw value.</summary>
    public double ToRaw(double engineering) => (engineering - B) / A;

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"y = {A} * x + {B}");
}
