using Sil.Core.Numerics;
using Xunit;

namespace Sil.Core.Tests;

public class IntegratorTests
{
    /// <summary>dx/dt = -x.</summary>
    private static void Decay(double t, ReadOnlySpan<double> x, Span<double> dx)
    {
        for (int i = 0; i < x.Length; i++)
        {
            dx[i] = -x[i];
        }
    }

    [Fact]
    public void Euler_Decay_MatchesGoldenVector()
    {
        var integrator = new EulerIntegrator(1);
        double[] x = [1.0];

        for (int step = 0; step < 10; step++)
        {
            integrator.Step(Decay, step * 0.1, 0.1, x);
        }

        Assert.Equal(GoldenVectors.EulerDecay10Steps, x[0], GoldenVectors.Tolerance);
    }

    [Fact]
    public void Rk4_Decay_SingleStep_MatchesGoldenVector()
    {
        var integrator = new Rk4Integrator(1);
        double[] x = [1.0];

        integrator.Step(Decay, 0.0, 0.1, x);

        Assert.Equal(GoldenVectors.Rk4Decay1Step, x[0], GoldenVectors.Tolerance);
    }

    [Fact]
    public void Rk4_Decay_TenSteps_MatchesGoldenVector()
    {
        var integrator = new Rk4Integrator(1);
        double[] x = [1.0];

        for (int step = 0; step < 10; step++)
        {
            integrator.Step(Decay, step * 0.1, 0.1, x);
        }

        Assert.Equal(GoldenVectors.Rk4Decay10Steps, x[0], GoldenVectors.Tolerance);
    }

    [Fact]
    public void Rk4_Decay_TenSteps_IsWithin1e6OfAnalyticSolution()
    {
        var integrator = new Rk4Integrator(1);
        double[] x = [1.0];

        for (int step = 0; step < 10; step++)
        {
            integrator.Step(Decay, step * 0.1, 0.1, x);
        }

        Assert.True(
            Math.Abs(x[0] - GoldenVectors.AnalyticDecayAtT1) < 1e-6,
            $"RK4 result {x[0]} deviates from e^-1 by more than 1e-6.");
    }

    [Fact]
    public void Rk4_IsFourthOrder_ErrorShrinksBy16WhenStepHalves()
    {
        double ErrorFor(double dt)
        {
            var integrator = new Rk4Integrator(1);
            double[] x = [1.0];
            int steps = (int)Math.Round(1.0 / dt);
            for (int step = 0; step < steps; step++)
            {
                integrator.Step(Decay, step * dt, dt, x);
            }

            return Math.Abs(x[0] - GoldenVectors.AnalyticDecayAtT1);
        }

        double coarse = ErrorFor(0.1);
        double fine = ErrorFor(0.05);

        // Fourth order => roughly a factor of 2^4 = 16 improvement.
        Assert.InRange(coarse / fine, 12.0, 20.0);
    }

    [Fact]
    public void Integrators_HandleCoupledStateVectors()
    {
        // Harmonic oscillator: x'' = -x written as [x, v]' = [v, -x].
        // Energy x^2 + v^2 is conserved by the exact solution.
        static void Oscillator(double t, ReadOnlySpan<double> s, Span<double> ds)
        {
            ds[0] = s[1];
            ds[1] = -s[0];
        }

        var integrator = new Rk4Integrator(2);
        double[] state = [1.0, 0.0];

        const double dt = 0.01;
        const int steps = 628;
        for (int step = 0; step < steps; step++)
        {
            integrator.Step(Oscillator, step * dt, dt, state);
        }

        // Analytic solution from [1, 0] is [cos t, -sin t]; compare at the real end time.
        double endTime = steps * dt;
        Assert.Equal(Math.Cos(endTime), state[0], 1e-9);
        Assert.Equal(-Math.Sin(endTime), state[1], 1e-9);
    }

    [Fact]
    public void Integrators_AreDeterministic_AcrossRepeatedRuns()
    {
        static double[] Run(IntegratorKind kind)
        {
            IIntegrator integrator = Integrators.Create(kind, 2);
            double[] state = [1.0, -0.5];
            for (int step = 0; step < 137; step++)
            {
                integrator.Step(Decay, step * 0.013, 0.013, state);
            }

            return state;
        }

        foreach (IntegratorKind kind in new[] { IntegratorKind.Euler, IntegratorKind.Rk4 })
        {
            double[] first = Run(kind);
            double[] second = Run(kind);

            // Bit-for-bit identical, not merely close.
            Assert.Equal(BitConverter.DoubleToInt64Bits(first[0]), BitConverter.DoubleToInt64Bits(second[0]));
            Assert.Equal(BitConverter.DoubleToInt64Bits(first[1]), BitConverter.DoubleToInt64Bits(second[1]));
        }
    }

    [Fact]
    public void Step_RejectsMismatchedStateLength()
    {
        var integrator = new EulerIntegrator(2);
        double[] wrongSize = [1.0];

        Assert.Throws<ArgumentException>(() => integrator.Step(Decay, 0.0, 0.1, wrongSize));
    }

    [Fact]
    public void Create_RejectsUnknownKind()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Integrators.Create((IntegratorKind)99, 1));
    }
}
