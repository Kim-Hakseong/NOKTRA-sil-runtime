using Sil.Core.Channels;
using Sil.Core.Engine;

namespace Sil.Core.Logging;

/// <summary>Which side of its band a channel left.</summary>
public enum LimitViolationKind
{
    /// <summary>The value fell below the lower limit.</summary>
    Low = 0,

    /// <summary>The value rose above the upper limit.</summary>
    High = 1,
}

/// <summary>The verdict of a scenario run.</summary>
public enum ScenarioVerdict
{
    /// <summary>No channel left its band.</summary>
    Pass = 0,

    /// <summary>At least one channel left its band.</summary>
    Fail = 1,
}

/// <summary>
/// An inclusive acceptance band for one channel: a value is in tolerance when
/// <c>Low &lt;= value &lt;= High</c>.
/// </summary>
/// <param name="ChannelName">Channel to watch.</param>
/// <param name="Low">Lower limit, inclusive.</param>
/// <param name="High">Upper limit, inclusive.</param>
public sealed record ChannelLimit(string ChannelName, double Low, double High)
{
    /// <summary>Validates the band.</summary>
    public void Validate()
    {
        if (!double.IsFinite(Low) || !double.IsFinite(High))
        {
            throw new ArgumentException($"Limits for '{ChannelName}' must be finite.");
        }

        if (Low > High)
        {
            throw new ArgumentException(
                $"Limits for '{ChannelName}' are inverted: low {Low} is above high {High}.");
        }
    }

    /// <summary>Returns the side violated, or null when the value is in tolerance.</summary>
    public LimitViolationKind? Check(double value)
    {
        if (value < Low)
        {
            return LimitViolationKind.Low;
        }

        if (value > High)
        {
            return LimitViolationKind.High;
        }

        return null;
    }
}

/// <summary>
/// One continuous excursion outside a channel's band.
/// </summary>
/// <remarks>
/// Excursions are recorded as events, not per sample: a two-second excursion at 1 kHz is one
/// violation with a duration, not two thousand. A channel that recovers and leaves the band
/// again produces a second event.
/// </remarks>
/// <param name="ChannelName">Channel that left its band.</param>
/// <param name="ChannelIndex">Index of that channel.</param>
/// <param name="Kind">Which side was crossed.</param>
/// <param name="Limit">The limit value that was crossed.</param>
/// <param name="FirstStepIndex">Cycle at which the excursion started.</param>
/// <param name="FirstTime">Simulation time at which the excursion started, in seconds.</param>
/// <param name="LastStepIndex">Last cycle of the excursion.</param>
/// <param name="LastTime">Simulation time of the last offending sample, in seconds.</param>
/// <param name="PeakValue">Value furthest outside the band during the excursion.</param>
/// <param name="SampleCount">Number of consecutive offending samples.</param>
public sealed record LimitViolation(
    string ChannelName,
    int ChannelIndex,
    LimitViolationKind Kind,
    double Limit,
    long FirstStepIndex,
    double FirstTime,
    long LastStepIndex,
    double LastTime,
    double PeakValue,
    long SampleCount)
{
    /// <summary>How far the peak value sat outside the limit.</summary>
    public double PeakExcess => Kind == LimitViolationKind.High ? PeakValue - Limit : Limit - PeakValue;

    public override string ToString()
        => $"{ChannelName} {Kind} at t={FirstTime:0.######}s (step {FirstStepIndex}), " +
           $"peak {PeakValue:0.######} vs limit {Limit:0.######}, {SampleCount} sample(s)";
}

/// <summary>Outcome of a monitored run.</summary>
/// <param name="Verdict">Pass or fail.</param>
/// <param name="Violations">Excursions in the order they started, up to the retention cap.</param>
/// <param name="SamplesEvaluated">Number of cycles the monitor examined.</param>
/// <param name="EndTime">Simulation time of the last examined cycle, in seconds.</param>
/// <param name="TotalViolationCount">
/// Total excursions detected, including any beyond the retention cap.
/// </param>
public sealed record ScenarioResult(
    ScenarioVerdict Verdict,
    IReadOnlyList<LimitViolation> Violations,
    long SamplesEvaluated,
    double EndTime,
    long TotalViolationCount)
{
    /// <summary>True when nothing left its band.</summary>
    public bool Passed => Verdict == ScenarioVerdict.Pass;

    /// <summary>Excursions that were detected but not retained because the cap was reached.</summary>
    public long DroppedViolationCount => Math.Max(0, TotalViolationCount - Violations.Count);
}

/// <summary>
/// Watches channels against acceptance bands during a run and produces the scenario verdict.
/// Runs as a recorder task, after the models have written their outputs to channels.
/// </summary>
public sealed class LimitMonitor : ISimTask
{
    private readonly ChannelTable _channels;
    private readonly ChannelLimit[] _limits;
    private readonly int[] _channelIndices;
    private readonly OpenExcursion?[] _open;
    private readonly List<LimitViolation> _violations = [];
    private readonly int _maxRetainedViolations;

    private long _samples;
    private double _endTime;
    private long _totalViolations;

    /// <summary>Number of excursions retained by default before only the count is kept.</summary>
    public const int DefaultMaxRetainedViolations = 1000;

    /// <param name="channels">Table to watch.</param>
    /// <param name="limits">Acceptance bands.</param>
    /// <param name="name">Task name.</param>
    /// <param name="maxRetainedViolations">
    /// Cap on retained excursion records. A long run of a marginal channel can chatter across a
    /// limit thousands of times a second; the verdict and the total count stay exact, but only
    /// this many records are kept so memory stays bounded.
    /// </param>
    public LimitMonitor(
        ChannelTable channels,
        IReadOnlyList<ChannelLimit> limits,
        string name = "limits",
        int maxRetainedViolations = DefaultMaxRetainedViolations)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (maxRetainedViolations < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxRetainedViolations), maxRetainedViolations, "At least one record must be retained.");
        }

        _maxRetainedViolations = maxRetainedViolations;
        _channels = channels;
        _limits = [.. limits];
        _channelIndices = new int[_limits.Length];
        _open = new OpenExcursion?[_limits.Length];
        Name = name;

        for (int i = 0; i < _limits.Length; i++)
        {
            ChannelLimit limit = _limits[i];
            ArgumentNullException.ThrowIfNull(limit);
            limit.Validate();

            int index = channels.IndexOf(limit.ChannelName);
            if (index < 0)
            {
                throw new ArgumentException(
                    $"Cannot monitor unknown channel '{limit.ChannelName}'.", nameof(limits));
            }

            _channelIndices[i] = index;
        }
    }

    public string Name { get; }

    /// <summary>The bands being watched.</summary>
    public IReadOnlyList<ChannelLimit> Limits => _limits;

    /// <summary>Excursions recorded so far, in the order they started.</summary>
    public IReadOnlyList<LimitViolation> Violations => _violations;

    /// <summary>The verdict as it stands. Fails as soon as one excursion is recorded.</summary>
    public ScenarioResult Result => new(
        _totalViolations == 0 ? ScenarioVerdict.Pass : ScenarioVerdict.Fail,
        [.. _violations],
        _samples,
        _endTime,
        _totalViolations);

    /// <summary>Total excursions detected, including any dropped past the retention cap.</summary>
    public long TotalViolationCount => _totalViolations;

    public void Initialize(in StepContext ctx)
    {
        _violations.Clear();
        Array.Clear(_open);
        _samples = 0;
        _endTime = 0.0;
        _totalViolations = 0;
    }

    public void Step(in StepContext ctx)
    {
        _samples++;
        _endTime = ctx.Time;

        for (int i = 0; i < _limits.Length; i++)
        {
            double value = _channels.Get(_channelIndices[i]);
            LimitViolationKind? kind = _limits[i].Check(value);
            OpenExcursion? open = _open[i];

            if (kind is null)
            {
                if (open is not null)
                {
                    Record(open.Close());
                    _open[i] = null;
                }

                continue;
            }

            if (open is null || open.Kind != kind.Value)
            {
                if (open is not null)
                {
                    // Crossed straight from one side to the other: close one event, open another.
                    Record(open.Close());
                }

                _open[i] = new OpenExcursion(
                    _limits[i].ChannelName,
                    _channelIndices[i],
                    kind.Value,
                    kind.Value == LimitViolationKind.High ? _limits[i].High : _limits[i].Low,
                    ctx.StepIndex,
                    ctx.Time,
                    value);
            }
            else
            {
                open.Extend(ctx.StepIndex, ctx.Time, value);
            }
        }
    }

    /// <summary>
    /// Closes any excursion still open at the end of a run and returns the final result. Call
    /// this once the engine has stopped, before reading the verdict.
    /// </summary>
    public ScenarioResult Finish()
    {
        for (int i = 0; i < _open.Length; i++)
        {
            if (_open[i] is { } open)
            {
                Record(open.Close());
                _open[i] = null;
            }
        }

        // Report violations in the order they started, not the order they ended.
        _violations.Sort(static (a, b) => a.FirstStepIndex != b.FirstStepIndex
            ? a.FirstStepIndex.CompareTo(b.FirstStepIndex)
            : a.ChannelIndex.CompareTo(b.ChannelIndex));

        return Result;
    }

    private void Record(LimitViolation violation)
    {
        _totalViolations++;
        if (_violations.Count < _maxRetainedViolations)
        {
            _violations.Add(violation);
        }
    }

    private sealed class OpenExcursion
    {
        private readonly string _channelName;
        private readonly int _channelIndex;
        private readonly double _limit;
        private readonly long _firstStepIndex;
        private readonly double _firstTime;

        private long _lastStepIndex;
        private double _lastTime;
        private double _peak;
        private long _samples;

        public OpenExcursion(
            string channelName,
            int channelIndex,
            LimitViolationKind kind,
            double limit,
            long firstStepIndex,
            double firstTime,
            double firstValue)
        {
            _channelName = channelName;
            _channelIndex = channelIndex;
            Kind = kind;
            _limit = limit;
            _firstStepIndex = firstStepIndex;
            _firstTime = firstTime;

            _lastStepIndex = firstStepIndex;
            _lastTime = firstTime;
            _peak = firstValue;
            _samples = 1;
        }

        public LimitViolationKind Kind { get; }

        public void Extend(long stepIndex, double time, double value)
        {
            _lastStepIndex = stepIndex;
            _lastTime = time;
            _samples++;

            bool worse = Kind == LimitViolationKind.High ? value > _peak : value < _peak;
            if (worse)
            {
                _peak = value;
            }
        }

        public LimitViolation Close() => new(
            _channelName,
            _channelIndex,
            Kind,
            _limit,
            _firstStepIndex,
            _firstTime,
            _lastStepIndex,
            _lastTime,
            _peak,
            _samples);
    }
}
