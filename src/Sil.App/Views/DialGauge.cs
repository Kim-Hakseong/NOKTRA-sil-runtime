using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Sil.App.Views;

/// <summary>
/// The Noktra dial: a dashed tick ring with an accent arc sweeping the value's position inside its
/// acceptance band, and the reading in the middle. Drawn rather than composed in XAML so the ring,
/// the arc and the limit marks stay in exact geometric agreement at any size.
/// </summary>
public sealed class DialGauge : Control
{
    /// <summary>Fraction of the band the value sits at, 0..1.</summary>
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<DialGauge, double>(nameof(Fraction), 0.0);

    /// <summary>The reading shown in the centre.</summary>
    public static readonly StyledProperty<string?> ValueTextProperty =
        AvaloniaProperty.Register<DialGauge, string?>(nameof(ValueText));

    /// <summary>Unit caption under the reading.</summary>
    public static readonly StyledProperty<string?> UnitTextProperty =
        AvaloniaProperty.Register<DialGauge, string?>(nameof(UnitText));

    /// <summary>Channel name shown above the reading.</summary>
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<DialGauge, string?>(nameof(Label));

    /// <summary>Draws the arc in the alarm colour instead of the accent.</summary>
    public static readonly StyledProperty<bool> IsAlarmProperty =
        AvaloniaProperty.Register<DialGauge, bool>(nameof(IsAlarm), false);

    // Angles increase clockwise from straight up. Starting at 225 (bottom-left) and sweeping 270
    // ends at 135 (bottom-right), which puts the gap at the bottom where it reads as the origin.
    private const double StartAngle = 225.0;
    private const double SweepAngle = 270.0;

    private static readonly IBrush TickBrush = new SolidColorBrush(Color.FromRgb(0xCD, 0xD1, 0xD3));
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.FromRgb(0xDE, 0xE1, 0xE2));
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x7C, 0x8C));
    private static readonly IBrush AlarmBrush = new SolidColorBrush(Color.FromRgb(0xA8, 0x41, 0x2F));
    private static readonly IBrush InkBrush = new SolidColorBrush(Color.FromRgb(0x0E, 0x11, 0x13));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.FromRgb(0x7E, 0x85, 0x88));

    static DialGauge()
    {
        AffectsRender<DialGauge>(
            FractionProperty, ValueTextProperty, UnitTextProperty, LabelProperty, IsAlarmProperty);
    }

    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public string? ValueText
    {
        get => GetValue(ValueTextProperty);
        set => SetValue(ValueTextProperty, value);
    }

    public string? UnitText
    {
        get => GetValue(UnitTextProperty);
        set => SetValue(UnitTextProperty, value);
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public bool IsAlarm
    {
        get => GetValue(IsAlarmProperty);
        set => SetValue(IsAlarmProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        double size = Math.Min(Bounds.Width, Bounds.Height);
        if (size < 24)
        {
            return;
        }

        var centre = new Point(Bounds.Width / 2.0, Bounds.Height / 2.0);
        double outerRadius = (size / 2.0) - 2.0;
        double trackRadius = outerRadius - 9.0;

        DrawTickRing(context, centre, outerRadius);

        // Unfilled track, then the accent arc over it.
        DrawArc(context, centre, trackRadius, StartAngle, SweepAngle, TrackBrush, 5.0);

        double fraction = Math.Clamp(Fraction, 0.0, 1.0);
        if (fraction > 0.0)
        {
            DrawArc(
                context, centre, trackRadius, StartAngle, SweepAngle * fraction,
                IsAlarm ? AlarmBrush : AccentBrush, 5.0);
        }

        DrawCentreText(context, centre, trackRadius);
    }

    /// <summary>Dashed ring standing in for a graduated bezel.</summary>
    private static void DrawTickRing(DrawingContext context, Point centre, double radius)
    {
        // Dash lengths are multiples of the stroke thickness, so the design system's 0.14/0.72
        // pair is sub-pixel at this weight and renders as a solid ring. Scaled to stay visible.
        var pen = new Pen(TickBrush, 3.0)
        {
            DashStyle = new DashStyle([0.5, 2.4], 0),
        };

        context.DrawEllipse(null, pen, centre, radius, radius);
    }

    private static void DrawArc(
        DrawingContext context,
        Point centre,
        double radius,
        double startDegrees,
        double sweepDegrees,
        IBrush brush,
        double thickness)
    {
        if (sweepDegrees <= 0.0)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            // Step the arc rather than using ArcTo so a sweep past 180 degrees needs no
            // large-arc bookkeeping and the joins stay smooth.
            int segments = Math.Max(2, (int)Math.Ceiling(sweepDegrees / 4.0));
            ctx.BeginFigure(PointOn(centre, radius, startDegrees), isFilled: false);

            for (int i = 1; i <= segments; i++)
            {
                double angle = startDegrees + (sweepDegrees * i / segments);
                ctx.LineTo(PointOn(centre, radius, angle));
            }

            ctx.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, new Pen(brush, thickness, lineCap: PenLineCap.Round), geometry);
    }

    /// <summary>Angles run clockwise from the left-bottom opening, in screen coordinates.</summary>
    private static Point PointOn(Point centre, double radius, double degrees)
    {
        double radians = (degrees - 90.0) * Math.PI / 180.0;
        return new Point(
            centre.X + (radius * Math.Sin(radians + (Math.PI / 2.0))),
            centre.Y - (radius * Math.Cos(radians + (Math.PI / 2.0))));
    }

    private void DrawCentreText(DrawingContext context, Point centre, double radius)
    {
        var monospace = new Typeface("Menlo,Cascadia Mono,Consolas,monospace");
        var sans = new Typeface(FontFamily.Default, weight: FontWeight.SemiBold);

        if (!string.IsNullOrEmpty(Label))
        {
            var label = new FormattedText(
                Label.ToUpperInvariant(), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, sans, 9, MutedBrush);
            context.DrawText(label, new Point(centre.X - (label.Width / 2.0), centre.Y - radius * 0.55));
        }

        if (!string.IsNullOrEmpty(ValueText))
        {
            var value = new FormattedText(
                ValueText, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, monospace, 20, InkBrush);
            context.DrawText(value, new Point(centre.X - (value.Width / 2.0), centre.Y - 13));
        }

        if (!string.IsNullOrEmpty(UnitText))
        {
            var unit = new FormattedText(
                UnitText, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, sans, 9, MutedBrush);
            context.DrawText(unit, new Point(centre.X - (unit.Width / 2.0), centre.Y + 13));
        }
    }
}
