namespace SuperHtmlToPdf;

internal sealed partial class LayoutEngine(RenderAssets assets, Action<string, string> warn, CancellationToken cancellationToken)
{
    private sealed class Box(Element? element, double width)
    {
        public Element? Element { get; } = element;
        public double Width { get; set; } = width;
        public double Height
        {
            get; set;
        }
        public bool KeepTogether
        {
            get; set;
        }
        public bool IsLine
        {
            get; set;
        }
        public (double Top, double Bottom)? TableHeader
        {
            get; set;
        }
        public List<(double X, double Y, Box Child)> Children { get; } = [];
        public List<Drawing> Ink { get; } = [];
        public List<Interval> PreferredUnbroken { get; } = [];
        public void Place(Box child, double x, double y) => Children.Add((x, y, child));
    }

    private double sheetHeight;
    private long placementChecks;
    private readonly Dictionary<(Element, bool), double> intrinsicCache = [];
    private readonly HashSet<string> issuedWarnings = [];
    private const double Epsilon = 0.001;
    private void Warning(string code, string message)
    {
        if (issuedWarnings.Add(code))
        {
            warn(code, message);
        }
    }
    private void CountPlacement()
    {
        if ((++placementChecks & 1023) != 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (placementChecks > 12_000_000)
        {
            throw new InvalidDataException("Table or grid placement exceeds the operation limit.");
        }
    }

    private static string Display(Element e) => e.Css("display", e.Tag switch
    {
        "#text" or "a" or "span" or "strong" or "em" or "b" or "i" or "img" or "br" or "small" => "inline",
        "head" or "script" or "style" or "title" or "link" or "meta" or "template" => "none",
        "table" => "table",
        "tr" => "table-row",
        "td" or "th" => "table-cell",
        _ => "block"
    });

    private static bool Hidden(Element el) => Display(el) == "none" || el.Attributes.ContainsKey("hidden");
    private static bool Inline(Element el) => el.Tag == "#text" || Display(el).StartsWith("inline", StringComparison.Ordinal);
    private static double Sizing(Element el, string property, double available, double fallback)
        => LayoutValues.Length(el.Css(property), available, el.FontSize(), fallback);

    private Box Build(Element el, double available, double? imposedWidth = null, int depth = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > 192)
        {
            throw new InvalidOperationException("Layout nesting exceeds 192 levels.");
        }

        available = Math.Max(0, available);
        var margin = el.Edges("margin", available);
        var border = el.Edges("border", available);
        var padding = el.Edges("padding", available);
        var edges = border.Horizontal + padding.Horizontal;
        var verticalEdges = border.Vertical + padding.Vertical;
        var borderBox = el.Css("box-sizing") == "border-box";
        var width = Sizing(el, "width", available, double.NaN);
        width = double.IsNaN(width) ? Math.Max(0, available - margin.Horizontal) : width + (borderBox ? 0 : edges);
        var min = Sizing(el, "min-width", available, 0);
        var max = Sizing(el, "max-width", available, double.PositiveInfinity);
        width = Math.Max(edges, imposedWidth ?? Math.Max(min + (borderBox ? 0 : edges), Math.Min(width, max + (borderBox ? 0 : edges))));
        var box = new Box(el, width) { KeepTogether = el.Css("break-inside").Contains("avoid", StringComparison.Ordinal) };
        if (Hidden(el))
        {
            return box;
        }

        CheckProfile(el);
        var inner = Math.Max(0, width - edges);
        var content = new Box(null, inner);
        var display = Display(el);
        if (el.Tag == "img")
        {
            Image(el, content, inner);
        }
        else if (display is "table" or "inline-table")
        {
            Table(el, content, depth + 1);
        }
        else if (display is "flex" or "inline-flex")
        {
            Flex(el, content, depth + 1);
        }
        else if (display is "grid" or "inline-grid")
        {
            Grid(el, content, depth + 1);
        }
        else if (el.Tag == "#text")
        {
            InlineFlow([el], el, content, 0, [], depth + 1);
        }
        else
        {
            Flow(el, content, depth + 1);
        }

        box.Place(content, border.Left + padding.Left, border.Top + padding.Top);
        var natural = content.Height + verticalEdges;
        // Percent heights require a definite containing block height. In normal printed flow it is indefinite.
        var height = UsedHeight(el, "height", natural, verticalEdges);
        var minHeight = UsedHeight(el, "min-height", 0, verticalEdges);
        var maxHeight = UsedHeight(el, "max-height", double.PositiveInfinity, verticalEdges);
        box.Height = Math.Max(verticalEdges, Math.Max(minHeight, Math.Min(maxHeight, height)));
        return box;
    }

    private void CheckProfile(Element el)
    {
        var radius = el.Css("border-radius");
        if (radius.Length != 0 && radius != "0" && LayoutValues.SplitTracks(radius).Any(v => !v.StartsWith('0') || LayoutValues.Length(v, 100, el.FontSize()) > 0))
        {
            Warning("css-border-radius", "Rounded border corners are painted as square corners.");
        }

        if (el.Css("overflow") is "hidden" or "clip" or "scroll" or "auto" ||
            el.Css("overflow-x") is "hidden" or "clip" or "scroll" or "auto" || el.Css("overflow-y") is "hidden" or "clip" or "scroll" or "auto")
        {
            Warning("css-overflow-clip", "Overflow is visible inside the printable area; arbitrary CSS clipping and scrollports are unavailable.");
        }

        if (el.Css("position") is "fixed" or "sticky")
        {
            Warning("css-scrolling-position", "Sticky positioning and repetition of fixed-position boxes on each page are unavailable.");
        }

        if (el.Css("direction") == "rtl")
        {
            Warning("css-bidi", "The Latin text profile lays out runs from left to right.");
        }

        if (el.Css("align-items").Contains("baseline", StringComparison.Ordinal) || el.Css("align-self").Contains("baseline", StringComparison.Ordinal))
        {
            Warning("css-baseline-alignment", "Flex and grid baseline alignment falls back to start alignment.");
        }

        if (el.Css("grid-template-areas").Length != 0 && el.Css("grid-template-areas") != "none" || el.Css("grid-area").Length != 0)
        {
            Warning("css-grid-areas", "Named grid areas are unavailable. Use grid-row and grid-column with numeric lines or spans.");
        }

        if (new[] { "grid-template-columns", "grid-template-rows" }.Any(k => el.Css(k).Contains('[') || el.Css(k).Contains("subgrid", StringComparison.Ordinal)))
        {
            Warning("css-grid-named-tracks", "Named grid lines and subgrid are unavailable.");
        }

        if (el.Css("grid-template-columns").Contains("auto-fit", StringComparison.Ordinal) || el.Css("grid-template-rows").Contains("auto-fit", StringComparison.Ordinal))
        {
            Warning("css-grid-auto-fit", "An auto-fit grid retains empty tracks as auto-fill does.");
        }

        if (new[] { "border-left-style", "border-right-style", "border-top-style", "border-bottom-style" }
            .Any(k => el.Css(k) is "dashed" or "dotted" or "double" or "inset" or "outset" or "ridge" or "groove"))
        {
            Warning("css-border-style", "Decorative border patterns are painted as solid borders.");
        }

        if (el.Css("break-before") is "left" or "right" or "verso" or "recto" || el.Css("break-after") is "left" or "right" or "verso" or "recto")
        {
            Warning("css-page-parity", "A side-specific break starts a new page without inserting a parity filler page.");
        }
    }

    private static double UsedHeight(Element el, string property, double fallback, double edges)
    {
        if (el.Css(property).Contains('%'))
        {
            return fallback;
        }

        var val = Sizing(el, property, 0, double.NaN);
        return double.IsNaN(val) ? fallback : val + (el.Css("box-sizing") == "border-box" ? 0 : edges);
    }

    private sealed record FloatArea(bool Left, double X, double Y, double Width, double Bottom);
    private static (double Left, double Right) FreeSpace(List<FloatArea> floats, double y, double height, double width)
    {
        var left = 0d;
        var right = width;
        foreach (var f in floats)
        {
            if (f.Bottom <= y + Epsilon || f.Y >= y + height - Epsilon)
            {
                continue;
            }

            if (f.Left)
            {
                left = Math.Max(left, f.X + f.Width);
            }
            else
            {
                right = Math.Min(right, f.X);
            }
        }
        return (left, Math.Max(left, right));
    }

    private void Flow(Element el, Box content, int depth)
    {
        var y = 0d;
        var pendingMargin = 0d;
        var floats = new List<FloatArea>();
        var inline = new List<Element>();
        var absolute = new List<Element>();
        var index = LayoutValues.Integer(el.Attr("start"), 1);
        void Flush()
        {
            if (inline.Count == 0)
            {
                return;
            }

            var next = InlineFlow(inline, el, content, y + pendingMargin, floats, depth);
            if (next > y + pendingMargin + Epsilon)
            {
                y = next;
                pendingMargin = 0;
            }
            inline.Clear();
        }
        foreach (var child in el.Children)
        {
            if (Hidden(child))
            {
                continue;
            }

            var position = child.Css("position");
            if (position is "absolute" or "fixed")
            {
                absolute.Add(child);
                continue;
            }
            var floating = child.Css("float");
            if (Inline(child) && floating is not ("left" or "right"))
            {
                inline.Add(child);
                continue;
            }
            Flush();
            var clear = child.Css("clear");
            if (clear is "left" or "right" or "both")
            {
                foreach (var f in floats.Where(f => clear == "both" || (clear == "left") == f.Left))
                {
                    y = Math.Max(y, f.Bottom);
                }
            }

            var m = child.Edges("margin", content.Width);
            var top = y + Math.Max(0, Math.Max(pendingMargin, m.Top)) + Math.Min(0, Math.Min(pendingMargin, m.Top));
            if (floating is "left" or "right")
            {
                var desired = Math.Min(content.Width, Sizing(child, "width", content.Width, Intrinsic(child, false)));
                var space = FreeSpace(floats, top, 1, content.Width);
                while (space.Right - space.Left + Epsilon < desired + m.Horizontal &&
                       floats.Any(f => f.Bottom > top + Epsilon))
                {
                    top = floats.Where(f => f.Bottom > top + Epsilon).Min(f => f.Bottom);
                    space = FreeSpace(floats, top, 1, content.Width);
                }
                var childBox = Build(child, content.Width, Math.Max(0, desired), depth);
                var left = floating == "left";
                var x = left ? space.Left : space.Right - desired - m.Horizontal;
                content.Place(childBox, x + m.Left, top);
                floats.Add(new FloatArea(left, x, top - m.Top, desired + m.Horizontal, top + childBox.Height + m.Bottom));
                continue;
            }
            var free = FreeSpace(floats, top, 1, content.Width);
            var childWidth = free.Right - free.Left;
            var block = Build(child, childWidth, depth: depth);
            var xOffset = free.Left + m.Left;
            var spare = Math.Max(0, childWidth - block.Width - m.Horizontal);
            if (child.Css("margin-left") == "auto")
            {
                xOffset += child.Css("margin-right") == "auto" ? spare / 2 : spare;
            }

            var relative = position == "relative";
            content.Place(block, xOffset + (relative ? Offset(child, "left", "right", childWidth) : 0),
                top + (relative ? Offset(child, "top", "bottom", sheetHeight) : 0));
            if (Display(child) == "list-item" || child.Tag == "li")
            {
                var marker = el.Tag == "ol" ? index++ + "." : "•";
                var size = child.FontSize();
                var markerWidth = assets.Measure(marker, size, child.Bold(), child.Italic(), child.Family());
                if (child.Css("list-style-type") != "none")
                {
                    content.Ink.Add(new DrawText(xOffset - markerWidth - size / 2, top + size, marker, size,
                        child.Bold(), child.Italic(), child.Css("color", "#000000"), child.Family()));
                }
            }
            y = top + block.Height;
            pendingMargin = m.Bottom;
        }
        Flush();
        content.Height = Math.Max(y + pendingMargin, floats.Select(f => f.Bottom).DefaultIfEmpty().Max());
        var positioningHeight = Math.Max(UsedHeight(el, "min-height", 0, 0), UsedHeight(el, "height", content.Height, 0));
        foreach (var child in absolute)
        {
            var left = Sizing(child, "left", content.Width, double.NaN);
            var right = Sizing(child, "right", content.Width, double.NaN);
            var top = Sizing(child, "top", positioningHeight, double.NaN);
            var bottom = Sizing(child, "bottom", positioningHeight, double.NaN);
            double? forced = double.IsFinite(left) && double.IsFinite(right) && child.Css("width", "auto") == "auto"
                ? Math.Max(0, content.Width - left - right) : null;
            var childBox = Build(child, content.Width, forced, depth);
            content.Place(childBox, double.IsFinite(left) ? left : double.IsFinite(right) ? content.Width - right - childBox.Width : 0,
                double.IsFinite(top) ? top : double.IsFinite(bottom) ? positioningHeight - bottom - childBox.Height : 0);
        }
    }

    private static double Offset(Element el, string start, string end, double reference)
        => Sizing(el, start, reference, -Sizing(el, end, reference, 0));

    private double Intrinsic(Element el, bool minimum, int depth = 0)
    {
        if (depth > 192 || Hidden(el))
        {
            return 0;
        }

        if (intrinsicCache.TryGetValue((el, minimum), out var width))
        {
            return width;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (el.Tag == "#text")
        {
            var pieces = minimum ? el.Text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries) : el.Text.Split('\n');
            width = pieces.Select(p => assets.Measure(p, el.FontSize(), el.Bold(), el.Italic(), el.Family())).DefaultIfEmpty().Max();
        }
        else if (el.Tag == "img")
        {
            width = assets.TryImage(el.Attr("src"), out var w, out _) ? w : 0;
        }
        else
        {
            var run = 0d;
            width = 0;
            foreach (var c in el.Children)
            {
                var child = Intrinsic(c, minimum, depth + 1);
                if (Inline(c) && !minimum)
                {
                    run += child;
                }
                else
                {
                    width = Math.Max(width, Math.Max(run, child));
                    run = 0;
                }
            }
            width = Math.Max(width, run);
        }
        if (!el.Css("width").Contains('%'))
        {
            width = Sizing(el, "width", 0, width);
        }

        width += el.Edges("padding", 0).Horizontal + el.Edges("border", 0).Horizontal;
        intrinsicCache[(el, minimum)] = width;
        return width;
    }
}
