using Sil.Core.Logging;
using Sil.Core.Models.Builtin;
using Sil.Core.Runtime;
using Sil.Core.Stimulus;
using Sil.Core.Systems;

namespace Sil.App.Simulation;

/// <summary>
/// Everything the shell needs to run one scenario: the wired system, its stimulus, its limit
/// monitor and the traces feeding the live chart.
/// </summary>
public sealed class SimulationScenario : IDisposable
{
    public required string Title { get; init; }

    public required string Description { get; init; }

    public required SilSystem System { get; init; }

    public required StimulusTask Stimulus { get; init; }

    public required LimitMonitor Monitor { get; init; }

    public required TraceRecorder Recorder { get; init; }

    /// <summary>Human-readable description of each stimulus binding, for the project tree.</summary>
    public required IReadOnlyList<string> StimulusSummary { get; init; }

    public void Dispose() => System.Dispose();
}

/// <summary>
/// Builds the scenario the application opens with. Everything here is constructed from the
/// runtime's own models and profiles and computed live — there is no canned data file and no
/// pre-recorded trace.
/// </summary>
public static class DemoSystemFactory
{
    /// <summary>Samples retained per trace for the live chart.</summary>
    public const int TraceCapacity = 2000;

    /// <summary>
    /// A lightly damped mass-spring-damper driven by a sine force, with an acceptance band on
    /// displacement that the resonant response deliberately breaks — so the shell shows a real
    /// PASS/FAIL transition rather than a static screen.
    /// </summary>
    public static SimulationScenario CreateDefault()
    {
        var builder = new SilSystemBuilder();
        builder.AddModel(new MassSpringDamperModel(
            "plant", mass: 1.0, damping: 0.25, stiffness: 4.0));

        builder.AddChannel("Force", "N", description: "Drive force applied to the mass");
        builder.AddChannel("Position", "m", description: "Mass displacement");
        builder.AddChannel("Velocity", "m/s", description: "Mass velocity");

        builder.Map("plant", "F", "Force");
        builder.Map("plant", "x", "Position");
        builder.Map("plant", "v", "Velocity");

        SilSystem system = builder.Build();

        // Drive close to the undamped natural frequency (wn = 2 rad/s ~= 0.318 Hz) so the
        // response grows into the limit band within a few seconds of run time.
        var drive = new SineProfile(amplitude: 1.0, frequencyHz: 0.318, offset: 0.0);
        var stimulus = new StimulusTask(system.Channels, [new StimulusBinding("Force", drive)]);

        var monitor = new LimitMonitor(system.Channels,
        [
            new ChannelLimit("Position", -1.0, 1.0),
            new ChannelLimit("Velocity", -3.0, 3.0),
        ]);

        var recorder = new TraceRecorder(
            system.Channels,
            [
                new ChannelTrace("Force", TraceCapacity),
                new ChannelTrace("Position", TraceCapacity),
                new ChannelTrace("Velocity", TraceCapacity),
            ],
            decimation: 5);

        return new SimulationScenario
        {
            Title = "Resonant mass-spring-damper",
            Description =
                "m = 1 kg, c = 0.25 N*s/m, k = 4 N/m driven at 0.318 Hz, near its 2 rad/s " +
                "natural frequency. Displacement is expected to stay within +/-1 m.",
            System = system,
            Stimulus = stimulus,
            Monitor = monitor,
            Recorder = recorder,
            StimulusSummary = ["Force = sine, amplitude 1 N, 0.318 Hz"],
        };
    }
}
