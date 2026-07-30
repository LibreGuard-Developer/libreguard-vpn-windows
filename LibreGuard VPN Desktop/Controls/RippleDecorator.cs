using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace LibreGuard_VPN_Desktop.Controls;

/// <summary>
/// A ContentControl that renders a Material-style ink-ripple animation on each mouse click.
/// Wrap any card or graph container with this control to get ripple feedback.
/// </summary>
internal class RippleDecorator : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(RippleDecorator),
            new FrameworkPropertyMetadata(new CornerRadius(0)));

    /// <summary>Corner radius forwarded to the template Border.</summary>
    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public static readonly DependencyProperty RippleColorProperty =
        DependencyProperty.Register(nameof(RippleColor), typeof(Color), typeof(RippleDecorator),
            new FrameworkPropertyMetadata(Color.FromArgb(50, 21, 112, 239)));

    /// <summary>Color of the expanding ripple circle. Default is a semi-transparent brand blue.</summary>
    public Color RippleColor
    {
        get => (Color)GetValue(RippleColorProperty);
        set => SetValue(RippleColorProperty, value);
    }

    private Canvas? _rippleCanvas;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _rippleCanvas = GetTemplateChild("PART_RippleCanvas") as Canvas;
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        SpawnRipple(e.GetPosition(this));
    }

    private void SpawnRipple(Point origin)
    {
        if (_rippleCanvas is null) return;

        var diameter = Math.Max(ActualWidth, ActualHeight) * 2.2;
        var ripple = new Ellipse
        {
            Width = diameter,
            Height = diameter,
            Fill = new SolidColorBrush(RippleColor),
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0, 0),
        };

        Canvas.SetLeft(ripple, origin.X - diameter / 2);
        Canvas.SetTop(ripple, origin.Y - diameter / 2);
        _rippleCanvas.Children.Add(ripple);

        var duration = new Duration(TimeSpan.FromMilliseconds(550));
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        var scaleAnim = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
        var fadeAnim = new DoubleAnimation(0.55, 0, duration) { EasingFunction = ease };

        fadeAnim.Completed += (_, _) => _rippleCanvas.Children.Remove(ripple);

        var transform = (ScaleTransform)ripple.RenderTransform;
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        ripple.BeginAnimation(OpacityProperty, fadeAnim);
    }
}
