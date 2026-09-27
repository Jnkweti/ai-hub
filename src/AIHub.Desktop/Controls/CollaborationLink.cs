using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AIHub.Desktop.Controls;

/// <summary>A short, directional signal for actual peer dispatches; idle is entirely static.</summary>
public sealed class CollaborationLink : FrameworkElement
{
    private Window? owner;
    private bool towardClaude;
    private static readonly Brush Codex = Frozen("#82DFD1"), Claude = Frozen("#EAB38B"), Line = Frozen("#41606B"), Center = Frozen("#12232A");
    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(CollaborationLink), new FrameworkPropertyMetadata(-1d, FrameworkPropertyMetadataOptions.AffectsRender));
    private double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    public CollaborationLink()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => { owner = Window.GetWindow(this); if (owner is not null) owner.StateChanged += StateChanged; Motion.Changed += Stop; };
        Unloaded += (_, _) => { Stop(); Motion.Changed -= Stop; if (owner is not null) owner.StateChanged -= StateChanged; owner = null; };
        IsVisibleChanged += (_, _) => { if (!IsVisible) Stop(); };
    }
    private void StateChanged(object? sender, EventArgs e) { if (owner?.WindowState == WindowState.Minimized) Stop(); }
    public void Stop() { BeginAnimation(ProgressProperty, null); Progress = -1; }
    public void Send(bool toClaude)
    {
        Stop(); towardClaude = toClaude;
        if (!IsVisible || !Motion.Enabled || owner?.WindowState == WindowState.Minimized) return;
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(850)) { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        Timeline.SetDesiredFrameRate(animation, 30);
        BeginAnimation(ProgressProperty, animation);
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var y = ActualHeight / 2; var left = 6d; var right = Math.Max(left, ActualWidth - 6); var center = (left + right) / 2;
        var pen = new Pen(Line, 1);
        dc.DrawLine(pen, new(left, y), new(right, y));
        dc.DrawEllipse(Codex, null, new(left, y), 2, 2);
        dc.DrawEllipse(Claude, null, new(right, y), 2, 2);
        dc.DrawEllipse(Center, pen, new(center, y), 8, 8);
        dc.DrawLine(new Pen(Codex, 1), new(center - 3, y), new(center + 3, y));
        if (Progress is >= 0 and <= 1)
        {
            var x = towardClaude ? left + (right - left) * Progress : right - (right - left) * Progress;
            var brush = towardClaude ? Codex : Claude;
            dc.PushOpacity(.15); dc.DrawEllipse(brush, null, new(x, y), 9, 9); dc.Pop();
            dc.DrawEllipse(brush, null, new(x, y), 3, 3);
        }
    }
    private static Brush Frozen(string value) { var brush = (Brush)new BrushConverter().ConvertFromString(value)!; brush.Freeze(); return brush; }
}
