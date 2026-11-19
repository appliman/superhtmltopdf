namespace SuperHtmlToPdf;

internal sealed partial class LayoutEngine
{
    private sealed record TableRow(Element Node, bool Header);
    private sealed record CellAt(Element Element, int Row, int Column, int Rows, int Columns);

    private void Table(Element el, Box content, int depth)
    {
        var rows = new List<TableRow>();
        var definitions = new List<Element>();
        var captions = new List<Element>();
        var footer = new List<TableRow>();
        var headers = new List<TableRow>();
        foreach (var child in el.Children.Where(c => !Hidden(c)))
        {
            if (child.Tag == "caption" || Display(child) == "table-caption")
            {
                captions.Add(child);
                continue;
            }
            if (child.Tag == "col")
            {
                AddColumn(child);
            }

            if (child.Tag == "colgroup")
            {
                if (child.Children.Any(c => c.Tag == "col"))
                {
                    foreach (var col in child.Children.Where(c => c.Tag == "col"))
                    {
                        AddColumn(col);
                    }
                }
                else
                {
                    AddColumn(child);
                }
            }
            if (IsRow(child))
            {
                rows.Add(new TableRow(child, false));
            }
            else if (child.Tag is "thead" or "tbody" or "tfoot" || Display(child) is "table-header-group" or "table-row-group" or "table-footer-group")
            {
                var header = Display(child) == "table-header-group" || child.Tag == "thead";
                var group = header ? headers : child.Tag == "tfoot" || Display(child) == "table-footer-group" ? footer : rows;
                foreach (var row in child.Children.Where(c => !Hidden(c) && IsRow(c)))
                {
                    group.Add(new TableRow(row, header));
                }
            }
        }
        void AddColumn(Element col)
        {
            var span = Math.Clamp(LayoutValues.Integer(col.Attr("span"), 1), 1, 1000);
            if (definitions.Count + span > 1000)
            {
                throw new InvalidOperationException("A table exceeds 1000 columns.");
            }

            for (var i = 0; i < span; i++)
            {
                definitions.Add(col);
            }
        }
        rows.InsertRange(0, headers);
        rows.AddRange(footer);
        var y = 0d;
        foreach (var caption in captions.Where(c => c.Css("caption-side") != "bottom"))
        {
            var box = Build(caption, content.Width, content.Width, depth);
            content.Place(box, 0, y);
            y += box.Height;
        }
        var positions = new List<CellAt>();
        var occupancy = new Dictionary<int, int>();
        var columns = Math.Max(1, definitions.Count);
        for (var row = 0; row < rows.Count; row++)
        {
            var col = 0;
            foreach (var cell in rows[row].Node.Children.Where(c => !Hidden(c) && (c.Tag is "th" or "td" || Display(c) == "table-cell")))
            {
                var across = Math.Clamp(LayoutValues.Integer(cell.Attr("colspan"), 1), 1, 1000);
                var down = Math.Clamp(LayoutValues.Integer(cell.Attr("rowspan"), 1), 0, rows.Count - row);
                if (down == 0)
                {
                    down = rows.Count - row;
                }

                while (Enumerable.Range(col, across).Any(i => { CountPlacement(); return occupancy.GetValueOrDefault(i) > row; }))
                {
                    col++;
                }

                if (col + across > 1000)
                {
                    throw new InvalidOperationException("A table exceeds 1000 columns.");
                }

                positions.Add(new CellAt(cell, row, col, down, across));
                for (var c = col; c < col + across; c++)
                {
                    occupancy[c] = row + down;
                }

                col += across;
            }
            columns = Math.Max(columns, col);
        }
        var separated = el.Css("border-collapse") != "collapse";
        var gap = LayoutValues.SplitTracks(el.Css("border-spacing", "2px"));
        var dx = separated && gap.Count > 0 ? Math.Max(0, LayoutValues.Length(gap[0], content.Width, el.FontSize())) : 0;
        var dy = separated && gap.Count > 0 ? Math.Max(0, LayoutValues.Length(gap[^1], content.Width, el.FontSize())) : 0;
        var available = Math.Max(0, content.Width - (columns + 1) * dx);
        var widths = ColumnWidths(el, definitions, positions, columns, available, dx);
        var starts = new double[columns + 1];
        starts[0] = dx;
        for (var c = 0; c < columns; c++)
        {
            starts[c + 1] = starts[c] + widths[c] + dx;
        }

        var heights = new double[rows.Count];
        var cells = new List<(CellAt Position, Box Box)>();
        foreach (var cell in positions)
        {
            var w = starts[cell.Column + cell.Columns] - starts[cell.Column] - dx;
            var box = Build(cell.Element, w, w, depth);
            cells.Add((cell, box));
            if (cell.Rows == 1)
            {
                heights[cell.Row] = Math.Max(heights[cell.Row], box.Height);
            }
        }
        foreach (var (cell, box) in cells.Where(entry => entry.Position.Rows > 1))
        {
            var occupied = heights.AsSpan(cell.Row, cell.Rows).ToArray().Sum() + dy * (cell.Rows - 1);
            if (occupied < box.Height)
            {
                heights[cell.Row + cell.Rows - 1] += box.Height - occupied;
            }
        }
        var rowOffsets = new double[rows.Count + 1];
        rowOffsets[0] = y + dy;
        var rowBoxes = new Box[rows.Count];
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r].Node;
            heights[r] = Math.Max(heights[r], Sizing(row, "height", 0, 0));
            rowOffsets[r + 1] = rowOffsets[r] + heights[r] + dy;
            rowBoxes[r] = new Box(row, starts[^1]) { Height = heights[r], KeepTogether = true };
            content.Place(rowBoxes[r], 0, rowOffsets[r]);
        }
        foreach (var (cell, box) in cells)
        {
            var h = rowOffsets[cell.Row + cell.Rows] - rowOffsets[cell.Row] - dy;
            var remaining = Math.Max(0, h - box.Height);
            var align = cell.Element.Css("vertical-align");
            var shift = align == "middle" ? remaining / 2 : align == "bottom" ? remaining : 0;
            if (shift > 0 && box.Children.Count > 0)
            {
                var entry = box.Children[0];
                box.Children[0] = (entry.X, entry.Y + shift, entry.Child);
            }
            box.Height = Math.Max(box.Height, h);
            rowBoxes[cell.Row].Place(box, starts[cell.Column], 0);
        }
        if (headers.Count > 0 && headers.Count < rows.Count)
        {
            content.TableHeader = (rowOffsets[0], rowOffsets[headers.Count]);
        }

        y = rowOffsets[^1];
        foreach (var caption in captions.Where(c => c.Css("caption-side") == "bottom"))
        {
            var box = Build(caption, content.Width, content.Width, depth);
            content.Place(box, 0, y);
            y += box.Height;
        }
        content.Height = y;
    }

    private static bool IsRow(Element e) => e.Tag == "tr" || Display(e) == "table-row";

    private double[] ColumnWidths(Element table, List<Element> definitions, List<CellAt> cells, int count, double available, double gap)
    {
        var widths = new double[count];
        var desired = new double[count];
        var fixedColumns = new bool[count];
        var fixedLayout = table.Css("table-layout") == "fixed";
        double Explicit(Element el) => LayoutValues.Length(el.Css("width", el.Attr("width")), available, el.FontSize(), double.NaN);
        for (var i = 0; i < definitions.Count; i++)
        {
            var size = Explicit(definitions[i]);
            if (double.IsFinite(size))
            {
                widths[i] = size;
                fixedColumns[i] = true;
            }
        }
        foreach (var cell in cells.OrderBy(c => c.Columns))
        {
            if (fixedLayout && cell.Row > 0)
            {
                continue;
            }

            var specified = Explicit(cell.Element);
            var floor = double.IsFinite(specified) ? specified : fixedLayout ? 0 : Intrinsic(cell.Element, true);
            var preferred = fixedLayout ? floor : Math.Max(floor, Intrinsic(cell.Element, false));
            var span = Enumerable.Range(cell.Column, cell.Columns).ToArray();
            var free = span.Where(c => !fixedColumns[c]).ToArray();
            if (free.Length == 0)
            {
                continue;
            }

            var extra = Math.Max(0, floor - span.Sum(c => widths[c]) - gap * (span.Length - 1)) / free.Length;
            var want = Math.Max(0, preferred - gap * (span.Length - 1)) / span.Length;
            foreach (var c in free)
            {
                widths[c] += extra;
                desired[c] = Math.Max(desired[c], want);
            }
            if (cell.Columns == 1 && double.IsFinite(specified))
            {
                fixedColumns[cell.Column] = true;
            }
        }
        var remaining = available - widths.Sum();
        var autos = Enumerable.Range(0, count).Where(c => !fixedColumns[c]).ToArray();
        if (autos.Length == 0)
        {
            autos = Enumerable.Range(0, count).ToArray();
        }

        if (remaining > 0)
        {
            var demands = autos.Sum(c => Math.Max(0, desired[c] - widths[c]));
            var fill = Math.Min(remaining, demands);
            if (demands > 0)
            {
                foreach (var c in autos)
                {
                    widths[c] += fill * Math.Max(0, desired[c] - widths[c]) / demands;
                }
            }

            foreach (var c in autos)
            {
                widths[c] += (remaining - fill) / autos.Length;
            }
        }
        return widths;
    }
}
