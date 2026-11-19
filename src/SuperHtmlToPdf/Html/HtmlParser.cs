using System.Net;

namespace SuperHtmlToPdf;

internal static class HtmlParser
{
    private static readonly HashSet<string> VoidTags = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"
    };
    private static readonly HashSet<string> ParagraphClosers = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "div", "dl", "fieldset", "footer", "form", "h1", "h2",
        "h3", "h4", "h5", "h6", "header", "hr", "main", "nav", "ol", "p", "pre", "section", "table", "ul"
    };

    public static Element Parse(string html, int maxNodes, int maxDepth, CancellationToken cancellationToken)
    {
        var document = new Element("#document");
        var stack = new List<Element> { document };
        var position = 0;
        var nodeCount = 1;
        while (position < html.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = position;
            if (html[position] != '<')
            {
                position = html.IndexOf('<', position);
                position = position < 0 ? html.Length : position;
                Add(new Element("#text", WebUtility.HtmlDecode(html[start..position])));
                continue;
            }
            if (html.AsSpan(position).StartsWith("<!--", StringComparison.Ordinal))
            {
                var closing = html.IndexOf("-->", position + 4, StringComparison.Ordinal);
                position = closing < 0 ? html.Length : closing + 3;
                continue;
            }
            if (position + 1 < html.Length && html[position + 1] is '!' or '?')
            {
                var closing = html.IndexOf('>', position + 2);
                position = closing < 0 ? html.Length : closing + 1;
                continue;
            }
            position++;
            var end = position < html.Length && html[position] == '/';
            if (end)
            {
                position++;
            }
            var nameStart = position;
            if (position < html.Length && char.IsAsciiLetter(html[position]))
            {
                while (position < html.Length && IsTagChar(html[position]))
                {
                    position++;
                }
            }
            if (nameStart == position)
            {
                Add(new Element("#text", "<"));
                position = start + 1;
                continue;
            }
            var tag = html[nameStart..position].ToLowerInvariant();
            if (end)
            {
                var closing = html.IndexOf('>', position);
                position = closing < 0 ? html.Length : closing + 1;
                CloseNearest(stack, [tag], []);
                continue;
            }
            ImplicitClose(stack, tag);
            var element = new Element(tag);
            Add(element);
            var selfClosing = ReadAttributes(html, ref position, element.Attributes);
            if (VoidTags.Contains(tag) || selfClosing)
            {
                continue;
            }
            if (stack.Count > maxDepth)
            {
                throw new InvalidDataException("HTML nesting exceeds the configured limit.");
            }
            if (tag is "style" or "script" or "textarea" or "title")
            {
                var close = FindRawClose(html, tag, position);
                var raw = html[position..close];
                if (raw.Length > 0)
                {
                    stack.Add(element);
                    Add(new Element("#text", tag is "textarea" or "title" ? WebUtility.HtmlDecode(raw) : raw));
                    stack.RemoveAt(stack.Count - 1);
                }
                var closeEnd = close < html.Length ? html.IndexOf('>', close + 2 + tag.Length) : -1;
                position = closeEnd < 0 ? html.Length : closeEnd + 1;
                continue;
            }
            stack.Add(element);
        }
        return document;

        void Add(Element child)
        {
            if (++nodeCount > maxNodes)
            {
                throw new InvalidDataException("HTML node count exceeds the configured limit.");
            }
            stack[^1].Append(child);
        }
    }

    private static bool IsTagChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or ':' or '_';

    private static void ImplicitClose(List<Element> stack, string tag)
    {
        if (ParagraphClosers.Contains(tag))
        {
            CloseNearest(stack, ["p"], ["table", "td", "th", "section", "article", "body", "div"]);
        }
        switch (tag)
        {
            case "li":
                CloseNearest(stack, ["li"], ["ul", "ol"]);
                break;
            case "dt":
            case "dd":
                CloseNearest(stack, ["dt", "dd"], ["dl"]);
                break;
            case "tr":
                CloseNearest(stack, ["tr"], ["table", "thead", "tbody", "tfoot"]);
                break;
            case "td":
            case "th":
                CloseNearest(stack, ["td", "th"], ["tr", "table"]);
                break;
            case "thead":
            case "tbody":
            case "tfoot":
                CloseNearest(stack, ["thead", "tbody", "tfoot", "tr"], ["table"]);
                break;
            case "option":
                CloseNearest(stack, ["option"], ["select"]);
                break;
            case "body":
                CloseNearest(stack, ["head"], ["html"]);
                break;
        }
    }

    private static void CloseNearest(List<Element> stack, ReadOnlySpan<string> names, ReadOnlySpan<string> boundaries)
    {
        for (var i = stack.Count - 1; i > 0; i--)
        {
            var candidate = stack[i].Tag;
            if (names.Contains(candidate))
            {
                stack.RemoveRange(i, stack.Count - i);
                return;
            }
            if (boundaries.Contains(candidate))
            {
                return;
            }
        }
    }

    private static int FindRawClose(string html, string tag, int position)
    {
        var marker = "</" + tag;
        while ((position = html.IndexOf(marker, position, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var next = position + marker.Length;
            if (next == html.Length || html[next] == '>' || char.IsWhiteSpace(html[next]))
            {
                return position;
            }
            position = next;
        }
        return html.Length;
    }

    private static bool ReadAttributes(string html, ref int i, Dictionary<string, string> attributes)
    {
        while (i < html.Length)
        {
            SkipSpace(html, ref i);
            if (i == html.Length)
            {
                break;
            }
            if (html[i] == '>')
            {
                i++;
                break;
            }
            if (html[i] == '/' && i + 1 < html.Length && html[i + 1] == '>')
            {
                i += 2;
                return true;
            }
            var start = i;
            while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] is not ('=' or '/' or '>'))
            {
                i++;
            }
            if (i == start)
            {
                i++;
                continue;
            }
            var name = html[start..i];
            SkipSpace(html, ref i);
            var value = "";
            if (i < html.Length && html[i] == '=')
            {
                i++;
                SkipSpace(html, ref i);
                if (i < html.Length && html[i] is '"' or '\'')
                {
                    var quote = html[i++];
                    var end = html.IndexOf(quote, i);
                    end = end < 0 ? html.Length : end;
                    value = html[i..end];
                    i = Math.Min(end + 1, html.Length);
                }
                else
                {
                    start = i;
                    while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>')
                    {
                        i++;
                    }
                    value = html[start..i];
                }
            }
            attributes.TryAdd(name, WebUtility.HtmlDecode(value));
        }
        return false;
    }

    private static void SkipSpace(string input, ref int i)
    {
        while (i < input.Length && char.IsWhiteSpace(input[i]))
        {
            i++;
        }
    }
}
