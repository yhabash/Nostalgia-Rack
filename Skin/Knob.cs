using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AmpPlayer;

/// <summary>
/// The champagne "skirt and scale" knob (K3 in the design): a printed 0–10 tick scale,
/// a wide skirt with a notch, and a smooth spun face with an engraved pointer line.
///
/// Use it like a slider: drag up/down (or left/right), turn the mouse wheel, or use the
/// arrow keys when it has focus. Double-click to go back to the default value.
/// It is drawn in code (OnRender), so it stays sharp at any size.
/// </summary>
public sealed class Knob : RangeBase
{
    public static readonly DependencyProperty DefaultValueProperty =
        DependencyProperty.Register(nameof(DefaultValue), typeof(double), typeof(Knob), new PropertyMetadata(0.0));

    /// <summary>The value a double-click returns to.</summary>
    public double DefaultValue
    {
        get => (double)GetValue(DefaultValueProperty);
        set => SetValue(DefaultValueProperty, value);
    }

    private Point _dragStart;
    private double _dragStartValue;

    static Knob()
    {
        FocusableProperty.OverrideMetadata(typeof(Knob), new FrameworkPropertyMetadata(true));
    }

    // Redraw whenever the value changes.
    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        InvalidateVisual();
    }

    public Knob()
    {
        Minimum = 0;
        Maximum = 1;
        SmallChange = 0.02;
        LargeChange = 0.1;
        Cursor = Cursors.SizeNS;
        IsEnabledChanged += (_, _) => InvalidateVisual();
    }

    // ---------------- Mouse and keyboard ----------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            Value = DefaultValue;
            e.Handled = true;
            return;
        }
        Focus();
        _dragStart = e.GetPosition(this);
        _dragStartValue = Value;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured) return;

        // 150 pixels of drag = the whole range. Up or right = more.
        var p = e.GetPosition(this);
        double delta = ((_dragStart.Y - p.Y) + (p.X - _dragStart.X)) / 150.0;
        Value = Math.Clamp(_dragStartValue + delta * (Maximum - Minimum), Minimum, Maximum);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        Value = Math.Clamp(Value + Math.Sign(e.Delta) * SmallChange * 2, Minimum, Maximum);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Keyboard.Modifiers != ModifierKeys.None) return; // leave Ctrl/Shift shortcuts to the window
        double step = e.Key switch
        {
            Key.Up or Key.Right => SmallChange,
            Key.Down or Key.Left => -SmallChange,
            Key.PageUp => LargeChange,
            Key.PageDown => -LargeChange,
            _ => 0
        };
        if (e.Key == Key.Home) { Value = Minimum; e.Handled = true; }
        else if (e.Key == Key.End) { Value = Maximum; e.Handled = true; }
        else if (step != 0) { Value = Math.Clamp(Value + step, Minimum, Maximum); e.Handled = true; }
    }

    // ---------------- Drawing ----------------

    private static readonly Brush TickBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0x3C, 0x26)));
    private static readonly Brush PointerBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x2F, 0x1C)));
    private static readonly Brush NotchBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7A, 0x5E, 0x2A)));
    private static readonly Pen FaceEdge = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x8C, 0x5A, 0x46, 0x23)), 1));

    private static readonly Brush SkirtBrush = Freeze(new RadialGradientBrush
    {
        GradientOrigin = new Point(0.4, 0.3), Center = new Point(0.4, 0.3), RadiusX = 0.75, RadiusY = 0.75,
        GradientStops =
        {
            new GradientStop(Color.FromRgb(0xF3, 0xE9, 0xD1), 0),
            new GradientStop(Color.FromRgb(0xC4, 0xB0, 0x83), 0.55),
            new GradientStop(Color.FromRgb(0x8F, 0x7B, 0x4E), 1)
        }
    });

    // The spun-metal face: alternating light and dark sectors, like brushed aluminium on a lathe.
    private static readonly Brush FaceBrush = Freeze(new LinearGradientBrush
    {
        StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
        GradientStops =
        {
            new GradientStop(Color.FromRgb(0xF7, 0xEF, 0xDC), 0),
            new GradientStop(Color.FromRgb(0xCB, 0xB9, 0x8F), 0.3),
            new GradientStop(Color.FromRgb(0xF2, 0xE8, 0xCF), 0.5),
            new GradientStop(Color.FromRgb(0xBB, 0xA7, 0x7A), 0.75),
            new GradientStop(Color.FromRgb(0xF7, 0xEF, 0xDC), 1)
        }
    });

    private static readonly Brush HighlightBrush = Freeze(new RadialGradientBrush
    {
        GradientOrigin = new Point(0.35, 0.28), Center = new Point(0.35, 0.28), RadiusX = 0.5, RadiusY = 0.5,
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF), 0),
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1)
        }
    });

    private static readonly Brush ShadowBrush = Freeze(new RadialGradientBrush
    {
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x70, 0, 0, 0), 0.6),
            new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1)
        }
    });

    private static T Freeze<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);

        // Proportions from the design: the tick ring fills the box, the knob is about 60% of it.
        double ringOuter = size / 2;
        double ringInner = ringOuter * 0.85;
        double skirt = ringOuter * 0.62;
        double face = skirt * 0.82;

        if (!IsEnabled) dc.PushOpacity(0.55);

        // 1. The printed 0–10 scale: 11 ticks over 270°, from bottom-left to bottom-right.
        var tickPen = new Pen(TickBrush, Math.Max(1, size / 60));
        for (int i = 0; i <= 10; i++)
        {
            double angle = DegreesToRadians(-135 + i * 27);
            dc.DrawLine(tickPen, PointAt(centre, ringInner, angle), PointAt(centre, ringOuter, angle));
        }

        // 2. A soft shadow under the knob, then the skirt and the face.
        dc.DrawEllipse(ShadowBrush, null, new Point(centre.X, centre.Y + size * 0.04), skirt * 1.08, skirt * 1.08);
        dc.DrawEllipse(SkirtBrush, null, centre, skirt, skirt);
        dc.DrawEllipse(FaceBrush, FaceEdge, centre, face, face);
        dc.DrawEllipse(HighlightBrush, null, centre, face, face);

        // 3. The pointer: an engraved line on the face and a notch on the skirt.
        double fraction = Maximum > Minimum ? (Value - Minimum) / (Maximum - Minimum) : 0;
        double pointerAngle = DegreesToRadians(-135 + 270 * fraction);
        var pointerPen = new Pen(PointerBrush, Math.Max(1.5, size / 28)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawLine(pointerPen, PointAt(centre, face * 0.25, pointerAngle), PointAt(centre, face * 0.85, pointerAngle));
        var notchPen = new Pen(NotchBrush, Math.Max(1.5, size / 30));
        dc.DrawLine(notchPen, PointAt(centre, face * 1.02, pointerAngle), PointAt(centre, skirt * 0.98, pointerAngle));

        if (!IsEnabled) dc.Pop();

        // A focus ring when you reach it with the Tab key, so it can be used without a mouse.
        if (IsKeyboardFocused)
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0xAA, 0xB8, 0x95, 0x5A)), 1) { DashStyle = DashStyles.Dot },
                           centre, ringOuter - 0.5, ringOuter - 0.5);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }

    // Angle 0 = straight up; positive = clockwise.
    private static Point PointAt(Point centre, double radius, double angle) =>
        new(centre.X + radius * Math.Sin(angle), centre.Y - radius * Math.Cos(angle));

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;
}
