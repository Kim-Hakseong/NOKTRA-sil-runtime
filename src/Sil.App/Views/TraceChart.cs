using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sil.Core.Runtime;

namespace Sil.App.Views;

/// <summary>
/// Draws channel traces as polylines. Written directly against Avalonia's drawing context rather
/// than pulled in as a charting dependency — the allowed package list is deliberately short, and
/// a strip chart is a polyline and two axes.
/// </summary>
public sealed class TraceChart : Control
{
    /// <summary>The traces to draw.</summary>
    public static readonly StyledProperty<IReadOnlyList<ChannelTrace>?> TracesProperty =
        AvaloniaProperty.Register<TraceChart, IReadOnlyList<ChannelTrace>?>(nameof(Traces));

    /// <summary>
    /// One accent, then ink shades. A second hue would compete with the accent and stop it reading
    /// as "this is the live value"; separation between series comes from weight instead.
    /// </summary>
    private static readonly IBrush[] SeriesBrushes =
    [
        new SolidColorBrush(Color.FromRgb(0x1E, 0x7C, 0x8C)),   // accent
        new SolidColorBrush(Color.FromRgb(0x2B, 0x30, 0x33)),   // soft ink
        new SolidColorBrush(Color.FromRgb(0x31, 0xA9, 0xBC)),   // bright accent
        new SolidColorBrush(Color.FromRgb(0x7E, 0x85, 0x88)),   // muted
        new SolidColorBrush(Color.FromRgb(0xB9, 0x86, 0x2F)),   // warn
    ];

    private static readonly double[] SeriesThickness = [1.6, 1.2, 1.2, 1.1, 1.2];

    private static readonly IPen GridPen =
        new Pen(new SolidColorBrush(Color.FromRgb(0xDE, 0xE1, 0xE2)), 1.0);

    private static readonly IBrush AxisBrush = new SolidColorBrush(Color.FromRgb(0x7E, 0x85, 0x88));

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0xE7, 0xE9, 0xEA));

    private static readonly IBrush EdgeBrush = new SolidColorBrush(Color.FromRgb(0xCD, 0xD1, 0xD3));

    private double[] _times = [];
    private double[] _values = [];

    public IReadOnlyList<ChannelTrace>? Traces
    {
        get => GetValue(TracesProperty);
        set => SetValue(TracesProperty, value);
    }

    /// <summary>Requests a repaint from the display timer.</summary>
    public void Refresh() => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        Rect area = new(Bounds.Size);

        // Recessed plot bed, matching Border.sunk.
        context.DrawRectangle(
            BackgroundBrush, new Pen(EdgeBrush, 1.0), area.Deflate(0.5), 6, 6);

        IReadOnlyList<ChannelTrace>? traces = Traces;
        if (traces is null || traces.Count == 0 || area.Width < 8 || area.Height < 8)
        {
            return;
        }

        Rect plot = area.Deflate(new Thickness(10, 10, 10, 10));

        if (!TryComputeRanges(traces, out double tMin, out double tMax, out double vMin, out double vMax))
        {
            DrawGrid(context, plot);
            return;
        }

        DrawGrid(context, plot);
        DrawZeroLine(context, plot, vMin, vMax);

        for (int i = 0; i < traces.Count; i++)
        {
            DrawTrace(
                context, plot, traces[i],
                SeriesBrushes[i % SeriesBrushes.Length],
                SeriesThickness[i % SeriesThickness.Length],
                tMin, tMax, vMin, vMax);
        }

        DrawScaleLabels(context, plot, tMin, tMax, vMin, vMax);
    }

    private bool TryComputeRanges(
        IReadOnlyList<ChannelTrace> traces,
        out double tMin, out double tMax, out double vMin, out double vMax)
    {
        tMin = double.PositiveInfinity;
        tMax = double.NegativeInfinity;
        vMin = double.PositiveInfinity;
        vMax = double.NegativeInfinity;

        bool any = false;

        foreach (ChannelTrace trace in traces)
        {
            EnsureBuffers(trace.Capacity);
            int count = trace.Snapshot(_times, _values);
            if (count == 0)
            {
                continue;
            }

            any = true;
            tMin = Math.Min(tMin, _times[0]);
            tMax = Math.Max(tMax, _times[count - 1]);

            (double min, double max) = trace.ValueRange();
            vMin = Math.Min(vMin, min);
            vMax = Math.Max(vMax, max);
        }

        if (!any)
        {
            return false;
        }

        if (tMax - tMin < 1e-9)
        {
            tMax = tMin + 1.0;
        }

        if (vMax - vMin < 1e-9)
        {
            // A flat trace still needs a visible band around it.
            double centre = vMin;
            vMin = centre - 1.0;
            vMax = centre + 1.0;
        }
        else
        {
            double margin = (vMax - vMin) * 0.08;
            vMin -= margin;
            vMax += margin;
        }

        return true;
    }

    private void DrawTrace(
        DrawingContext context,
        Rect plot,
        ChannelTrace trace,
        IBrush brush,
        double thickness,
        double tMin, double tMax, double vMin, double vMax)
    {
        EnsureBuffers(trace.Capacity);
        int count = trace.Snapshot(_times, _values);
        if (count < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(Project(plot, _times[0], _values[0], tMin, tMax, vMin, vMax), isFilled: false);
            for (int i = 1; i < count; i++)
            {
                ctx.LineTo(Project(plot, _times[i], _values[i], tMin, tMax, vMin, vMax));
            }

            ctx.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, new Pen(brush, thickness), geometry);
    }

    private static Point Project(
        Rect plot, double t, double v, double tMin, double tMax, double vMin, double vMax)
    {
        double x = plot.X + ((t - tMin) / (tMax - tMin) * plot.Width);
        double y = plot.Y + plot.Height - ((v - vMin) / (vMax - vMin) * plot.Height);
        return new Point(x, y);
    }

    private static void DrawGrid(DrawingContext context, Rect plot)
    {
        const int divisions = 4;

        for (int i = 0; i <= divisions; i++)
        {
            double y = plot.Y + (plot.Height * i / divisions);
            context.DrawLine(GridPen, new Point(plot.X, y), new Point(plot.Right, y));

            double x = plot.X + (plot.Width * i / divisions);
            context.DrawLine(GridPen, new Point(x, plot.Y), new Point(x, plot.Bottom));
        }
    }

    private static void DrawZeroLine(DrawingContext context, Rect plot, double vMin, double vMax)
    {
        if (vMin > 0.0 || vMax < 0.0)
        {
            return;
        }

        double y = plot.Y + plot.Height - ((0.0 - vMin) / (vMax - vMin) * plot.Height);
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xCD, 0xD1, 0xD3)), 1.0);
        context.DrawLine(pen, new Point(plot.X, y), new Point(plot.Right, y));
    }

    private static void DrawScaleLabels(
        DrawingContext context, Rect plot, double tMin, double tMax, double vMin, double vMax)
    {
        // 9px monospace micro labels, as everywhere else in the shell.
        var typeface = new Typeface("Menlo,Cascadia Mono,Consolas,monospace");

        Draw($"{vMax:0.###}", plot.X + 6, plot.Y + 3);
        Draw($"{vMin:0.###}", plot.X + 6, plot.Bottom - 14);
        Draw($"{tMin:0.##}s", plot.X + 6, plot.Bottom - 28);

        var right = new FormattedText(
            $"{tMax:0.##}s", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, 9, AxisBrush);
        context.DrawText(right, new Point(plot.Right - right.Width - 6, plot.Bottom - 28));

        void Draw(string text, double x, double y)
        {
            var formatted = new FormattedText(
                text, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 9, AxisBrush);
            context.DrawText(formatted, new Point(x, y));
        }
    }

    private void EnsureBuffers(int capacity)
    {
        if (_times.Length < capacity)
        {
            _times = new double[capacity];
            _values = new double[capacity];
        }
    }
}
