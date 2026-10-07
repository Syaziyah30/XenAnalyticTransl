using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace XenAnalyticTransl.Studio;

/// <summary>
/// Paints every find match inside a TextBox, the way an editor does.
///
/// A WPF TextBox can only SELECT one range, so showing all matches at once has to
/// happen on the adorner layer - a transparent sheet over the control that we draw
/// rectangles on. The rectangles come from GetRectFromCharacterIndex, which already
/// accounts for scrolling, so the highlights track the text as it moves.
///
/// Never hit-testable: clicks and the caret must pass straight through.
/// </summary>
public sealed class FindHighlightAdorner : Adorner
{
    private static readonly Brush Other = new SolidColorBrush(Color.FromArgb(0x66, 0xF7, 0xD6, 0x6E));
    private static readonly Brush Current = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0x9A, 0x2E));
    private static readonly Pen CurrentEdge = new(new SolidColorBrush(Color.FromRgb(0xD9, 0x73, 0x06)), 1);

    private readonly TextBox _box;
    private IReadOnlyList<int> _starts = [];
    private int _length;
    private int _current = -1;

    static FindHighlightAdorner()
    {
        Other.Freeze();
        Current.Freeze();
        CurrentEdge.Freeze();
    }

    public FindHighlightAdorner(TextBox box) : base(box)
    {
        _box = box;
        IsHitTestVisible = false;

        // The rectangles are in viewport coordinates, so they go stale on scroll,
        // on resize, and whenever the text is replaced.
        _box.AddHandler(ScrollViewer.ScrollChangedEvent,
                        new ScrollChangedEventHandler((_, _) => InvalidateVisual()));
        _box.TextChanged += (_, _) => InvalidateVisual();
        _box.SizeChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>Character offsets of each match, all of the same length.</summary>
    public void SetMatches(IReadOnlyList<int> starts, int length, int current)
    {
        _starts = starts;
        _length = length;
        _current = current;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_length <= 0 || _starts.Count == 0) return;

        var view = new Rect(0, 0, _box.ActualWidth, _box.ActualHeight);

        for (var i = 0; i < _starts.Count; i++)
        {
            var rect = SpanRect(_starts[i]);
            if (rect is null) continue;

            // Off-screen matches report rectangles outside the control.
            if (!view.IntersectsWith(rect.Value)) continue;

            var isCurrent = i == _current;
            dc.DrawRectangle(isCurrent ? Current : Other,
                             isCurrent ? CurrentEdge : null,
                             Rect.Intersect(rect.Value, view));
        }
    }

    /// <summary>
    /// The box around one match. Returns null when the position cannot be resolved,
    /// which happens while the control is still laying out.
    /// </summary>
    private Rect? SpanRect(int start)
    {
        var end = start + _length;
        if (end > _box.Text.Length) return null;

        var a = _box.GetRectFromCharacterIndex(start);
        var b = _box.GetRectFromCharacterIndex(end);
        if (a.IsEmpty || b.IsEmpty) return null;

        // A match split across a wrap gives two different lines. Highlight the part
        // on the first line rather than painting a band across the whole control.
        if (System.Math.Abs(b.Top - a.Top) > 0.5)
            b = new Rect(_box.ActualWidth, a.Top, 0, a.Height);

        var width = System.Math.Max(2, b.Left - a.Left);
        return new Rect(a.Left, a.Top, width, a.Height > 0 ? a.Height : b.Height);
    }
}

/// <summary>
/// The same idea for the scenario editor, which is a RichTextBox and so has no
/// character-index API. Offsets are resolved against the flow document each time it
/// renders, because the editor rebuilds its document on every re-colour and any
/// TextPointer cached across that would be stale.
/// </summary>
public sealed class RichFindAdorner : Adorner
{
    private static readonly Brush Other = new SolidColorBrush(Color.FromArgb(0x66, 0xF7, 0xD6, 0x6E));
    private static readonly Brush Current = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0x9A, 0x2E));
    private static readonly Pen CurrentEdge = new(new SolidColorBrush(Color.FromRgb(0xD9, 0x73, 0x06)), 1);

    private readonly RichTextBox _box;
    private IReadOnlyList<int> _starts = [];
    private int _length;
    private int _current = -1;

    static RichFindAdorner()
    {
        Other.Freeze();
        Current.Freeze();
        CurrentEdge.Freeze();
    }

    public RichFindAdorner(RichTextBox box) : base(box)
    {
        _box = box;
        IsHitTestVisible = false;

        _box.AddHandler(ScrollViewer.ScrollChangedEvent,
                        new ScrollChangedEventHandler((_, _) => InvalidateVisual()));
        _box.TextChanged += (_, _) => InvalidateVisual();
        _box.SizeChanged += (_, _) => InvalidateVisual();
    }

    public void SetMatches(IReadOnlyList<int> starts, int length, int current)
    {
        _starts = starts;
        _length = length;
        _current = current;
        InvalidateVisual();
    }

    /// <summary>
    /// The document's text as a single run of characters. Offsets used for matching must
    /// be built from this same walk, or they will not line up with the pointers below.
    /// </summary>
    public static string RunText(FlowDocument doc)
    {
        var sb = new System.Text.StringBuilder();
        for (var p = doc.ContentStart; p is not null; p = p.GetNextContextPosition(LogicalDirection.Forward))
            if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                sb.Append(p.GetTextInRun(LogicalDirection.Forward));
        return sb.ToString();
    }

    private static TextPointer? At(FlowDocument doc, int offset)
    {
        var seen = 0;
        for (var p = doc.ContentStart; p is not null; p = p.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (p.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text) continue;

            var run = p.GetTextInRun(LogicalDirection.Forward).Length;
            if (seen + run >= offset) return p.GetPositionAtOffset(offset - seen);
            seen += run;
        }
        return null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_length <= 0 || _starts.Count == 0) return;

        var view = new Rect(0, 0, _box.ActualWidth, _box.ActualHeight);

        for (var i = 0; i < _starts.Count; i++)
        {
            var a = At(_box.Document, _starts[i]);
            var b = At(_box.Document, _starts[i] + _length);
            if (a is null || b is null) continue;

            var ra = a.GetCharacterRect(LogicalDirection.Forward);
            var rb = b.GetCharacterRect(LogicalDirection.Forward);
            if (ra.IsEmpty) continue;

            // Wrapped match: highlight the first line rather than banding the control.
            if (System.Math.Abs(rb.Top - ra.Top) > 0.5)
                rb = new Rect(_box.ActualWidth, ra.Top, 0, ra.Height);

            var rect = new Rect(ra.Left, ra.Top,
                                System.Math.Max(2, rb.Left - ra.Left),
                                ra.Height > 0 ? ra.Height : rb.Height);

            if (!view.IntersectsWith(rect)) continue;

            var isCurrent = i == _current;
            dc.DrawRectangle(isCurrent ? Current : Other,
                             isCurrent ? CurrentEdge : null,
                             Rect.Intersect(rect, view));
        }
    }
}

/// <summary>
/// Attaches one <see cref="FindHighlightAdorner"/> per TextBox and keeps it current.
/// </summary>
public static class FindHighlighter
{
    private static readonly Dictionary<TextBox, FindHighlightAdorner> Map = new();
    private static readonly Dictionary<RichTextBox, RichFindAdorner> RichMap = new();

    public static void Show(RichTextBox box, IReadOnlyList<int> starts, int length, int current)
    {
        if (!RichMap.TryGetValue(box, out var adorner))
        {
            var layer = AdornerLayer.GetAdornerLayer(box);
            if (layer is null) return;

            adorner = new RichFindAdorner(box);
            layer.Add(adorner);
            RichMap[box] = adorner;
        }

        adorner.SetMatches(starts, length, current);
    }

    public static void Clear(RichTextBox box)
    {
        if (RichMap.TryGetValue(box, out var adorner))
            adorner.SetMatches([], 0, -1);
    }

    public static void Show(TextBox box, IReadOnlyList<int> starts, int length, int current)
    {
        if (!Map.TryGetValue(box, out var adorner))
        {
            var layer = AdornerLayer.GetAdornerLayer(box);
            if (layer is null) return;              // not in the visual tree yet

            adorner = new FindHighlightAdorner(box);
            layer.Add(adorner);
            Map[box] = adorner;
        }

        adorner.SetMatches(starts, length, current);
    }

    public static void Clear(TextBox box)
    {
        if (Map.TryGetValue(box, out var adorner))
            adorner.SetMatches([], 0, -1);
    }

    public static void ClearAll(params TextBox[] boxes)
    {
        foreach (var b in boxes) Clear(b);
    }
}
