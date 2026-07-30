using System.Globalization;
using System.Text;
using Sil.Core.Channels;
using Sil.Core.Engine;

namespace Sil.Core.Logging;

/// <summary>
/// Writes channel values to CSV, one row per logged cycle.
/// </summary>
/// <remarks>
/// The output is byte-for-byte reproducible: LF line endings regardless of platform, UTF-8
/// without a BOM, invariant round-trip number formatting, and nothing time-of-day stamped into
/// the file. Two runs of the same scenario therefore produce identical bytes, which is what
/// makes a diff a usable regression check.
/// </remarks>
public sealed class CsvChannelLogger : ISimTask, IDisposable
{
    private const string TimeColumnHeader = "t";

    private readonly ChannelTable _channels;
    private readonly int[] _columns;
    private readonly TextWriter _writer;
    private readonly bool _ownsWriter;
    private readonly int _decimation;
    private readonly StringBuilder _row = new(256);

    private bool _headerWritten;
    private bool _disposed;

    /// <param name="channels">Table to sample.</param>
    /// <param name="writer">Destination. Its <c>NewLine</c> is forced to LF.</param>
    /// <param name="channelNames">Channels to log, in column order. Null logs every channel.</param>
    /// <param name="decimation">Log every Nth cycle. 1 logs every cycle.</param>
    /// <param name="ownsWriter">Whether disposing the logger also disposes the writer.</param>
    /// <param name="name">Task name.</param>
    public CsvChannelLogger(
        ChannelTable channels,
        TextWriter writer,
        IReadOnlyList<string>? channelNames = null,
        int decimation = 1,
        bool ownsWriter = false,
        string name = "csv-log")
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (decimation < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(decimation), decimation, "Decimation must be at least 1.");
        }

        _channels = channels;
        _writer = writer;
        _writer.NewLine = "\n";
        _ownsWriter = ownsWriter;
        _decimation = decimation;
        Name = name;

        if (channelNames is null)
        {
            _columns = new int[channels.Count];
            for (int i = 0; i < _columns.Length; i++)
            {
                _columns[i] = i;
            }
        }
        else
        {
            _columns = new int[channelNames.Count];
            for (int i = 0; i < channelNames.Count; i++)
            {
                int index = channels.IndexOf(channelNames[i]);
                if (index < 0)
                {
                    throw new ArgumentException(
                        $"Cannot log unknown channel '{channelNames[i]}'.", nameof(channelNames));
                }

                _columns[i] = index;
            }
        }
    }

    /// <summary>Creates a logger that writes to a file, replacing anything already there.</summary>
    public static CsvChannelLogger ToFile(
        ChannelTable channels,
        string path,
        IReadOnlyList<string>? channelNames = null,
        int decimation = 1,
        string name = "csv-log")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            return new CsvChannelLogger(channels, writer, channelNames, decimation, ownsWriter: true, name);
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    public string Name { get; }

    /// <summary>Number of rows written since the last reset.</summary>
    public long RowsWritten { get; private set; }

    /// <summary>Channel names in column order, excluding the leading time column.</summary>
    public IReadOnlyList<string> Columns
        => [.. _columns.Select(index => _channels.Definition(index).Name)];

    public void Initialize(in StepContext ctx)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_headerWritten)
        {
            WriteHeader();
            _headerWritten = true;
        }

        RowsWritten = 0;
    }

    public void Step(in StepContext ctx)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ctx.StepIndex % _decimation != 0)
        {
            return;
        }

        _row.Clear();
        Append(_row, ctx.Time);

        foreach (int column in _columns)
        {
            _row.Append(',');
            Append(_row, _channels.Get(column));
        }

        _writer.Write(_row.ToString());
        _writer.Write('\n');
        RowsWritten++;
    }

    /// <summary>Flushes buffered rows to the underlying writer.</summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writer.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_ownsWriter)
        {
            _writer.Dispose();
        }
        else
        {
            _writer.Flush();
        }
    }

    private void WriteHeader()
    {
        _row.Clear();
        _row.Append(TimeColumnHeader);

        foreach (int column in _columns)
        {
            ChannelDefinition definition = _channels.Definition(column);
            _row.Append(',').Append(definition.Name);
            if (definition.Unit.Length > 0)
            {
                _row.Append('[').Append(definition.Unit).Append(']');
            }
        }

        _writer.Write(_row.ToString());
        _writer.Write('\n');
    }

    private static void Append(StringBuilder builder, double value)
        => builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
}
