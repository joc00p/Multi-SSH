using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MultiSSH.Views;

/// <summary>
/// A horizontal insertion line drawn at the top or bottom edge of a tree row,
/// showing where a dragged session will land when reordering.
/// </summary>
public sealed class InsertionAdorner : Adorner
{
    private static readonly Pen LinePen = MakePen();
    private static readonly Brush CapBrush = LinePen.Brush;

    private bool _atBottom;
    public bool AtBottom
    {
        get => _atBottom;
        set { if (_atBottom != value) { _atBottom = value; InvalidateVisual(); } }
    }

    public InsertionAdorner(UIElement adorned) : base(adorned)
    {
        IsHitTestVisible = false;   // never swallow drag events
    }

    private static Pen MakePen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x3B, 0x9E, 0xFF)), 2);
        pen.Freeze();
        return pen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double y = AtBottom ? AdornedElement.RenderSize.Height : 0;
        double w = AdornedElement.RenderSize.Width;
        dc.DrawLine(LinePen, new Point(0, y), new Point(w, y));
        dc.DrawEllipse(CapBrush, null, new Point(0, y), 2.5, 2.5);
        dc.DrawEllipse(CapBrush, null, new Point(w, y), 2.5, 2.5);
    }
}
