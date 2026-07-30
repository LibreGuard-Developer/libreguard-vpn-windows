using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Controls;

public abstract class ConnectionAnimationElement : FrameworkElement
{
    private readonly DispatcherTimer _timer;
    private DateTime _lastTick = DateTime.UtcNow;
    private DateTime _phaseStartedAt = DateTime.UtcNow;
    private double _phaseStartProgress;
    private double _phaseDurationSeconds = 15;
    private Color _animatedColor = StatusColor(ConnectionStatus.Disconnected);

    protected ConnectionAnimationElement()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += OnAnimationTick;
        Unloaded += (_, _) => _timer.Stop();
        Loaded += (_, _) => EnsureTimer();
    }

    public static readonly DependencyProperty StatusProperty =
        DependencyProperty.Register(
            nameof(Status),
            typeof(ConnectionStatus),
            typeof(ConnectionAnimationElement),
            new FrameworkPropertyMetadata(
                ConnectionStatus.Disconnected,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnStatusChanged));

    public ConnectionStatus Status
    {
        get => (ConnectionStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    protected double Progress { get; private set; }
    protected double OrbitDegrees { get; private set; }
    protected double Pulse { get; private set; }
    protected Color AnimationColor => _animatedColor;

    protected virtual void OnStatusChanged(ConnectionStatus oldStatus, ConnectionStatus newStatus)
    {
        _phaseStartedAt = DateTime.UtcNow;
        _phaseStartProgress = Progress;
        _phaseDurationSeconds = newStatus == ConnectionStatus.Disconnecting ? 12 : 15;

        if (newStatus is ConnectionStatus.Connecting or ConnectionStatus.Reconnecting)
        {
            if (Progress <= 0.01 || Progress >= 0.96)
            {
                Progress = 0.06;
                _phaseStartProgress = Progress;
            }
        }
        else if (newStatus == ConnectionStatus.Connected && Progress < 0.82)
        {
            Progress = 0.82;
            _phaseStartProgress = Progress;
        }

        EnsureTimer();
        InvalidateVisual();
    }

    protected bool IsTransitioning => Status is ConnectionStatus.Connecting
        or ConnectionStatus.Reconnecting
        or ConnectionStatus.Disconnecting;

    protected bool ShouldShowProgress => IsTransitioning || Status == ConnectionStatus.Connected && Progress < 0.999;

    protected static Color StatusColor(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Connected => Color.FromRgb(0x10, 0xB9, 0x81),
        ConnectionStatus.Connecting => Color.FromRgb(0xF5, 0x9E, 0x0B),
        ConnectionStatus.Disconnecting => Color.FromRgb(0xF5, 0x9E, 0x0B),
        ConnectionStatus.Reconnecting => Color.FromRgb(0xF5, 0x9E, 0x0B),
        ConnectionStatus.Error => Color.FromRgb(0xEF, 0x44, 0x44),
        _ => Color.FromRgb(0x94, 0xA3, 0xB8)
    };

    protected static Color PrimaryColor => Color.FromRgb(0x15, 0x70, 0xEF);
    protected static Color SurfaceColor => Color.FromRgb(0xF8, 0xFA, 0xFC);

    protected static SolidColorBrush Brush(Color color, double opacity = 1)
    {
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    protected static Pen Pen(Color color, double thickness, double opacity = 1, PenLineCap lineCap = PenLineCap.Round)
    {
        var pen = new Pen(Brush(color, opacity), thickness)
        {
            StartLineCap = lineCap,
            EndLineCap = lineCap
        };
        pen.Freeze();
        return pen;
    }

    protected static Geometry ShieldGeometry()
    {
        var geometry = Geometry.Parse("M50,6 L86,20 V46 C86,68 72,86 50,96 C28,86 14,68 14,46 V20 Z");
        geometry.Freeze();
        return geometry;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!_timer.IsEnabled)
            EnsureTimer();
    }

    private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ConnectionAnimationElement element)
            element.OnStatusChanged((ConnectionStatus)e.OldValue, (ConnectionStatus)e.NewValue);
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = Math.Clamp((now - _lastTick).TotalSeconds, 0.001, 0.05);
        _lastTick = now;

        Pulse = (Math.Sin(Environment.TickCount64 / 1000.0 * Math.PI * 2 / 1.8) + 1) / 2;
        OrbitDegrees = (OrbitDegrees + 112.5 * dt) % 360;
        _animatedColor = LerpColor(_animatedColor, StatusColor(Status), Math.Clamp(dt / 0.42, 0, 1));
        StepProgress(dt);

        InvalidateVisual();

        if (Status is ConnectionStatus.Disconnected or ConnectionStatus.Error && Progress <= 0.001)
            _timer.Stop();
    }

    private void StepProgress(double dt)
    {
        switch (Status)
        {
            case ConnectionStatus.Connecting:
            case ConnectionStatus.Reconnecting:
                var connectElapsed = (DateTime.UtcNow - _phaseStartedAt).TotalSeconds;
                var connectPercent = EaseOutCubic(Math.Clamp(connectElapsed / _phaseDurationSeconds, 0, 1));
                var estimatedProgress = 0.06 + 0.86 * connectPercent;
                Progress = MoveToward(Progress, Math.Max(Progress, estimatedProgress), dt / 0.22);
                break;

            case ConnectionStatus.Connected:
                Progress = MoveToward(Progress, 1, dt / 0.62);
                break;

            case ConnectionStatus.Disconnecting:
                var disconnectElapsed = (DateTime.UtcNow - _phaseStartedAt).TotalSeconds;
                var disconnectPercent = EaseOutCubic(Math.Clamp(disconnectElapsed / _phaseDurationSeconds, 0, 1));
                var disconnectProgress = _phaseStartProgress + (0.04 - _phaseStartProgress) * disconnectPercent;
                Progress = MoveToward(Progress, Math.Clamp(disconnectProgress, 0.04, 1), dt / 0.22);
                break;

            default:
                Progress = MoveToward(Progress, 0, dt / 0.32);
                break;
        }
    }

    private void EnsureTimer()
    {
        if (!IsLoaded)
            return;

        _lastTick = DateTime.UtcNow;
        if (!_timer.IsEnabled)
            _timer.Start();
    }

    private static double MoveToward(double current, double target, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return current + (target - current) * amount;
    }

    private static double EaseOutCubic(double value)
    {
        var inverse = 1 - value;
        return 1 - inverse * inverse * inverse;
    }

    private static Color LerpColor(Color current, Color target, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)(current.R + (target.R - current.R) * amount),
            (byte)(current.G + (target.G - current.G) * amount),
            (byte)(current.B + (target.B - current.B) * amount));
    }
}

public sealed class VpnConnectionShieldControl : ConnectionAnimationElement
{
    private bool _isPressed;

    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(VpnConnectionShieldControl));

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(188, 188);

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        CaptureMouse();
        _isPressed = true;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_isPressed && IsMouseCaptured)
        {
            ReleaseMouseCapture();
            _isPressed = false;
            InvalidateVisual();

            if (Status != ConnectionStatus.Connected && Command?.CanExecute(null) == true)
                Command.Execute(null);
        }

        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_isPressed && !IsMouseCaptured)
        {
            _isPressed = false;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var color = AnimationColor;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var min = Math.Min(ActualWidth, ActualHeight);
        var activePulse = Status switch
        {
            ConnectionStatus.Connecting or ConnectionStatus.Reconnecting or ConnectionStatus.Disconnecting => 1 + Pulse * 0.015,
            ConnectionStatus.Connected => 1 + Pulse * 0.024,
            _ => 1
        };
        var stateScale = Status switch
        {
            ConnectionStatus.Connected => 1.04,
            ConnectionStatus.Connecting or ConnectionStatus.Reconnecting or ConnectionStatus.Disconnecting => 1.01,
            _ => 0.98
        };
        var pressScale = _isPressed ? 0.95 : 1;

        dc.PushTransform(new ScaleTransform(stateScale * activePulse * pressScale, stateScale * activePulse * pressScale, center.X, center.Y));

        var radius = min * 0.47;
        dc.DrawEllipse(
            Brush(color, Status switch
            {
                ConnectionStatus.Connected => 0.10 + Pulse * 0.06,
                ConnectionStatus.Connecting or ConnectionStatus.Reconnecting or ConnectionStatus.Disconnecting => 0.08 + Pulse * 0.08,
                _ => 0.05
            }),
            null,
            center,
            radius,
            radius);

        if (IsTransitioning)
        {
            var haloRadius = min * (0.46 + Pulse * 0.09);
            dc.DrawEllipse(Brush(color, (1 - Pulse) * 0.10), null, center, haloRadius, haloRadius);
        }

        var strokeWidth = min * 0.055;
        var ringRadius = radius - strokeWidth / 2;
        dc.DrawEllipse(null, Pen(color, strokeWidth, 0.13), center, ringRadius, ringRadius);

        if (Status == ConnectionStatus.Connected && Progress >= 0.995)
        {
            dc.DrawEllipse(null, Pen(color, strokeWidth, 0.78, PenLineCap.Flat), center, ringRadius, ringRadius);
            var endPoint = PointOnCircle(center, ringRadius, -90);
            dc.DrawEllipse(Brush(color, 0.34), null, endPoint, strokeWidth * 0.52, strokeWidth * 0.52);
        }
        else if (Progress > 0.003)
        {
            DrawArc(dc, center, ringRadius, -90, Math.Min(358.5, 360 * Progress), Pen(color, strokeWidth, 0.92));
        }

        if (IsTransitioning)
        {
            dc.PushTransform(new RotateTransform(OrbitDegrees, center.X, center.Y));
            DrawArc(dc, center, ringRadius, -110, 92, Pen(PrimaryColor, strokeWidth * 0.9, 0.72));
            dc.Pop();
        }

        var innerRadius = min * 0.34;
        var innerBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.35),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.62,
            RadiusY = 0.62
        };
        innerBrush.GradientStops.Add(new GradientStop(Color.FromArgb(245, SurfaceColor.R, SurfaceColor.G, SurfaceColor.B), 0));
        innerBrush.GradientStops.Add(new GradientStop(Color.FromArgb(42, color.R, color.G, color.B), 0.68));
        innerBrush.GradientStops.Add(new GradientStop(Color.FromArgb(245, SurfaceColor.R, SurfaceColor.G, SurfaceColor.B), 1));
        dc.DrawEllipse(innerBrush, null, center, innerRadius, innerRadius);

        var iconScale = Status == ConnectionStatus.Connected ? 1.05 : Status == ConnectionStatus.Disconnected ? 0.96 : 1;
        dc.PushTransform(new ScaleTransform(iconScale, iconScale, center.X, center.Y));
        var shield = ShieldGeometry();
        var bounds = shield.Bounds;
        var size = min * 0.34;
        dc.PushTransform(new TranslateTransform(center.X - size / 2, center.Y - size / 2));
        dc.PushTransform(new ScaleTransform(size / bounds.Width, size / bounds.Height));
        dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
        dc.DrawGeometry(Brush(color), null, shield);
        dc.Pop();
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private static void DrawArc(DrawingContext dc, Point center, double radius, double startAngle, double sweepAngle, Pen pen)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var start = PointOnCircle(center, radius, startAngle);
            var end = PointOnCircle(center, radius, startAngle + sweepAngle);
            context.BeginFigure(start, false, false);
            context.ArcTo(
                end,
                new Size(radius, radius),
                0,
                Math.Abs(sweepAngle) > 180,
                sweepAngle >= 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                true,
                false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180;
        return new Point(center.X + Math.Cos(radians) * radius, center.Y + Math.Sin(radians) * radius);
    }
}

public sealed class VpnConnectionProgressBarControl : ConnectionAnimationElement
{
    protected override Size MeasureOverride(Size availableSize) => new(224, 10);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        if (!ShouldShowProgress || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var color = Status == ConnectionStatus.Connected ? AnimationColor : PrimaryColor;
        var glowColor = IsTransitioning ? Color.FromRgb(0x38, 0xBD, 0xF8) : AnimationColor;
        var radius = ActualHeight / 2;
        var progressWidth = Math.Max(0, ActualWidth * Math.Clamp(Progress, 0, 1));
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRoundedRectangle(Brush(color, 0.12), null, rect, radius, radius);

        if (progressWidth <= 0)
            return;

        var progressRect = new Rect(0, 0, progressWidth, ActualHeight);
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5)
        };
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(205, PrimaryColor.R, PrimaryColor.G, PrimaryColor.B), 0));
        gradient.GradientStops.Add(new GradientStop(color, 0.54));
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(225, glowColor.R, glowColor.G, glowColor.B), 0.82));
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(185, 255, 255, 255), 1));
        dc.DrawRoundedRectangle(gradient, null, progressRect, radius, radius);

        var end = new Point(Math.Max(radius, progressWidth), radius);
        var cometPulse = IsTransitioning ? 0.75 + Pulse * 0.45 : 0.62;
        dc.DrawEllipse(Brush(glowColor, IsTransitioning ? 0.18 + Pulse * 0.18 : 0.22), null, end, ActualHeight * 2.5 * cometPulse, ActualHeight * 2.5 * cometPulse);
        dc.DrawEllipse(Brush(glowColor, IsTransitioning ? 0.38 + Pulse * 0.22 : 0.30), null, end, ActualHeight * 1.25, ActualHeight * 1.25);
        dc.DrawEllipse(Brush(Colors.White, IsTransitioning ? 0.72 : 0.42), null, end, ActualHeight * 0.46, ActualHeight * 0.46);

        if (IsTransitioning && progressWidth > ActualHeight)
        {
            var shimmerWidth = ActualWidth * 0.22;
            var shimmerStart = (progressWidth + shimmerWidth) * Pulse - shimmerWidth;
            var shimmer = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5)
            };
            shimmer.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            shimmer.GradientStops.Add(new GradientStop(Color.FromArgb(100, 255, 255, 255), 0.5));
            shimmer.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            dc.DrawRoundedRectangle(shimmer, null, new Rect(shimmerStart, 0, shimmerWidth, ActualHeight), radius, radius);
        }
    }
}
