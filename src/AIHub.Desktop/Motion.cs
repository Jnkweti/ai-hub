using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AIHub.Desktop;

public static class Motion
{
    private static bool reduced;
    public static bool Enabled => !reduced && SystemParameters.ClientAreaAnimation;
    public static event Action? Changed;
    public static void Configure(bool reduceMotion) { reduced = reduceMotion; Changed?.Invoke(); }

    public static readonly DependencyProperty EntranceProperty = DependencyProperty.RegisterAttached(
        "Entrance", typeof(bool), typeof(Motion), new PropertyMetadata(false, (d, e) =>
        {
            if (d is not FrameworkElement element) return;
            if ((bool)e.NewValue) element.Loaded += Loaded; else element.Loaded -= Loaded;
        }));
    public static bool GetEntrance(DependencyObject value) => (bool)value.GetValue(EntranceProperty);
    public static void SetEntrance(DependencyObject value, bool entrance) => value.SetValue(EntranceProperty, entrance);
    private static void Loaded(object sender, RoutedEventArgs e) => Enter((FrameworkElement)sender);

    public static readonly DependencyProperty InteractiveProperty = DependencyProperty.RegisterAttached(
        "Interactive", typeof(bool), typeof(Motion), new PropertyMetadata(false, (d, e) =>
        {
            if (d is not Control c) return;
            if ((bool)e.NewValue) { c.MouseEnter += Hover; c.MouseLeave += Leave; }
            else { c.MouseEnter -= Hover; c.MouseLeave -= Leave; }
        }));
    public static bool GetInteractive(DependencyObject value) => (bool)value.GetValue(InteractiveProperty);
    public static void SetInteractive(DependencyObject value, bool interactive) => value.SetValue(InteractiveProperty, interactive);
    private static void Hover(object sender, MouseEventArgs e) => HoverOpacity((Control)sender, .09);
    private static void Leave(object sender, MouseEventArgs e) => HoverOpacity((Control)sender, 0);
    private static void HoverOpacity(Control control, double opacity)
    {
        if (control.Template.FindName("HoverLayer", control) is FrameworkElement layer)
            layer.BeginAnimation(UIElement.OpacityProperty, Tween(opacity, 160));
    }
    public static DoubleAnimation Tween(double to, int milliseconds, double? from = null)
    {
        var animation = new DoubleAnimation { To = to, From = from, Duration = TimeSpan.FromMilliseconds(Enabled ? milliseconds : 0), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Timeline.SetDesiredFrameRate(animation, 30);
        return animation;
    }
    public static void Enter(FrameworkElement element, int milliseconds = 300, int delay = 0)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        var slide = new TranslateTransform(); element.RenderTransform = slide;
        if (!Enabled) return;
        var fade = Tween(1, milliseconds, 0); fade.BeginTime = TimeSpan.FromMilliseconds(delay);
        var move = Tween(0, milliseconds, 8); move.BeginTime = fade.BeginTime;
        element.Opacity = 0; slide.Y = 8;
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        slide.BeginAnimation(TranslateTransform.YProperty, move);
    }
    public static void Pulse(FrameworkElement element, bool active)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = active ? 1 : .4;
        if (!active || !Enabled) return;
        var animation = new DoubleAnimation(.3, 1, TimeSpan.FromMilliseconds(1050)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(animation, 30);
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }
}
