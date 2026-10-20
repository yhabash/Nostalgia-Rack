using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AmpPlayer;

/// <summary>
/// The cream backlit needle meter (V1 in the design), drawn in code so it stays sharp.
/// Two kinds:
/// - VU: shows how loud the music is, from −20 to +3 dB, with a red zone above 0.
/// - Gain reduction (GR): shows how hard the compressor works (0 = not at all).
/// The window updates <see cref="Level"/> many times a second; the needle movement
/// itself (how fast it rises and falls) is worked out by the window, like a real VU meter.
/// </summary>
public sealed class VuMeter : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(VuMeter),
        new FrameworkPropertyMetadata(-40.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ChannelLabelProperty = DependencyProperty.Register(
        nameof(ChannelLabel), typeof(string), typeof(VuMeter),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsGainReductionProperty = DependencyProperty.Register(
        nameof(IsGainReduction), typeof(bool), typeof(VuMeter),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>VU: the level in dB (0 = the reference level). GR: the reduction in dB.</summary>
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    /// <summary>"L" or "R", printed in the corner.</summary>
    public string ChannelLabel { get => (string)GetValue(ChannelLabelProperty); set => SetValue(ChannelLabelProperty, value); }

    /// <summary>True for the compressor's gain-reduction meter.</summary>
    public bool IsGainReduction { get => (bool)GetValue(IsGainReductionProperty); set => SetValue(IsGainReductionProperty, value); }

    // The scale positions (dB → needle angle in degrees, 0 = straight up), from the design.
    private static readonly (double db, double angle)[] VuScale =
        { (-20, -48), (-10, -32), (-7, -20), (-5, -10), (-3, 2), (0, 22), (3, 48) };
    private static readonly (double db, double angle)[] GrScale =
        { (20, -48), (10, -32), (6, -20), (4, -10), (2, 2), (1, 22), (0, 48) };

    // All drawing happens in a 158 × 98 "design box" (the bezel), then is scaled to fit.
    private const double DesignWidth = 158, DesignHeight = 98;
    private static readonly Point Pivot = new(79, 102);  // below the face, like a real meter
    private const double ArcRadius = 70;

    private static readonly Brush BezelBrush = Freeze(new LinearGradientBrush(Color.FromRgb(0xE6, 0xD8, 0xB6), Color.FromRgb(0xA8, 0x94, 0x5F), 90));
    private static readonly Brush FaceBrush = Freeze(new RadialGradientBrush
    {
        Center = new Point(0.5, 1.15), GradientOrigin = new Point(0.5, 1.15), RadiusX = 0.9, RadiusY = 1.25,
        GradientStops =
        {
            new GradientStop(Color.FromRgb(0xFF, 0xF3, 0xD2), 0),
            new GradientStop(Color.FromRgb(0xF6, 0xD8, 0x94), 0.45),
            new GradientStop(Color.FromRgb(0xE2, 0xA8, 0x50), 1)
        }
    });
    private static readonly Brush GlareBrush = Freeze(new LinearGradientBrush(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF), new Point(0, 0), new Point(0.6, 0.6)));
    private static readonly Brush InnerShadow = Freeze(new LinearGradientBrush(Color.FromArgb(0x60, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 90) { EndPoint = new Point(0, 0.12) });
    private static readonly Brush InkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x20, 0x14)));
    private static readonly Brush RedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xB3, 0x26, 0x1E)));
    private static readonly Brush NeedleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1C, 0x17, 0x12)));
    private static readonly Brush PivotBrush = Freeze(new RadialGradientBrush(Color.FromRgb(0x3C, 0x3A, 0x36), Color.FromRgb(0x0B, 0x0B, 0x0A)));
    private static readonly Typeface LabelFont = new(new FontFamily(new Uri("pack://application:,,,/"), "./Skin/Fonts/#Jost"),
                                                     FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        double scale = Math.Min(ActualWidth / DesignWidth, ActualHeight / DesignHeight);
        dc.PushTransform(new ScaleTransform(scale, scale));

        // Bezel and face.
        dc.DrawRoundedRectangle(BezelBrush, null, new Rect(0, 0, DesignWidth, DesignHeight), 6, 6);
        var faceRect = new Rect(4, 4, 150, 90);
        dc.PushClip(new RectangleGeometry(faceRect, 3, 3));
        dc.DrawRectangle(FaceBrush, null, faceRect);
        dc.DrawRectangle(InnerShadow, null, faceRect);

        var scalePoints = IsGainReduction ? GrScale : VuScale;
        var ink = new Pen(InkBrush, 1);
        var red = new Pen(RedBrush, 3.5);

        // The arc, with a thick red section above 0 dB on the VU meter.
        DrawArc(dc, ink, -48, 48, ArcRadius);
        if (!IsGainReduction) DrawArc(dc, red, 22, 48, ArcRadius - 1.5);

        // Ticks and numbers.
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var (db, angle) in scalePoints)
        {
            bool inRed = !IsGainReduction && db > 0 || (!IsGainReduction && db == 0);
            var tickPen = new Pen(inRed ? RedBrush : InkBrush, 1.2);
            dc.DrawLine(tickPen, At(ArcRadius - 6, angle), At(ArcRadius, angle));

            string text = IsGainReduction ? db.ToString(CultureInfo.InvariantCulture)
                                          : db > 0 ? $"+{db}" : db.ToString(CultureInfo.InvariantCulture);
            var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFont, 7,
                                          inRed ? RedBrush : InkBrush, dpi);
            var p = At(ArcRadius - 15, angle);
            dc.DrawText(label, new Point(p.X - label.Width / 2, p.Y - label.Height / 2));
        }

        var title = new FormattedText(IsGainReduction ? "GR dB" : "VU", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                      LabelFont, 11, InkBrush, dpi);
        dc.DrawText(title, new Point(79 - title.Width / 2, 66));

        if (!string.IsNullOrEmpty(ChannelLabel))
        {
            var channel = new FormattedText(ChannelLabel, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFont, 7, InkBrush, dpi);
            dc.DrawText(channel, new Point(140, 80));
        }

        // The needle: long, black, from the pivot below the face.
        double needleAngle = AngleFor(Level, scalePoints);
        var needle = new Pen(NeedleBrush, 2) { EndLineCap = PenLineCap.Round };
        dc.DrawLine(needle, Pivot, At(88, needleAngle));
        dc.DrawEllipse(PivotBrush, null, new Point(Pivot.X, Pivot.Y - 2), 14, 14);

        dc.DrawRectangle(GlareBrush, null, faceRect);
        dc.Pop(); // clip
        dc.Pop(); // scale
    }

    // Converts a level in dB to a needle angle by following the printed scale.
    private static double AngleFor(double db, (double db, double angle)[] points)
    {
        bool ascending = points[0].db < points[^1].db;
        var ordered = ascending ? points : points.Reverse().ToArray();

        if (db <= ordered[0].db) return ascending ? ordered[0].angle - 2 : ordered[0].angle;   // resting
        if (db >= ordered[^1].db) return Math.Min(ordered[^1].angle + 4, 52);                   // pinned
        for (int i = 1; i < ordered.Length; i++)
        {
            if (db <= ordered[i].db)
            {
                double t = (db - ordered[i - 1].db) / (ordered[i].db - ordered[i - 1].db);
                return ordered[i - 1].angle + t * (ordered[i].angle - ordered[i - 1].angle);
            }
        }
        return ordered[^1].angle;
    }

    private static Point At(double radius, double angleDegrees)
    {
        double a = angleDegrees * Math.PI / 180;
        return new Point(Pivot.X + radius * Math.Sin(a), Pivot.Y - radius * Math.Cos(a));
    }

    private static void DrawArc(DrawingContext dc, Pen pen, double fromDegrees, double toDegrees, double radius)
    {
        var start = At(radius, fromDegrees);
        var end = At(radius, toDegrees);
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true));
        dc.DrawGeometry(null, pen, new PathGeometry(new[] { figure }));
    }
}
