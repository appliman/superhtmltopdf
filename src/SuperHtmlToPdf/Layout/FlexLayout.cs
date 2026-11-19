namespace SuperHtmlToPdf;

internal sealed partial class LayoutEngine
{
    private sealed class FlexItem(Element el, double basis, double min, double max, double grow, double shrink, BoxEdges margin)
    {
        public Element Node => el;
        public double Basis => basis;
        public double Min => min;
        public double Max => max;
        public double Grow => grow;
        public double Shrink => shrink;
        public BoxEdges Margin => margin;
        public double Used = Math.Clamp(basis, min, max);
        public Box? Box;
    }

    private static IEnumerable<Element> Items(Element el) => el.Children.Where(c => !Hidden(c) && (c.Tag != "#text" || !string.IsNullOrWhiteSpace(c.Text)))
        .OrderBy(c => LayoutValues.Integer(c.Css("order"), 0));

    private static (double Start, double Gap) Distribution(string value, double free, int count)
    {
        if (count <= 0 || free <= 0)
        {
            return (0, 0);
        }

        return value switch
        {
            "end" or "flex-end" or "right" => (free, 0),
            "center" => (free / 2, 0),
            "space-between" when count > 1 => (0, free / (count - 1)),
            "space-around" => (free / count / 2, free / count),
            "space-evenly" => (free / (count + 1), free / (count + 1)),
            _ => (0, 0)
        };
    }

    private void Flex(Element container, Box content, int depth)
    {
        var direction = container.Css("flex-direction", "row");
        var column = direction.StartsWith("column", StringComparison.Ordinal);
        var reverse = direction.EndsWith("reverse", StringComparison.Ordinal);
        var wrap = container.Css("flex-wrap", "nowrap");
        var mainGap = Math.Max(0, Sizing(container, column ? "row-gap" : "column-gap", content.Width, 0));
        var crossGap = Math.Max(0, Sizing(container, column ? "column-gap" : "row-gap", content.Width, 0));
        var main = column ? UsedHeight(container, "height", double.PositiveInfinity, 0) : content.Width;
        var crossLimit = column ? content.Width : UsedHeight(container, "height", 0, 0);
        var lines = new List<List<FlexItem>>();
        var line = new List<FlexItem>();
        var lineSize = 0d;
        foreach (var el in Items(container))
        {
            var margin = el.Edges("margin", content.Width);
            Box? natural = column ? Build(el, content.Width, depth: depth) : null;
            var automatic = column ? natural!.Height : Intrinsic(el, false);
            var axis = column ? "height" : "width";
            var reference = double.IsFinite(main) ? main : 0;
            var basis = Sizing(el, "flex-basis", reference, Sizing(el, axis, reference, automatic));
            var min = Math.Max(0, Sizing(el, "min-" + axis, reference, column ? natural!.Height : Intrinsic(el, true)));
            var max = Math.Max(min, Sizing(el, "max-" + axis, reference, double.PositiveInfinity));
            var item = new FlexItem(el, Math.Max(0, basis), min, max, Math.Max(0, LayoutValues.Number(el.Css("flex-grow"))),
                Math.Max(0, LayoutValues.Number(el.Css("flex-shrink", "1"), 1)), margin);
            var itemSize = item.Used + (column ? margin.Vertical : margin.Horizontal);
            if (wrap != "nowrap" && line.Count > 0 && lineSize + mainGap + itemSize > main + Epsilon)
            {
                lines.Add(line);
                line = [];
                lineSize = 0;
            }
            lineSize += itemSize + (line.Count > 0 ? mainGap : 0);
            line.Add(item);
        }
        if (line.Count != 0)
        {
            lines.Add(line);
        }

        if (lines.Count == 0)
        {
            return;
        }

        if (wrap == "wrap-reverse")
        {
            lines.Reverse();
        }

        var crossSizes = new List<double>();
        foreach (var items in lines)
        {
            if (double.IsFinite(main))
            {
                DistributeFlex(items, main - mainGap * (items.Count - 1) -
                items.Sum(i => column ? i.Margin.Vertical : i.Margin.Horizontal));
            }

            var lineCross = 0d;
            foreach (var item in items)
            {
                var align = item.Node.Css("align-self", "auto");
                if (align == "auto")
                {
                    align = container.Css("align-items", "stretch");
                }

                var crossWidth = column
                    ? Sizing(item.Node, "width", content.Width, align is "stretch" or "normal" ? content.Width - item.Margin.Horizontal : Math.Min(content.Width, Intrinsic(item.Node, false)))
                    : item.Used;
                item.Box = Build(item.Node, content.Width, Math.Max(0, crossWidth), depth);
                if (column)
                {
                    item.Box.Height = item.Used;
                }

                lineCross = Math.Max(lineCross, column ? item.Box.Width + item.Margin.Horizontal : item.Box.Height + item.Margin.Vertical);
            }
            crossSizes.Add(lineCross);
        }
        var naturalCross = crossSizes.Sum() + crossGap * (lines.Count - 1);
        var extra = Math.Max(0, crossLimit - naturalCross);
        var alignContent = container.Css("align-content", "stretch");
        if (alignContent is "stretch" or "normal" || lines.Count == 1)
        {
            for (var i = 0; i < crossSizes.Count; i++)
            {
                crossSizes[i] += extra / crossSizes.Count;
            }
        }

        var distribution = Distribution(alignContent, Math.Max(0, crossLimit - crossSizes.Sum() - crossGap * (lines.Count - 1)), lines.Count);
        var crossPos = distribution.Start;
        var longest = 0d;
        for (var i = 0; i < lines.Count; i++)
        {
            var items = lines[i];
            var occupied = items.Sum(item => item.Used + (column ? item.Margin.Vertical : item.Margin.Horizontal)) + mainGap * (items.Count - 1);
            var total = double.IsFinite(main) ? main : occupied;
            var alignMain = Distribution(container.Css("justify-content"), total - occupied, items.Count);
            var pos = alignMain.Start;
            foreach (var item in items)
            {
                var box = item.Box!;
                var align = item.Node.Css("align-self", "auto");
                if (align == "auto")
                {
                    align = container.Css("align-items", "stretch");
                }

                var marginMainStart = column ? item.Margin.Top : item.Margin.Left;
                var marginMainEnd = column ? item.Margin.Bottom : item.Margin.Right;
                var marginCross = column ? item.Margin.Horizontal : item.Margin.Vertical;
                if (align is "stretch" or "normal" && item.Node.Css(column ? "width" : "height", "auto") == "auto")
                {
                    if (column && Math.Abs(box.Width - crossSizes[i] + marginCross) > Epsilon)
                    {
                        box = Build(item.Node, content.Width, Math.Max(0, crossSizes[i] - marginCross), depth);
                    }
                    else if (!column)
                    {
                        box.Height = Math.Max(box.Height, crossSizes[i] - marginCross);
                    }

                    if (column)
                    {
                        box.Height = item.Used;
                    }
                }
                var room = Math.Max(0, crossSizes[i] - marginCross - (column ? box.Width : box.Height));
                var offset = crossPos + (column ? item.Margin.Left : item.Margin.Top) + Distribution(align, room, 1).Start;
                var start = reverse ? total - pos - marginMainEnd - item.Used : pos + marginMainStart;
                content.Place(box, column ? offset : start, column ? start : offset);
                pos += marginMainStart + item.Used + marginMainEnd + mainGap + alignMain.Gap;
            }
            longest = Math.Max(longest, total);
            crossPos += crossSizes[i] + crossGap + distribution.Gap;
        }
        content.Height = column ? longest : Math.Max(naturalCross, crossLimit);
    }

    private static void DistributeFlex(List<FlexItem> items, double target)
    {
        var active = new List<FlexItem>(items);
        var remaining = target - items.Sum(i => i.Used);
        while (active.Count > 0 && Math.Abs(remaining) > Epsilon)
        {
            var grow = remaining > 0;
            double Factor(FlexItem item) => grow ? item.Grow : item.Shrink * item.Basis;
            var totalFactor = active.Sum(Factor);
            if (totalFactor <= Epsilon)
            {
                break;
            }

            var previous = remaining;
            var frozen = new List<FlexItem>();
            foreach (var item in active)
            {
                var proposed = item.Used + previous * Factor(item) / totalFactor;
                var clamped = Math.Clamp(proposed, item.Min, item.Max);
                remaining -= clamped - item.Used;
                item.Used = clamped;
                if (Math.Abs(clamped - proposed) > Epsilon)
                {
                    frozen.Add(item);
                }
            }
            if (frozen.Count == 0)
            {
                break;
            }

            foreach (var item in frozen)
            {
                active.Remove(item);
            }
        }
    }
}
