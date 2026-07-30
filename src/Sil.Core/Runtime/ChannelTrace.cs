using Sil.Core.Channels;
using Sil.Core.Engine;

namespace Sil.Core.Runtime;

/// <summary>
/// A fixed-capacity ring buffer of recent (time, value) samples for one channel, used to feed
/// live displays. Old samples are overwritten, so memory is bounded no matter how long a run is.
/// </summary>
/// <remarks>
/// The simulation thread writes and the UI thread reads, so both sides take a lock. The buffer
/// is deliberately outside the deterministic path: dropping display samples can never change a
/// computed result.
/// </remarks>
public sealed class ChannelTrace
{
    private readonly object _gate = new();
    private readonly double[] _times;
    private readonly double[] _values;

    private int _start;
    private int _count;

    public ChannelTrace(string channelName, int capacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least 1.");
        }

        ChannelName = channelName;
        _times = new double[capacity];
        _values = new double[capacity];
    }

    /// <summary>Channel this trace follows.</summary>
    public string ChannelName { get; }

    /// <summary>Maximum number of retained samples.</summary>
    public int Capacity => _times.Length;

    /// <summary>Number of samples currently retained.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Appends a sample, evicting the oldest one when full.</summary>
    public void Add(double time, double value)
    {
        lock (_gate)
        {
            int index = (_start + _count) % _times.Length;
            _times[index] = time;
            _values[index] = value;

            if (_count == _times.Length)
            {
                _start = (_start + 1) % _times.Length;
            }
            else
            {
                _count++;
            }
        }
    }

    /// <summary>Discards every sample.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _start = 0;
            _count = 0;
        }
    }

    /// <summary>
    /// Copies the retained samples, oldest first, into the supplied buffers and returns how many
    /// were written. Both buffers must hold at least <see cref="Capacity"/> entries.
    /// </summary>
    public int Snapshot(Span<double> times, Span<double> values)
    {
        if (times.Length < Capacity || values.Length < Capacity)
        {
            throw new ArgumentException(
                $"Snapshot buffers must hold at least {Capacity} samples.", nameof(times));
        }

        lock (_gate)
        {
            for (int i = 0; i < _count; i++)
            {
                int index = (_start + i) % _times.Length;
                times[i] = _times[index];
                values[i] = _values[index];
            }

            return _count;
        }
    }

    /// <summary>
    /// Value range of the retained samples. Returns <c>(0, 0)</c> when the trace is empty.
    /// </summary>
    public (double Min, double Max) ValueRange()
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                return (0.0, 0.0);
            }

            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;

            for (int i = 0; i < _count; i++)
            {
                double value = _values[(_start + i) % _values.Length];
                if (value < min)
                {
                    min = value;
                }

                if (value > max)
                {
                    max = value;
                }
            }

            return (min, max);
        }
    }
}

/// <summary>
/// Samples channels into <see cref="ChannelTrace"/> ring buffers as a recorder task, with
/// decimation so a 1 kHz run does not have to feed a display at 1 kHz.
/// </summary>
public sealed class TraceRecorder : ISimTask
{
    private readonly ChannelTable _channels;
    private readonly int[] _channelIndices;
    private readonly ChannelTrace[] _traces;
    private readonly int _decimation;

    public TraceRecorder(
        ChannelTable channels,
        IReadOnlyList<ChannelTrace> traces,
        int decimation = 1,
        string name = "trace")
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(traces);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (decimation < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(decimation), decimation, "Decimation must be at least 1.");
        }

        _channels = channels;
        _traces = [.. traces];
        _channelIndices = new int[_traces.Length];
        _decimation = decimation;
        Name = name;

        for (int i = 0; i < _traces.Length; i++)
        {
            int index = channels.IndexOf(_traces[i].ChannelName);
            if (index < 0)
            {
                throw new ArgumentException(
                    $"Cannot trace unknown channel '{_traces[i].ChannelName}'.", nameof(traces));
            }

            _channelIndices[i] = index;
        }
    }

    public string Name { get; }

    /// <summary>The traces being filled.</summary>
    public IReadOnlyList<ChannelTrace> Traces => _traces;

    public void Initialize(in StepContext ctx)
    {
        foreach (ChannelTrace trace in _traces)
        {
            trace.Clear();
        }
    }

    public void Step(in StepContext ctx)
    {
        if (ctx.StepIndex % _decimation != 0)
        {
            return;
        }

        for (int i = 0; i < _traces.Length; i++)
        {
            _traces[i].Add(ctx.Time, _channels.Get(_channelIndices[i]));
        }
    }
}
