namespace SuperHtmlToPdf;

internal sealed partial class LayoutEngine
{
    private readonly record struct Interval(double Start, double End);
    private sealed record Header(double Start, double End, double TableEnd, List<Drawing> Ink);
    private sealed record Page(double Start, double End, Header? Repeat);
    private sealed class Canvas
    {
        public List<Drawing> Ink { get; } = [];
        public List<double> Cuts { get; } = [0];
        public List<double> Forced { get; } = [];
        public List<Interval> Hard { get; } = [];
        public List<Interval> Soft { get; } = [];
        public List<Header> Headers { get; } = [];
        public double Extent;
    }

    public List<List<Drawing>> Paginate(Element root, double width, double height, int maxPages)
    {
        if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(height) || height <= 0 || maxPages < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Page dimensions and page count must be positive.");
        }

        sheetHeight = height;
        var rootBox = Build(root, width, width);
        var canvas = new Canvas();
        Flatten(rootBox, 0, 0, canvas);
        var end = Math.Max(rootBox.Height, canvas.Extent);
        if (!double.IsFinite(end))
        {
            throw new InvalidOperationException("Document dimensions exceed the supported range.");
        }

        canvas.Cuts.Add(end);
        var cuts = canvas.Cuts.Distinct().Order().ToArray();
        var forced = canvas.Forced.Distinct().Order().ToArray();
        var pageList = new List<Page>();
        var start = 0d;
        var headers = canvas.Headers.OrderBy(h => h.Start).ToArray();
        var hard = new ProtectedBreaks(canvas.Hard, height);
        var soft = new ProtectedBreaks(canvas.Soft, height);
        var activeHeaders = new List<Header>();
        var headerIndex = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pageList.Count >= maxPages)
            {
                throw new InvalidOperationException("Document exceeds the configured page limit.");
            }

            activeHeaders.RemoveAll(h => h.TableEnd <= start + Epsilon);
            while (headerIndex < headers.Length && headers[headerIndex].Start < start + Epsilon)
            {
                var header = headers[headerIndex++];
                if (header.TableEnd > start + Epsilon)
                {
                    activeHeaders.Add(header);
                }
            }
            var repeat = activeHeaders.LastOrDefault(h => start >= h.End - Epsilon && h.End - h.Start < height - Epsilon);
            var reserve = repeat is null ? 0 : repeat.End - repeat.Start;
            var limit = Math.Min(end, start + height - reserve);
            var nextForced = UpperBound(forced, start + Epsilon);
            var explicitBreak = nextForced < forced.Length && forced[nextForced] <= limit + Epsilon;
            double stop;
            if (explicitBreak)
            {
                stop = Math.Min(end, forced[nextForced]);
            }
            else if (limit >= end - Epsilon)
            {
                stop = end;
            }
            else
            {
                stop = FindCut(start, limit, cuts, hard, soft);
                if (stop <= start + Epsilon && repeat is not null)
                {
                    // A tall first body row may fit only if its repeated header is suppressed.
                    repeat = null;
                    limit = Math.Min(end, start + height);
                    stop = FindCut(start, limit, cuts, hard, soft);
                }
                if (stop <= start + Epsilon)
                {
                    Warning("FragmentOverflow", "A box exceeds the printable area and was fragmented.");
                    stop = limit;
                }
            }
            if (stop <= start && start < end)
            {
                throw new InvalidOperationException("Pagination did not advance.");
            }

            pageList.Add(new Page(start, stop, repeat));
            start = stop;
        } while (start < end - Epsilon);

        var results = pageList.Select(_ => new List<Drawing>()).ToList();
        for (var i = 0; i < pageList.Count; i++)
        {
            if (pageList[i].Repeat is { } header)
            {
                foreach (var command in header.Ink)
                {
                    if (Slice(command, header.Start, header.End, -header.Start) is { } copy)
                    {
                        results[i].Add(copy);
                    }
                }
            }
        }

        var boundaries = pageList.Select(p => p.End).ToArray();
        foreach (var command in canvas.Ink)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bounds = Bounds(command);
            var first = Math.Min(UpperBound(boundaries, bounds.Start + Epsilon), boundaries.Length - 1);
            for (var i = first; i < pageList.Count; i++)
            {
                var page = pageList[i];
                if (page.Start >= bounds.End - Epsilon && i != first)
                {
                    break;
                }

                var offset = -page.Start + (page.Repeat is { } h ? h.End - h.Start : 0);
                if (Slice(command, page.Start, page.End, offset) is { } drawing)
                {
                    results[i].Add(drawing);
                }
            }
        }
        return results;
    }

    private static int UpperBound(double[] sorted, double value)
    {
        var lo = 0;
        var hi = sorted.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (sorted[mid] <= value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }
    private sealed class ProtectedBreaks
    {
        private readonly double[] starts;
        private readonly double[] ends;
        public ProtectedBreaks(List<Interval> intervals, double max)
        {
            var merged = new List<Interval>();
            foreach (var span in intervals.Where(s => s.End - s.Start <= max + Epsilon && s.End - s.Start > Epsilon).OrderBy(s => s.Start))
            {
                if (merged.Count > 0 && span.Start < merged[^1].End - Epsilon)
                {
                    merged[^1] = merged[^1] with
                    {
                        End = Math.Max(merged[^1].End, span.End)
                    };
                }
                else
                {
                    merged.Add(span);
                }
            }
            starts = merged.Select(s => s.Start).ToArray();
            ends = merged.Select(s => s.End).ToArray();
        }
        public bool Contains(double p)
        {
            var i = UpperBound(starts, p - Epsilon) - 1;
            return i >= 0 && p < ends[i] - Epsilon;
        }
    }
    private static double FindCut(double start, double limit, double[] cuts, ProtectedBreaks hard, ProtectedBreaks soft)
    {
        var fallback = start;
        for (var i = UpperBound(cuts, limit + Epsilon) - 1; i >= 0 && cuts[i] > start + Epsilon; i--)
        {
            if (hard.Contains(cuts[i]))
            {
                continue;
            }

            if (!soft.Contains(cuts[i]))
            {
                return cuts[i];
            }

            if (fallback == start)
            {
                fallback = cuts[i];
            }
        }
        return fallback;
    }

    private static bool PageBreak(string value) => value is "page" or "always" or "left" or "right" or "recto" or "verso";
    private void Flatten(Box box, double x, double y, Canvas canvas)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var end = y + box.Height;
        canvas.Extent = Math.Max(canvas.Extent, end);
        canvas.Cuts.Add(y);
        canvas.Cuts.Add(end);
        if (box.IsLine)
        {
            canvas.Hard.Add(new(y, end));
        }

        if (box.KeepTogether)
        {
            canvas.Soft.Add(new(y, end));
        }

        foreach (var interval in box.PreferredUnbroken)
        {
            canvas.Soft.Add(new(y + interval.Start, y + interval.End));
        }

        var el = box.Element;
        if (el is not null)
        {
            if (PageBreak(el.Css("break-before")))
            {
                canvas.Forced.Add(y);
            }

            if (PageBreak(el.Css("break-after")))
            {
                canvas.Forced.Add(end);
            }

            if (el.Css("visibility") != "hidden")
            {
                Decorate(el, box, x, y, canvas);
            }
        }
        foreach (var cmd in box.Ink)
        {
            var placed = cmd.Translate(x, y);
            canvas.Ink.Add(placed);
            var bounds = Bounds(placed);
            if (placed is DrawImage)
            {
                canvas.Hard.Add(bounds);
            }
        }
        var startInk = canvas.Ink.Count;
        foreach (var child in box.Children)
        {
            Flatten(child.Child, x + child.X, y + child.Y, canvas);
        }

        if (box.TableHeader is { } band)
        {
            var source = canvas.Ink.Skip(startInk).Where(op => Bounds(op).Start < y + band.Bottom - Epsilon &&
                Bounds(op).End > y + band.Top + Epsilon).ToList();
            canvas.Headers.Add(new Header(y + band.Top, y + band.Bottom, end, source));
            canvas.Soft.Add(new(y + band.Top, y + band.Bottom));
        }
    }

    private static void Decorate(Element el, Box box, double x, double y, Canvas canvas)
    {
        var fill = el.Css("background-color");
        if (!string.IsNullOrEmpty(fill) && fill != "transparent" && box.Width > 0 && box.Height > 0)
        {
            canvas.Ink.Add(new DrawRect(x, y, box.Width, box.Height, fill, null, 0));
        }

        var b = el.Edges("border", box.Width);
        void Edge(string side, double xx, double yy, double w, double h)
        {
            if (w <= 0 || h <= 0)
            {
                return;
            }

            var color = el.Css("border-" + side + "-color", el.Css("color", "#000000"));
            if (color == "currentcolor")
            {
                color = el.Css("color", "#000000");
            }

            canvas.Ink.Add(new DrawRect(xx, yy, w, h, color, null, 0));
        }
        Edge("top", x, y, box.Width, b.Top);
        Edge("right", x + box.Width - b.Right, y, b.Right, box.Height);
        Edge("bottom", x, y + box.Height - b.Bottom, box.Width, b.Bottom);
        Edge("left", x, y, b.Left, box.Height);
    }
    private static Interval Bounds(Drawing command) => command switch
    {
        DrawText t => new(t.Baseline - t.Size * 0.8, t.Baseline + t.Size * 0.2),
        DrawRect r => new(r.Y, r.Y + r.Height),
        DrawImage i => new(i.Y, i.Y + i.Height),
        _ => throw new InvalidOperationException("Unknown drawing primitive.")
    };
    private static Drawing? Slice(Drawing command, double start, double end, double offset)
    {
        switch (command)
        {
            case DrawText t when t.Baseline > start + Epsilon && t.Baseline <= end + Epsilon:
                return t.Translate(0, offset);
            case DrawImage image when image.Y >= start - Epsilon && image.Y < end - Epsilon:
                var scale = Math.Min(1, (end - image.Y) / Math.Max(image.Height, Epsilon));
                return image with
                {
                    Y = image.Y + offset,
                    Width = image.Width * scale,
                    Height = image.Height * scale
                };
            case DrawRect rect:
                var top = Math.Max(start, rect.Y);
                var bottom = Math.Min(end, rect.Y + rect.Height);
                if (bottom > top)
                {
                    return rect with
                    {
                        Y = top + offset,
                        Height = bottom - top
                    };
                }

                break;
        }
        return null;
    }
}
