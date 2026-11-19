namespace SuperHtmlToPdf;

internal sealed partial class LayoutEngine
{
    private const int MaxTracks = 1024;
    private sealed record GridItem(Element Element, int Column, int Row, int Columns, int Rows);
    private sealed class Track(double size, double cap, double fraction, bool intrinsic)
    {
        public double Size = size;
        public double Cap => cap;
        public double Fraction => fraction;
        public bool Intrinsic => intrinsic;
    }

    private void Grid(Element el, Box content, int depth)
    {
        var dx = Math.Max(0, Sizing(el, "column-gap", content.Width, 0));
        var dy = Math.Max(0, Sizing(el, "row-gap", content.Width, 0));
        var height = UsedHeight(el, "height", double.NaN, 0);
        var columnSpec = TrackList(el, el.Css("grid-template-columns"), content.Width, dx);
        var rowSpec = TrackList(el, el.Css("grid-template-rows"), double.IsFinite(height) ? height : 0, dy);
        var columnCount = Math.Max(1, columnSpec.Count);
        var rowCount = Math.Max(1, rowSpec.Count);
        var occupancy = new HashSet<(int Row, int Column)>();
        var placed = new List<GridItem>();
        var autoFlow = el.Css("grid-auto-flow", "row");
        var byColumn = autoFlow.Contains("column", StringComparison.Ordinal);
        var dense = autoFlow.Contains("dense", StringComparison.Ordinal);
        var nextRow = 0;
        var nextCol = 0;
        var entries = Items(el).Select(child => (Element: child, Column: GridPosition(child, "column", columnCount),
            Row: GridPosition(child, "row", rowCount))).ToArray();
        foreach (var entry in entries)
        {
            if (entry.Column.Start is { } c)
            {
                columnCount = Math.Max(columnCount, c + entry.Column.Span);
            }
            else
            {
                columnCount = Math.Max(columnCount, entry.Column.Span);
            }

            if (entry.Row.Start is { } r)
            {
                rowCount = Math.Max(rowCount, r + entry.Row.Span);
            }
            else
            {
                rowCount = Math.Max(rowCount, entry.Row.Span);
            }
        }
        foreach (var entry in entries.OrderByDescending(e => (e.Row.Start.HasValue ? 1 : 0) + (e.Column.Start.HasValue ? 1 : 0)))
        {
            var cw = entry.Column.Span;
            var rh = entry.Row.Span;
            var definiteColumn = entry.Column.Start;
            var definiteRow = entry.Row.Start;
            var r = definiteRow ?? (dense || definiteColumn.HasValue ? 0 : nextRow);
            var c = definiteColumn ?? (dense || definiteRow.HasValue ? 0 : nextCol);
            while (true)
            {
                if (r + rh > MaxTracks || c + cw > MaxTracks)
                {
                    throw new InvalidOperationException("Grid exceeds the track limit.");
                }

                if (definiteColumn.HasValue && definiteRow.HasValue || Fits(r, c, rh, cw))
                {
                    break;
                }

                if (definiteColumn.HasValue)
                {
                    r++;
                }
                else if (definiteRow.HasValue)
                {
                    c++;
                }
                else if (byColumn)
                {
                    if (++r + rh > rowCount)
                    {
                        r = 0;
                        c++;
                    }
                }
                else
                {
                    if (++c + cw > columnCount)
                    {
                        c = 0;
                        r++;
                    }
                }
            }
            for (var row = r; row < r + rh; row++)
            {
                for (var col = c; col < c + cw; col++)
                {
                    occupancy.Add((row, col));
                }
            }

            placed.Add(new GridItem(entry.Element, c, r, cw, rh));
            rowCount = Math.Max(rowCount, r + rh);
            columnCount = Math.Max(columnCount, c + cw);
            if (!definiteColumn.HasValue && !definiteRow.HasValue)
            {
                nextRow = r;
                nextCol = c;
            }
        }
        bool Fits(int row, int col, int rows, int cols)
        {
            for (var rr = row; rr < row + rows; rr++)
            {
                for (var cc = col; cc < col + cols; cc++)
                {
                    CountPlacement();
                    if (occupancy.Contains((rr, cc)))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        var autoColumns = TrackList(el, el.Css("grid-auto-columns", "auto"), content.Width, dx);
        var autoRows = TrackList(el, el.Css("grid-auto-rows", "auto"), double.IsFinite(height) ? height : 0, dy);
        FillImplicit(columnSpec, autoColumns, columnCount);
        FillImplicit(rowSpec, autoRows, rowCount);
        var xTracks = columnSpec.Select(spec => ParseTrack(spec, content.Width, el)).ToArray();
        var yTracks = rowSpec.Select(spec => ParseTrack(spec, double.IsFinite(height) ? height : 0, el)).ToArray();
        foreach (var item in placed.OrderBy(i => i.Columns))
        {
            Contribute(xTracks, item.Column, item.Columns, Intrinsic(item.Element, true) + item.Element.Edges("margin", content.Width).Horizontal, dx);
        }

        CompleteTracks(xTracks, content.Width - (columnCount - 1) * dx, el.Css("justify-content", "stretch"));
        var xOffsets = TrackOffsets(xTracks, dx, content.Width, el.Css("justify-content", "stretch"), out var actualDx);
        var boxes = new Dictionary<Element, Box>();
        foreach (var item in placed.OrderBy(i => i.Rows))
        {
            var available = xOffsets[item.Column + item.Columns] - xOffsets[item.Column] - actualDx;
            var margin = item.Element.Edges("margin", available);
            var align = item.Element.Css("justify-self", "auto");
            if (align == "auto")
            {
                align = el.Css("justify-items", "stretch");
            }

            var itemWidth = Sizing(item.Element, "width", available, align is "stretch" or "normal" ? available - margin.Horizontal : Math.Min(available, Intrinsic(item.Element, false)));
            var box = Build(item.Element, available, Math.Max(0, itemWidth), depth);
            boxes.Add(item.Element, box);
            Contribute(yTracks, item.Row, item.Rows, box.Height + margin.Vertical, dy);
        }
        if (double.IsFinite(height))
        {
            CompleteTracks(yTracks, height - (rowCount - 1) * dy, el.Css("align-content", "stretch"));
        }
        else
        {
            var fraction = yTracks.Where(t => t.Fraction > 0).Select(t => t.Size / t.Fraction).DefaultIfEmpty().Max();
            foreach (var t in yTracks.Where(t => t.Fraction > 0))
            {
                t.Size = Math.Max(t.Size, t.Fraction * fraction);
            }
        }
        var naturalHeight = yTracks.Sum(t => t.Size) + dy * (yTracks.Length - 1);
        var yOffsets = TrackOffsets(yTracks, dy, double.IsFinite(height) ? height : naturalHeight, el.Css("align-content", "stretch"), out var actualDy);
        // Paint in order-modified document order, independently of the placement phase.
        var paintOrder = placed.ToDictionary(p => p.Element);
        foreach (var child in Items(el))
        {
            var item = paintOrder[child];
            var box = boxes[child];
            var areaWidth = xOffsets[item.Column + item.Columns] - xOffsets[item.Column] - actualDx;
            var areaHeight = yOffsets[item.Row + item.Rows] - yOffsets[item.Row] - actualDy;
            var margin = child.Edges("margin", areaWidth);
            var horizontal = child.Css("justify-self", "auto");
            if (horizontal == "auto")
            {
                horizontal = el.Css("justify-items", "stretch");
            }

            var vertical = child.Css("align-self", "auto");
            if (vertical == "auto")
            {
                vertical = el.Css("align-items", "stretch");
            }

            if (vertical is "stretch" or "normal" && child.Css("height", "auto") == "auto")
            {
                box.Height = Math.Max(box.Height, areaHeight - margin.Vertical);
            }

            var x = xOffsets[item.Column] + margin.Left + Distribution(horizontal, areaWidth - box.Width - margin.Horizontal, 1).Start;
            var y = yOffsets[item.Row] + margin.Top + Distribution(vertical, areaHeight - box.Height - margin.Vertical, 1).Start;
            content.Place(box, x, y);
        }
        content.Height = Math.Max(naturalHeight, double.IsFinite(height) ? height : 0);
    }

    private (int? Start, int Span) GridPosition(Element el, string axis, int count)
    {
        var parts = el.Css("grid-" + axis).Split('/', StringSplitOptions.TrimEntries);
        var first = el.Css("grid-" + axis + "-start", parts[0]);
        var last = el.Css("grid-" + axis + "-end", parts.Length > 1 ? parts[1] : "");
        int? Line(string value)
        {
            var index = LayoutValues.Integer(value, 0);
            return index > 0 ? Math.Min(index - 1, MaxTracks) : index < 0 ? Math.Max(0, count + 1 + index) : null;
        }
        var start = Line(first);
        var end = Line(last);
        var span = last.StartsWith("span ", StringComparison.Ordinal) ? LayoutValues.Integer(last[5..], 1) :
            first.StartsWith("span ", StringComparison.Ordinal) ? LayoutValues.Integer(first[5..], 1) : 1;
        if (start.HasValue && end.HasValue)
        {
            span = Math.Max(1, end.Value - start.Value);
        }
        else if (!start.HasValue && end.HasValue)
        {
            start = Math.Max(0, end.Value - span);
        }

        return (start, Math.Clamp(span, 1, MaxTracks));
    }

    private List<string> TrackList(Element el, string value, double available, double gap)
    {
        var result = new List<string>();
        foreach (var t in LayoutValues.SplitTracks(value, MaxTracks))
        {
            if (result.Count >= MaxTracks)
            {
                throw new InvalidDataException("Grid exceeds the track limit.");
            }

            if (t is "none" or "normal" || t.StartsWith('['))
            {
                continue;
            }

            if (t.StartsWith("repeat(", StringComparison.Ordinal) && t.EndsWith(')') && t.IndexOf(',') is var split && split >= 7)
            {
                var countText = t[7..split].Trim();
                var inner = LayoutValues.SplitTracks(t[(split + 1)..^1], MaxTracks - result.Count);
                if (inner.Count == 0)
                {
                    continue;
                }

                var count = LayoutValues.Integer(countText, 1);
                if (countText is "auto-fit" or "auto-fill")
                {
                    var unit = inner.Sum(s => Math.Max(0.75, ParseTrack(s, available, el).Size)) + gap * inner.Count;
                    count = (int)Math.Clamp(Math.Floor((available + gap) / unit), 1, MaxTracks);
                }
                if (count < 1 || count > MaxTracks || (long)count * inner.Count + result.Count > MaxTracks)
                {
                    throw new InvalidDataException("Grid exceeds the track limit.");
                }

                for (var i = 0; i < count; i++)
                {
                    result.AddRange(inner);
                }
            }
            else
            {
                result.Add(t);
            }
        }
        if (result.Count > MaxTracks)
        {
            throw new InvalidOperationException("Grid exceeds the track limit.");
        }

        return result;
    }

    private static void FillImplicit(List<string> tracks, List<string> pattern, int count)
    {
        var start = tracks.Count;
        while (tracks.Count < count)
        {
            tracks.Add(pattern.Count == 0 ? "auto" : pattern[(tracks.Count - start) % pattern.Count]);
        }
    }
    private Track ParseTrack(string value, double available, Element el)
    {
        value = value.Trim();
        if (value.StartsWith("minmax(", StringComparison.Ordinal) && value.EndsWith(')') && value.IndexOf(',') is var comma && comma > 7)
        {
            var first = value[7..comma].Trim();
            var second = value[(comma + 1)..^1].Trim();
            if (first.Contains('(') || second.Contains('('))
            {
                Warning("css-grid-minmax", "minmax() bounds must be atomic track sizes. The track uses auto sizing.");
                return new Track(0, double.PositiveInfinity, 0, true);
            }
            var low = AtomicTrack(first, available, el);
            var high = AtomicTrack(second, available, el);
            return new Track(low.Size, Math.Max(low.Size, high.Cap), high.Fraction, low.Intrinsic || high.Intrinsic);
        }
        return AtomicTrack(value, available, el);
    }

    private static Track AtomicTrack(string value, double available, Element el)
    {
        if (value.EndsWith("fr", StringComparison.Ordinal))
        {
            return new Track(0, double.PositiveInfinity, Math.Max(0, LayoutValues.Number(value[..^2], 1)), true);
        }

        if (value is "auto" or "min-content" or "max-content")
        {
            return new Track(0, double.PositiveInfinity, 0, true);
        }

        var fixedSize = Math.Max(0, LayoutValues.Length(value, available, el.FontSize()));
        return new Track(fixedSize, fixedSize, 0, false);
    }
    private static void Contribute(Track[] tracks, int index, int count, double demand, double gap)
    {
        var span = tracks.AsSpan(index, count);
        var total = gap * (count - 1);
        var growable = 0;
        foreach (var t in span)
        {
            total += t.Size;
            if (t.Intrinsic && t.Cap > t.Size)
            {
                growable++;
            }
        }
        if (growable == 0 || demand <= total)
        {
            return;
        }

        foreach (var t in span)
        {
            if (t.Intrinsic && t.Cap > t.Size)
            {
                t.Size = Math.Min(t.Cap, t.Size + (demand - total) / growable);
            }
        }
    }
    private static void CompleteTracks(Track[] tracks, double available, string alignment)
    {
        var fractions = tracks.Where(t => t.Fraction > 0).ToList();
        var remaining = available - tracks.Where(t => t.Fraction == 0).Sum(t => t.Size);
        while (fractions.Count > 0)
        {
            var unit = Math.Max(0, remaining) / Math.Max(1, fractions.Sum(t => t.Fraction));
            var frozen = fractions.Where(t => t.Size > unit * t.Fraction + Epsilon).ToArray();
            if (frozen.Length == 0)
            {
                foreach (var t in fractions)
                {
                    t.Size = Math.Max(t.Size, unit * t.Fraction);
                }

                break;
            }
            foreach (var t in frozen)
            {
                remaining -= t.Size;
                fractions.Remove(t);
            }
        }
        if (alignment is not ("normal" or "stretch"))
        {
            return;
        }

        var auto = tracks.Where(t => t.Fraction == 0 && t.Cap > t.Size && t.Intrinsic).ToArray();
        var extra = Math.Max(0, available - tracks.Sum(t => t.Size));
        foreach (var t in auto)
        {
            t.Size = Math.Min(t.Cap, t.Size + extra / auto.Length);
        }
    }
    private static double[] TrackOffsets(Track[] tracks, double gap, double reference, string alignment, out double actualGap)
    {
        var d = Distribution(alignment, reference - tracks.Sum(t => t.Size) - (tracks.Length - 1) * gap, tracks.Length);
        actualGap = gap + d.Gap;
        var offsets = new double[tracks.Length + 1];
        offsets[0] = d.Start;
        for (var i = 0; i < tracks.Length; i++)
        {
            offsets[i + 1] = offsets[i] + tracks[i].Size + actualGap;
        }

        return offsets;
    }
}
