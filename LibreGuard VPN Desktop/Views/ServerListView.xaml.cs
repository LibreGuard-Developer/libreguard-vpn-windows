using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.ViewModels;

namespace LibreGuard_VPN_Desktop.Views;

public partial class ServerListView : UserControl
{
    public ServerListView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ServerListViewModel vm)
        {
            vm.FavoriteServers.CollectionChanged += OnFavoriteServersChanged;
            await vm.LoadServersCommand.ExecuteAsync(null);
        }
    }

    // ── Section appear ──────────────────────────────────────────────────────

    private void OnFavoriteServersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Animate the whole Favourites section into view the first time a card is added
        if (e.Action == NotifyCollectionChangedAction.Add
            && DataContext is ServerListViewModel vm
            && vm.FavoriteServers.Count == 1)
        {
            // Queue after the DataTrigger visibility update has been rendered
            Dispatcher.BeginInvoke(DispatcherPriority.Render, AnimateSectionAppear);
        }
    }

    private void AnimateSectionAppear()
    {
        FavouritesSection.Opacity = 0;
        FavouritesSection.RenderTransformOrigin = new Point(0.5, 0);
        FavouritesSection.RenderTransform = new TranslateTransform(0, -18);

        FavouritesSection.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360)));

        ((TranslateTransform)FavouritesSection.RenderTransform).BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(-18, 0, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }
            });
    }

    // ── All-Servers star click ───────────────────────────────────────────────
    // The Command binding on this button handles the data toggle;
    // this handler runs the visual particle only.

    private void AllServersStarButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ServerLocation server)
            return;

        // Click fires BEFORE the command, so IsFavorite still reflects the PRE-toggle state.
        var from = button.TranslatePoint(
            new Point(button.ActualWidth / 2, button.ActualHeight / 2), AnimationOverlay);

        if (!server.IsFavorite)
        {
            // Adding — arc upward to the Favourites section heading star
            var to = FavouritesSection.Visibility == Visibility.Visible
                ? FavouritesStar.TranslatePoint(
                    new Point(FavouritesStar.ActualWidth / 2, FavouritesStar.ActualHeight / 2),
                    AnimationOverlay)
                : new Point(from.X - 30, 110); // section not yet visible: approximate position

            LaunchOrb(from, to);
        }
        else
        {
            // Removing via the blue star in All Servers — scatter the star off to the side
            LaunchParticle(from, new Point(from.X + 55, from.Y - 22), true);
        }

        PulseButton(button);
    }

    // ── Favourites star click ────────────────────────────────────────────────
    // No Command binding on this button — we animate the card out first,
    // then call the command to remove the item from the collection.

    private async void FavouritesStarButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ServerLocation server)
            return;

        // Launch dimming particle upward
        var from = button.TranslatePoint(
            new Point(button.ActualWidth / 2, button.ActualHeight / 2), AnimationOverlay);
        LaunchParticle(from, new Point(from.X + 28, from.Y - 55), true);

        // Animate the card container out before the item is removed from the collection
        var container = FavouritesItemsControl.ItemContainerGenerator
            .ContainerFromItem(server) as ContentPresenter;

        var animDuration = TimeSpan.FromMilliseconds(260);

        if (container is not null)
        {
            container.RenderTransformOrigin = new Point(0.5, 0.5);
            var scale = new ScaleTransform(1, 1);
            var translate = new TranslateTransform(0, 0);
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(translate);
            container.RenderTransform = group;

            var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, 0.82, animDuration) { EasingFunction = easeIn });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, 0.82, animDuration) { EasingFunction = easeIn });
            translate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, -12, animDuration));
            container.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, 0, animDuration));

            await Task.Delay(animDuration);
        }

        // Guard against a rapid double-click that may have already toggled it back
        if (server.IsFavorite && DataContext is ServerListViewModel vm)
            vm.ToggleFavoriteCommand.Execute(server);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Spawns a ★ particle on the overlay Canvas that arcs from <paramref name="from"/>
    /// to <paramref name="to"/> and fades out.
    /// </summary>
    private void LaunchParticle(Point from, Point to, bool dim)
    {
        var star = new TextBlock
        {
            Text = "★",
            FontSize = 20,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = new SolidColorBrush(dim
                ? Color.FromRgb(148, 163, 184)   // muted slate
                : Color.FromRgb(21, 112, 239)),   // #1570EF primary blue
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };

        var scale = new ScaleTransform(0.35, 0.35);
        var translate = new TranslateTransform(0, 0);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(translate);
        star.RenderTransform = group;

        Canvas.SetLeft(star, from.X - 10);
        Canvas.SetTop(star, from.Y - 10);
        AnimationOverlay.Children.Add(star);

        var totalDuration = new Duration(TimeSpan.FromMilliseconds(520));

        // Scale: burst to 1.5× then shrink to 0.65× as it arrives
        var scaleAnim = new DoubleAnimationUsingKeyFrames { Duration = totalDuration };
        scaleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.35, KeyTime.FromPercent(0)));
        scaleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.5, KeyTime.FromPercent(0.22))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } });
        scaleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.65, KeyTime.FromPercent(1.0)));

        // X: straight line with cubic ease-in-out
        var translateX = new DoubleAnimation(0, to.X - from.X, totalDuration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };

        // Y: parabolic arc — lift slightly in the middle for a natural flight curve
        var dy = to.Y - from.Y;
        var arcLift = dim ? Math.Abs(dy) * 0.15 : -(Math.Max(28, Math.Abs(dy) * 0.22));
        var translateY = new DoubleAnimationUsingKeyFrames { Duration = totalDuration };
        translateY.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        translateY.KeyFrames.Add(new EasingDoubleKeyFrame(dy / 2 + arcLift, KeyTime.FromPercent(0.42))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        translateY.KeyFrames.Add(new EasingDoubleKeyFrame(dy, KeyTime.FromPercent(1.0))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });

        // Opacity: quick fade-in → hold → fade-out at the end
        var opacity = new DoubleAnimationUsingKeyFrames { Duration = totalDuration };
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.13)));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.74)));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1.0)));
        opacity.Completed += (s, _) => AnimationOverlay.Children.Remove(star);

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim.Clone());
        translate.BeginAnimation(TranslateTransform.XProperty, translateX);
        translate.BeginAnimation(TranslateTransform.YProperty, translateY);
        star.BeginAnimation(OpacityProperty, opacity);
    }

    // ── Add-to-favourites orb animation ────────────────────────────────────

    /// <summary>
    /// Glides a small glowing orb from <paramref name="from"/> to <paramref name="to"/>
    /// along a gentle arc, and emits a ripple ring at the source for tactile feedback.
    /// </summary>
    private void LaunchOrb(Point from, Point to)
    {
        var orb = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = new SolidColorBrush(Color.FromArgb(220, 21, 112, 239)),
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(21, 112, 239),
                BlurRadius = 8,
                ShadowDepth = 0,
                Opacity = 0.7
            }
        };

        var translate = new TranslateTransform(0, 0);
        orb.RenderTransform = translate;
        Canvas.SetLeft(orb, from.X - 5);
        Canvas.SetTop(orb, from.Y - 5);
        AnimationOverlay.Children.Add(orb);

        var duration = new Duration(TimeSpan.FromMilliseconds(480));

        // X: smooth glide
        translate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, to.X - from.X, duration)
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });

        // Y: gentle arc
        var dy = to.Y - from.Y;
        var arcLift = -(Math.Max(20, Math.Abs(dy) * 0.18));
        var translateY = new DoubleAnimationUsingKeyFrames { Duration = duration };
        translateY.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        translateY.KeyFrames.Add(new EasingDoubleKeyFrame(dy / 2 + arcLift, KeyTime.FromPercent(0.45))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        translateY.KeyFrames.Add(new EasingDoubleKeyFrame(dy, KeyTime.FromPercent(1.0))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        translate.BeginAnimation(TranslateTransform.YProperty, translateY);

        // Opacity: fade in → hold → fade out near target
        var opacity = new DoubleAnimationUsingKeyFrames { Duration = duration };
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.15)));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.72)));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1.0)));
        opacity.Completed += (_, _) => AnimationOverlay.Children.Remove(orb);
        orb.BeginAnimation(OpacityProperty, opacity);

        LaunchRipple(from);
    }

    /// <summary>
    /// Emits an expanding ring at <paramref name="center"/> that fades out.
    /// </summary>
    private void LaunchRipple(Point center)
    {
        var ring = new Ellipse
        {
            Width = 16,
            Height = 16,
            Stroke = new SolidColorBrush(Color.FromRgb(21, 112, 239)),
            StrokeThickness = 1.5,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };

        var scale = new ScaleTransform(0.5, 0.5);
        ring.RenderTransform = scale;
        Canvas.SetLeft(ring, center.X - 8);
        Canvas.SetTop(ring, center.Y - 8);
        AnimationOverlay.Children.Add(ring);

        var duration = new Duration(TimeSpan.FromMilliseconds(350));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var scaleAnim = new DoubleAnimation(0.5, 2.2, duration) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim.Clone());

        var opacityAnim = new DoubleAnimationUsingKeyFrames { Duration = duration };
        opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.65, KeyTime.FromPercent(0)));
        opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1.0)));
        opacityAnim.Completed += (_, _) => AnimationOverlay.Children.Remove(ring);
        ring.BeginAnimation(OpacityProperty, opacityAnim);
    }

    /// <summary>
    /// Quick scale-pop on the star button to give tactile click feedback.
    /// </summary>
    private static void PulseButton(Button button)
    {
        button.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1, 1);
        button.RenderTransform = scale;

        var pulse = new DoubleAnimationUsingKeyFrames();
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.35, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90)))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } });
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse.Clone());
    }
}
