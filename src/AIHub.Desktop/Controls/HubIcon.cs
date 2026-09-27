using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;

namespace AIHub.Desktop.Controls;

/// <summary>Renders the bundled Lucide geometry directly, with no browser or image scaling.</summary>
public sealed class HubIcon : FrameworkElement
{
    private static readonly Dictionary<string, Geometry> Cache = [];
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(HubIcon), new FrameworkPropertyMetadata("circle-dot", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(HubIcon), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public HubIcon() { Width = 18; Height = 18; IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!Cache.TryGetValue(Kind, out var geometry))
        {
            var resource = Application.GetResourceStream(new Uri("/AI Hub;component/Assets/Icons/" + Kind + ".svg", UriKind.Relative));
            if (resource is null) return;
            using var stream = resource.Stream;
            var svg = XDocument.Load(stream); var group = new GeometryGroup();
            foreach (var item in svg.Root!.Elements())
            {
                double N(string name) => double.Parse(item.Attribute(name)?.Value ?? "0", CultureInfo.InvariantCulture);
                Geometry? shape = item.Name.LocalName switch
                {
                    "path" => Geometry.Parse(item.Attribute("d")!.Value),
                    "circle" => new EllipseGeometry(new Point(N("cx"), N("cy")), N("r"), N("r")),
                    "rect" => new RectangleGeometry(new Rect(N("x"), N("y"), N("width"), N("height")), N("rx"), item.Attribute("ry") is null ? N("rx") : N("ry")),
                    _ => null
                };
                if (shape is not null) group.Children.Add(shape);
            }
            group.Freeze(); geometry = group; Cache[Kind] = geometry;
        }
        var pen = new Pen(Foreground, 1.65) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        dc.DrawGeometry(null, pen, geometry); dc.Pop();
    }
}
