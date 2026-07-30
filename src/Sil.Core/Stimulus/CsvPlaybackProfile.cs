using System.Globalization;

namespace Sil.Core.Stimulus;

/// <summary>How a CSV playback profile fills the gaps between its samples.</summary>
public enum PlaybackInterpolation
{
    /// <summary>Hold the previous sample until the next one is due (zero-order hold).</summary>
    Hold = 0,

    /// <summary>Straight line between neighbouring samples.</summary>
    Linear = 1,
}

/// <summary>What a CSV playback profile does once past its last sample.</summary>
public enum PlaybackEndBehaviour
{
    /// <summary>Hold the final sample value forever.</summary>
    HoldLast = 0,

    /// <summary>Restart from the first sample, repeating with the table's own period.</summary>
    Loop = 1,
}

/// <summary>
/// Replays a recorded time series. Lookup is a binary search over an immutable sorted table
/// rather than a moving cursor, so sampling stays a pure function of time and a run can be
/// paused, stepped or replayed without the profile drifting out of sync.
/// </summary>
public sealed class CsvPlaybackProfile : IStimulusProfile
{
    private readonly double[] _times;
    private readonly double[] _values;

    /// <param name="times">Sample times in seconds, strictly increasing.</param>
    /// <param name="values">Sample values, one per time.</param>
    /// <param name="interpolation">Gap-filling rule.</param>
    /// <param name="endBehaviour">What happens after the last sample.</param>
    public CsvPlaybackProfile(
        IReadOnlyList<double> times,
        IReadOnlyList<double> values,
        PlaybackInterpolation interpolation = PlaybackInterpolation.Linear,
        PlaybackEndBehaviour endBehaviour = PlaybackEndBehaviour.HoldLast)
    {
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(values);

        if (times.Count == 0)
        {
            throw new ArgumentException("A playback profile needs at least one sample.", nameof(times));
        }

        if (times.Count != values.Count)
        {
            throw new ArgumentException(
                $"Got {times.Count} sample times but {values.Count} values.", nameof(values));
        }

        _times = new double[times.Count];
        _values = new double[values.Count];

        for (int i = 0; i < times.Count; i++)
        {
            double time = times[i];
            double value = values[i];

            if (!double.IsFinite(time) || !double.IsFinite(value))
            {
                throw new ArgumentException($"Sample {i} is not finite.", nameof(times));
            }

            if (i > 0 && time <= times[i - 1])
            {
                throw new ArgumentException(
                    $"Sample times must strictly increase; sample {i} ({time}) does not follow {times[i - 1]}.",
                    nameof(times));
            }

            _times[i] = time;
            _values[i] = value;
        }

        Interpolation = interpolation;
        EndBehaviour = endBehaviour;
    }

    public PlaybackInterpolation Interpolation { get; }

    public PlaybackEndBehaviour EndBehaviour { get; }

    /// <summary>Number of samples in the table.</summary>
    public int SampleCount => _times.Length;

    /// <summary>Time of the first sample.</summary>
    public double StartTime => _times[0];

    /// <summary>Time of the last sample.</summary>
    public double EndTime => _times[^1];

    public double ValueAt(double t)
    {
        if (_times.Length == 1)
        {
            return _values[0];
        }

        double time = t;

        if (EndBehaviour == PlaybackEndBehaviour.Loop && time > EndTime)
        {
            double period = EndTime - StartTime;
            time = StartTime + ((time - StartTime) % period);
        }

        if (time <= StartTime)
        {
            return _values[0];
        }

        if (time >= EndTime)
        {
            return _values[^1];
        }

        int index = Array.BinarySearch(_times, time);
        if (index >= 0)
        {
            return _values[index];
        }

        int next = ~index;
        int previous = next - 1;

        if (Interpolation == PlaybackInterpolation.Hold)
        {
            return _values[previous];
        }

        double span = _times[next] - _times[previous];
        double fraction = (time - _times[previous]) / span;
        return _values[previous] + (fraction * (_values[next] - _values[previous]));
    }
}

/// <summary>A single named column read from a stimulus CSV file.</summary>
/// <param name="Name">Column header.</param>
/// <param name="Profile">Playback profile built from that column.</param>
public sealed record CsvStimulusColumn(string Name, CsvPlaybackProfile Profile);

/// <summary>
/// Raised when a stimulus CSV cannot be read as a time series.
/// </summary>
public sealed class StimulusFormatException : Exception
{
    public StimulusFormatException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Reads a stimulus CSV: one header row, a leading time column in seconds, then one column per
/// signal. Parsing is invariant-culture only — a file must mean the same thing on every machine.
/// </summary>
public static class CsvStimulusReader
{
    /// <summary>Reads a CSV file from disk.</summary>
    public static IReadOnlyList<CsvStimulusColumn> ReadFile(
        string path,
        PlaybackInterpolation interpolation = PlaybackInterpolation.Linear,
        PlaybackEndBehaviour endBehaviour = PlaybackEndBehaviour.HoldLast)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var reader = new StreamReader(path);
        return Read(reader, interpolation, endBehaviour);
    }

    /// <summary>Reads CSV text.</summary>
    public static IReadOnlyList<CsvStimulusColumn> ReadText(
        string text,
        PlaybackInterpolation interpolation = PlaybackInterpolation.Linear,
        PlaybackEndBehaviour endBehaviour = PlaybackEndBehaviour.HoldLast)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var reader = new StringReader(text);
        return Read(reader, interpolation, endBehaviour);
    }

    /// <summary>Reads a CSV time series from an open reader.</summary>
    public static IReadOnlyList<CsvStimulusColumn> Read(
        TextReader reader,
        PlaybackInterpolation interpolation = PlaybackInterpolation.Linear,
        PlaybackEndBehaviour endBehaviour = PlaybackEndBehaviour.HoldLast)
    {
        ArgumentNullException.ThrowIfNull(reader);

        string? headerLine = ReadContentLine(reader);
        if (headerLine is null)
        {
            throw new StimulusFormatException("The stimulus CSV is empty.");
        }

        string[] headers = SplitRow(headerLine);
        if (headers.Length < 2)
        {
            throw new StimulusFormatException(
                "A stimulus CSV needs a time column plus at least one signal column.");
        }

        var times = new List<double>();
        var columns = new List<double>[headers.Length - 1];
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = [];
        }

        int rowNumber = 1;
        string? line;
        while ((line = ReadContentLine(reader)) is not null)
        {
            rowNumber++;
            string[] cells = SplitRow(line);
            if (cells.Length != headers.Length)
            {
                throw new StimulusFormatException(
                    $"Row {rowNumber} has {cells.Length} cells but the header declares {headers.Length}.");
            }

            times.Add(ParseCell(cells[0], rowNumber, headers[0]));
            for (int i = 1; i < cells.Length; i++)
            {
                columns[i - 1].Add(ParseCell(cells[i], rowNumber, headers[i]));
            }
        }

        if (times.Count == 0)
        {
            throw new StimulusFormatException("The stimulus CSV has a header but no data rows.");
        }

        var result = new CsvStimulusColumn[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            try
            {
                result[i] = new CsvStimulusColumn(
                    headers[i + 1],
                    new CsvPlaybackProfile(times, columns[i], interpolation, endBehaviour));
            }
            catch (ArgumentException ex)
            {
                throw new StimulusFormatException($"Column '{headers[i + 1]}': {ex.Message}");
            }
        }

        return result;
    }

    private static string? ReadContentLine(TextReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string trimmed = line.Trim();
            if (trimmed.Length != 0 && !trimmed.StartsWith('#'))
            {
                return line;
            }
        }

        return null;
    }

    private static string[] SplitRow(string line)
    {
        string[] cells = line.Split(',');
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i] = cells[i].Trim();
        }

        return cells;
    }

    private static double ParseCell(string cell, int rowNumber, string columnName)
    {
        if (!double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            throw new StimulusFormatException(
                $"Row {rowNumber}, column '{columnName}': '{cell}' is not a number.");
        }

        if (!double.IsFinite(value))
        {
            throw new StimulusFormatException(
                $"Row {rowNumber}, column '{columnName}': '{cell}' is not finite.");
        }

        return value;
    }
}
