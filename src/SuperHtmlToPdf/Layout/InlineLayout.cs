using System.Globalization;
using System.Text;

namespace SuperHtmlToPdf;

internal sealed partial class LayoutEngine
{
    private sealed record Atom(Element Style, string Text, double Width, bool Space = false, bool Break = false, Box? Object = null);

    private double InlineFlow(List<Element> source, Element parent, Box destination, double startY, List<FloatArea> floats, int depth)
    {
        var atoms = new List<Atom>();
        foreach (var el in source)
        {
            Collect(el, destination.Width, atoms, depth);
        }

        var y = startY;
        var line = new List<Atom>();
        var lineBands = new List<Interval>();
        var used = 0d;
        var pendingSpace = (Atom?)null;
        var indent = Sizing(parent, "text-indent", destination.Width, 0);
        (double Left, double Right) Space() => FreeSpace(floats, y, parent.LineHeight(), destination.Width);
        void Finish(bool forced)
        {
            if (line.Count == 0 && !forced)
            {
                return;
            }

            var strut = parent.LineHeight();
            var ascent = Math.Max(parent.FontSize() * 0.8, (strut - parent.FontSize()) / 2 + parent.FontSize() * 0.8);
            var descent = Math.Max(0, strut - ascent);
            foreach (var atom in line)
            {
                if (atom.Object is { } obj)
                {
                    ascent = Math.Max(ascent, obj.Height);
                    continue;
                }
                var h = atom.Style.LineHeight();
                var a = atom.Style.FontSize() * 0.8 + (h - atom.Style.FontSize()) / 2;
                ascent = Math.Max(ascent, a);
                descent = Math.Max(descent, h - a);
            }
            var bounds = Space();
            var left = bounds.Left + indent;
            var free = Math.Max(0, bounds.Right - left - used);
            var align = parent.Css("text-align");
            var x = align switch
            {
                "right" or "end" => left + free,
                "center" => left + free / 2,
                _ => left
            };
            var spacing = !forced && align == "justify" && line.Count(a => a.Space) is > 0 and var spaces ? free / spaces : 0;
            var box = new Box(null, destination.Width) { Height = ascent + descent, IsLine = true };
            foreach (var atom in line)
            {
                if (atom.Object is { } obj)
                {
                    box.Place(obj, x, Math.Max(0, ascent - obj.Height));
                }
                else if (atom.Text.Length != 0 && atom.Style.Css("visibility") is not ("hidden" or "collapse"))
                {
                    var size = atom.Style.FontSize();
                    var color = atom.Style.Css("color", "#000000");
                    var background = atom.Style.Css("background-color");
                    if (!string.IsNullOrEmpty(background) && background != "transparent")
                    {
                        box.Ink.Add(new DrawRect(x, ascent - size * 0.8, atom.Width, size, background, null, 0));
                    }

                    box.Ink.Add(new DrawText(x, ascent, atom.Text, size, atom.Style.Bold(), atom.Style.Italic(), color, atom.Style.Family()));
                    var decoration = atom.Style.Css("text-decoration-line", atom.Style.Css("text-decoration"));
                    if (decoration.Contains("underline", StringComparison.Ordinal))
                    {
                        box.Ink.Add(new DrawRect(x, ascent + size * 0.1, atom.Width, Math.Max(0.4, size / 20), color, null, 0));
                    }

                    if (decoration.Contains("line-through", StringComparison.Ordinal))
                    {
                        box.Ink.Add(new DrawRect(x, ascent - size * 0.3, atom.Width, Math.Max(0.4, size / 20), color, null, 0));
                    }
                }
                x += atom.Width + (atom.Space ? spacing : 0);
            }
            destination.Place(box, 0, y);
            lineBands.Add(new(y, y + box.Height));
            y += box.Height;
            indent = 0;
            used = 0;
            line.Clear();
            pendingSpace = null;
        }
        foreach (var atom in atoms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (atom.Break)
            {
                Finish(true);
                continue;
            }
            if (atom.Space)
            {
                if (line.Count > 0)
                {
                    pendingSpace = atom;
                }

                continue;
            }
            var bounds = Space();
            var available = Math.Max(0, bounds.Right - bounds.Left - indent);
            var wrap = atom.Style.Css("white-space") is not ("nowrap" or "pre");
            if (wrap && line.Count > 0 && used + (pendingSpace?.Width ?? 0) + atom.Width > available + Epsilon &&
                (pendingSpace is not null || atom.Object is not null || atom.Style.Css("word-break") == "break-all"))
            {
                Finish(false);
                bounds = Space();
                available = Math.Max(0, bounds.Right - bounds.Left - indent);
            }
            while (line.Count == 0 && atom.Width > available + Epsilon && floats.Any(f => f.Y <= y && f.Bottom > y + Epsilon))
            {
                y = floats.Where(f => f.Y <= y && f.Bottom > y + Epsilon).Min(f => f.Bottom);
                bounds = Space();
                available = Math.Max(0, bounds.Right - bounds.Left - indent);
            }
            if (line.Count > 0 && pendingSpace is not null)
            {
                line.Add(pendingSpace);
                used += pendingSpace.Width;
            }
            pendingSpace = null;
            if (wrap && atom.Object is null && atom.Width > available && available > 0 &&
                (atom.Style.Css("overflow-wrap", atom.Style.Css("word-wrap")) is "anywhere" or "break-word" ||
                 atom.Style.Css("word-break") is "break-all" or "break-word"))
            {
                var chars = StringInfo.GetTextElementEnumerator(atom.Text);
                var run = new StringBuilder();
                var runWidth = 0d;
                while (chars.MoveNext())
                {
                    var grapheme = chars.GetTextElement();
                    var w = TextWidth(atom.Style, grapheme);
                    if (run.Length > 0 && used + runWidth + w > available)
                    {
                        line.Add(new Atom(atom.Style, run.ToString(), runWidth));
                        used += runWidth;
                        Finish(false);
                        run.Clear();
                        runWidth = 0;
                    }
                    run.Append(grapheme);
                    runWidth += w;
                }
                if (run.Length > 0)
                {
                    line.Add(new Atom(atom.Style, run.ToString(), runWidth));
                    used += runWidth;
                }
            }
            else
            {
                line.Add(atom);
                used += atom.Width;
            }
        }
        if (line.Count > 0)
        {
            Finish(true);
        }

        if (lineBands.Count > 1)
        {
            var orphans = Math.Clamp(LayoutValues.Integer(parent.Css("orphans"), 2), 1, lineBands.Count);
            var widows = Math.Clamp(LayoutValues.Integer(parent.Css("widows"), 2), 1, lineBands.Count);
            if (orphans > 1)
            {
                destination.PreferredUnbroken.Add(new(lineBands[0].Start, lineBands[orphans - 1].End));
            }

            if (widows > 1)
            {
                destination.PreferredUnbroken.Add(new(lineBands[^widows].Start, lineBands[^1].End));
            }
        }
        destination.Height = Math.Max(destination.Height, y);
        return y;
    }

    private double TextWidth(Element el, string text) => assets.Measure(text, el.FontSize(), el.Bold(), el.Italic(), el.Family());

    private void Collect(Element el, double available, List<Atom> output, int depth)
    {
        if (depth > 192)
        {
            throw new InvalidOperationException("Inline nesting exceeds 192 levels.");
        }

        if (Hidden(el))
        {
            return;
        }

        if (el.Tag == "br")
        {
            output.Add(new Atom(el, "", 0, Break: true));
            return;
        }
        if (el.Tag == "#text")
        {
            var white = el.Css("white-space", "normal");
            var preserve = white is "pre" or "pre-wrap" or "break-spaces";
            var preserveLines = preserve || white == "pre-line";
            var value = el.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            value = el.Css("text-transform") switch
            {
                "uppercase" => value.ToUpperInvariant(),
                "lowercase" => value.ToLowerInvariant(),
                _ => value
            };
            if (white == "nowrap")
            {
                // Preserve inline run boundaries while collapsing internal white space.
                var parts = value.Split([' ', '\n', '\t', '\f'], StringSplitOptions.RemoveEmptyEntries);
                if (value.Length > 0 && IsSpace(value[0]))
                {
                    output.Add(new Atom(el, " ", TextWidth(el, " "), Space: true));
                }

                if (parts.Length > 0)
                {
                    var text = string.Join(' ', parts);
                    output.Add(new Atom(el, text, TextWidth(el, text)));
                }
                if (value.Length > 0 && IsSpace(value[^1]))
                {
                    output.Add(new Atom(el, " ", TextWidth(el, " "), Space: true));
                }

                return;
            }
            for (var i = 0; i < value.Length;)
            {
                if (preserveLines && value[i] == '\n')
                {
                    output.Add(new Atom(el, "", 0, Break: true));
                    i++;
                    continue;
                }
                var start = i;
                var isSpace = IsSpace(value[i++]);
                while (i < value.Length && IsSpace(value[i]) == isSpace && (!preserveLines || value[i] != '\n'))
                {
                    i++;
                }

                var text = isSpace && !preserve ? " " : value[start..i].Replace("\t", "    ", StringComparison.Ordinal);
                output.Add(new Atom(el, text, TextWidth(el, text), Space: isSpace && !preserve));
            }
            return;
        }
        if (el.Tag == "img" || Display(el) is "inline-block" or "inline-flex" or "inline-grid" or "inline-table")
        {
            var preferred = el.Tag == "img" ? ImageDimensions(el, available).Width : Math.Min(available, Intrinsic(el, false));
            var width = Sizing(el, "width", available, preferred);
            var child = Build(el, available, width, depth + 1);
            output.Add(new Atom(el, "", child.Width, Object: child));
            return;
        }
        foreach (var child in el.Children)
        {
            Collect(child, available, output, depth + 1);
        }
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\f';

    private (double Width, double Height) ImageDimensions(Element el, double available)
    {
        var found = assets.TryImage(el.Attr("src"), out var naturalWidth, out var naturalHeight);
        if (!found || naturalWidth <= 0 || naturalHeight <= 0)
        {
            return (0, 0);
        }

        var w = Sizing(el, "width", available, LayoutValues.Length(el.Attr("width"), available, el.FontSize(), double.NaN));
        var h = Sizing(el, "height", sheetHeight, LayoutValues.Length(el.Attr("height"), sheetHeight, el.FontSize(), double.NaN));
        if (!double.IsFinite(w))
        {
            w = double.IsFinite(h) ? naturalWidth * h / naturalHeight : naturalWidth;
        }

        if (!double.IsFinite(h))
        {
            h = naturalHeight * w / naturalWidth;
        }

        var maxW = Sizing(el, "max-width", available, double.PositiveInfinity);
        var maxH = Sizing(el, "max-height", sheetHeight, double.PositiveInfinity);
        var scale = Math.Min(1, Math.Min(maxW / Math.Max(w, Epsilon), maxH / Math.Max(h, Epsilon)));
        return (Math.Max(0, w * scale), Math.Max(0, h * scale));
    }

    private void Image(Element el, Box box, double available)
    {
        var dimensions = ImageDimensions(el, available);
        if (dimensions.Width <= 0 || dimensions.Height <= 0)
        {
            return;
        }

        box.Ink.Add(new DrawImage(0, 0, dimensions.Width, dimensions.Height, el.Attr("src")));
        box.Height = dimensions.Height;
        box.KeepTogether = true;
    }
}
