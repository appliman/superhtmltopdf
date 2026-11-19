using System.Globalization;
using System.Text;

namespace SuperHtmlToPdf;

internal sealed class CssCascade(Action<string, string> warn, CancellationToken cancellationToken)
{
    private sealed record Rule(CssSelector Selector, List<CssDeclaration> Declarations, int Order);
    private readonly record struct Candidate(CssDeclaration Declaration, long Specificity, int Order, int Index);
    private readonly Dictionary<string, List<Rule>> _rules = new(StringComparer.Ordinal);
    private readonly List<Candidate> _page = [];
    private readonly HashSet<(string Code, string Message)> _warnings = [];
    private int _order;
    private long _work;
    private long _stringWork;

    internal static Dictionary<string, string> Apply(Element root, IReadOnlyList<string> sheets,
        Action<string, string> warn, CancellationToken cancellationToken)
    {
        var cascade = new CssCascade(warn, cancellationToken);
        foreach (var sheet in sheets)
        {
            cascade.ReadRules(CssSyntax.StripComments(sheet), 0);
        }
        cascade.Visit(root, [], 12);
        var page = new Dictionary<string, string>(StringComparer.Ordinal);
        cascade.ApplyCandidates(page, cascade._page, [], null);
        return page;
    }

    private void Tick(int cost = 1)
    {
        _work += cost;
        if (_work > 20_000_000)
        {
            throw new InvalidDataException("CSS selector and declaration evaluation exceeds the operation limit.");
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void ChargeStrings(int count)
    {
        _stringWork += count;
        if (_stringWork > 64_000_000)
        {
            throw new InvalidDataException("CSS variable expansion exceeds the document limit.");
        }
    }

    private void Warn(string code, string message)
    {
        if (_warnings.Count < 1024 && _warnings.Add((code, message)))
        {
            warn(code, message);
        }
    }

    private void ReadRules(string css, int level)
    {
        if (level > 12)
        {
            throw new InvalidDataException("CSS nesting exceeds the limit.");
        }
        for (var i = 0; i < css.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (i < css.Length && char.IsWhiteSpace(css[i]))
            {
                i++;
            }
            var start = i;
            while (i < css.Length && css[i] is not ('{' or ';' or '}'))
            {
                if (css[i] is '\'' or '"')
                {
                    i = CssSyntax.StringEnd(css, i);
                }
                else if (css[i] is '[' or '(')
                {
                    i = CssSyntax.GroupEnd(css, i);
                }
                i++;
            }
            if (i >= css.Length)
            {
                break;
            }
            var head = css[start..i].Trim();
            if (css[i] != '{')
            {
                if (head.StartsWith('@') && !head.StartsWith("@charset", StringComparison.OrdinalIgnoreCase))
                {
                    Warn("css-at-rule", $"Unsupported CSS rule {head.Split(' ')[0]}.");
                }
                i++;
                continue;
            }
            var end = CssSyntax.GroupEnd(css, i);
            var body = css[(i + 1)..end];
            i = end + 1;
            if (head.StartsWith("@media", StringComparison.OrdinalIgnoreCase))
            {
                if (MatchesMedia(head[6..]))
                {
                    ReadRules(body, level + 1);
                }
            }
            else if (head.Equals("@page", StringComparison.OrdinalIgnoreCase))
            {
                var pageRules = CssDeclaration.Read(body);
                if (_page.Count + pageRules.Count > 8192)
                {
                    throw new InvalidDataException("Too many @page declarations.");
                }
                for (var j = 0; j < pageRules.Count; j++)
                {
                    _page.Add(new(pageRules[j], 0, _order, j));
                }
                _order++;
            }
            else if (head.StartsWith('@'))
            {
                Warn("css-at-rule", $"Unsupported CSS rule {head.Split(' ')[0]}.");
            }
            else
            {
                var declarations = CssDeclaration.Read(body);
                foreach (var selectorText in CssSyntax.Split(head, ','))
                {
                    var selector = CssSelector.Parse(selectorText);
                    if (selector is null)
                    {
                        Warn("css-selector", $"Unsupported selector {selectorText[..Math.Min(selectorText.Length, 120)]}.");
                        continue;
                    }
                    if (_order >= 100_000)
                    {
                        throw new InvalidDataException("CSS rule count exceeds the limit.");
                    }
                    if (!_rules.TryGetValue(selector.IndexKey, out var rules))
                    {
                        rules = [];
                        _rules.Add(selector.IndexKey, rules);
                    }
                    rules.Add(new(selector, declarations, _order++));
                }
            }
        }
    }

    private bool MatchesMedia(string query)
    {
        foreach (var clause in CssSyntax.Split(query.ToLowerInvariant(), ','))
        {
            var term = clause.StartsWith("only ", StringComparison.Ordinal) ? clause[5..].Trim() : clause;
            if (term is "print" or "all" or "not screen")
            {
                return true;
            }
            if (term is "screen" or "not print" or "not all" || term.StartsWith("screen and ", StringComparison.Ordinal))
            {
                continue;
            }
            Warn("css-media", $"Unsupported print media condition {term[..Math.Min(term.Length, 100)]}.");
        }
        return false;
    }

    private void Visit(Element node, Dictionary<string, string> inheritedVariables, double rootSize)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var style = node.Style;
        var parent = node.Parent?.Style;
        Inherit(style, parent);
        ApplyDefaults(node);
        if (node.Tag == "#text")
        {
            return;
        }
        var normal = new List<Candidate>();
        var before = new List<Candidate>();
        var after = new List<Candidate>();
        if (node.Tag != "#document")
        {
            foreach (var rule in CandidateRules(node))
            {
                if (!rule.Selector.Matches(node, Tick))
                {
                    continue;
                }
                var target = rule.Selector.Pseudo switch
                {
                    "before" => before,
                    "after" => after,
                    _ => normal
                };
                for (var i = 0; i < rule.Declarations.Count; i++)
                {
                    if (target.Count >= 16_384)
                    {
                        throw new InvalidDataException("Too many matching CSS declarations for an element.");
                    }
                    Tick();
                    target.Add(new(rule.Declarations[i], rule.Selector.Weight, rule.Order, i));
                }
            }
        }
        if (node.Attributes.TryGetValue("style", out var inline))
        {
            var declarations = CssDeclaration.Read(CssSyntax.StripComments(inline));
            for (var i = 0; i < declarations.Count; i++)
            {
                normal.Add(new(declarations[i], 1L << 56, _order, i));
            }
        }
        var variables = ApplyCandidates(style, normal, inheritedVariables, parent);
        var parentSize = ParentFontSize(parent);
        var fontSize = ResolveFontSize(style.GetValueOrDefault("font-size"), parentSize, rootSize);
        style["font-size"] = Points(fontSize);
        if (node.Parent?.Tag == "#document" && node.Tag == "html")
        {
            rootSize = fontSize;
        }
        if (style.TryGetValue("line-height", out var lineHeight) && lineHeight != "normal" &&
            !double.TryParse(lineHeight, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            style["line-height"] = Points(ResolveFontSize(lineHeight, fontSize, rootSize));
        }
        foreach (var child in node.Children)
        {
            Visit(child, variables, rootSize);
        }
        // Structural selectors count only original HTML elements, never generated boxes.
        Generate("before", before);
        Generate("after", after);

        void Generate(string side, List<Candidate> rules)
        {
            if (rules.Count == 0)
            {
                return;
            }
            var pseudo = new Element("span");
            Inherit(pseudo.Style, style);
            pseudo.Style["display"] = "inline";
            ApplyCandidates(pseudo.Style, rules, variables, style);
            if (!pseudo.Style.TryGetValue("content", out var raw) || raw is "normal" or "none")
            {
                return;
            }
            if (!TryContent(raw, node, out var content))
            {
                Warn("css-content", "Unsupported generated content.");
                return;
            }
            pseudo.Style["font-size"] = Points(ResolveFontSize(pseudo.Style.GetValueOrDefault("font-size"), fontSize, rootSize));
            if (content.Length > 0)
            {
                var child = new Element("#text", content);
                Inherit(child.Style, pseudo.Style);
                pseudo.Append(child);
            }
            if (side == "before")
            {
                node.Children.Insert(0, pseudo);
            }
            else
            {
                node.Children.Add(pseudo);
            }
        }
    }

    private IEnumerable<Rule> CandidateRules(Element node)
    {
        if (_rules.TryGetValue("*", out var wild))
        {
            foreach (var rule in wild)
            {
                yield return rule;
            }
        }
        if (_rules.TryGetValue(node.Tag, out var typed))
        {
            foreach (var rule in typed)
            {
                yield return rule;
            }
        }
        if (node.Attributes.TryGetValue("id", out var id) && _rules.TryGetValue("#" + id, out var byId))
        {
            foreach (var rule in byId)
            {
                yield return rule;
            }
        }
        if (node.Attributes.TryGetValue("class", out var classes))
        {
            foreach (var item in classes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal))
            {
                if (_rules.TryGetValue("." + item, out var byClass))
                {
                    foreach (var rule in byClass)
                    {
                        yield return rule;
                    }
                }
            }
        }
    }

    private Dictionary<string, string> ApplyCandidates(Dictionary<string, string> style, List<Candidate> candidates,
        Dictionary<string, string> inheritedVariables, Dictionary<string, string>? parent)
    {
        candidates.Sort((a, b) =>
        {
            var rank = a.Declaration.Important.CompareTo(b.Declaration.Important);
            if (rank == 0)
            {
                rank = a.Specificity.CompareTo(b.Specificity);
            }
            if (rank == 0)
            {
                rank = a.Order.CompareTo(b.Order);
            }
            return rank == 0 ? a.Index.CompareTo(b.Index) : rank;
        });
        var variables = inheritedVariables;
        foreach (var candidate in candidates)
        {
            var declaration = candidate.Declaration;
            if (!declaration.Name.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }
            if (ReferenceEquals(variables, inheritedVariables))
            {
                Tick(1 + inheritedVariables.Count);
                variables = new(inheritedVariables, StringComparer.Ordinal);
            }
            variables[declaration.Name] = declaration.Value;
        }
        if (!ReferenceEquals(variables, inheritedVariables))
        {
            // Resolve custom properties at the element that declares them. A child
            // overriding --color must not change the inherited computed --alias.
            var computed = new Dictionary<string, string>(variables, StringComparer.Ordinal);
            Tick(1 + variables.Count);
            foreach (var property in variables)
            {
                if (!property.Value.Contains("var(", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var saved = CssDeclarations.Variables(property.Value, variables, ChargeStrings);
                if (saved is null)
                {
                    computed.Remove(property.Key);
                }
                else
                {
                    computed[property.Key] = saved;
                }
            }
            variables = computed;
        }
        foreach (var candidate in candidates)
        {
            var (name, raw, _) = candidate.Declaration;
            if (name.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }
            ChargeStrings(raw.Length);
            if (!CssDeclarations.IsKnown(name))
            {
                Warn("css-property", $"Unsupported CSS property {name[..Math.Min(name.Length, 100)]}.");
                continue;
            }
            var value = CssDeclarations.Variables(raw, variables, ChargeStrings);
            if (value is null)
            {
                Warn("css-variable", $"Unresolved CSS variable in {name}.");
                continue;
            }
            if (value is "inherit" or "initial" or "unset" or "revert")
            {
                var inherit = value == "inherit" || (value == "unset" && CssDeclarations.Inherited.Contains(name));
                value = inherit ? parent?.GetValueOrDefault(name) : null;
                if (value is null)
                {
                    style.Remove(name);
                    continue;
                }
            }
            if (CssDeclarations.UnsupportedFunction(value) is { } unsupported)
            {
                Warn("css-value", $"Unsupported CSS function {unsupported} in {name}.");
                continue;
            }
            CssDeclarations.Set(style, name, value);
        }
        return variables;
    }

    private static void Inherit(Dictionary<string, string> style, Dictionary<string, string>? parent)
    {
        if (parent is null)
        {
            style["font-size"] = "12pt";
            style["font-family"] = "sans-serif";
            style["color"] = "#000000";
            return;
        }
        foreach (var property in CssDeclarations.Inherited)
        {
            if (parent.TryGetValue(property, out var value))
            {
                style[property] = value;
            }
        }
    }

    private static void ApplyDefaults(Element node)
    {
        var s = node.Style;
        var tag = node.Tag;
        s["display"] = tag switch
        {
            "head" or "title" or "meta" or "link" or "script" or "style" or "base" or "template" or "noscript" => "none",
            "table" => "table",
            "tbody" => "table-row-group",
            "thead" => "table-header-group",
            "tfoot" => "table-footer-group",
            "tr" => "table-row",
            "td" or "th" => "table-cell",
            "col" => "table-column",
            "colgroup" => "table-column-group",
            "caption" => "table-caption",
            "li" => "list-item",
            "#document" or "html" or "body" or "main" or "div" or "section" or "article" or "header" or "footer" or
            "aside" or "address" or "nav" or "form" or "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or
            "ul" or "ol" or "dl" or "dd" or "dt" or "pre" or "hr" or "figure" or "figcaption" or "blockquote" or "fieldset" => "block",
            _ => "inline"
        };
        if (tag is "b" or "strong" or "th")
        {
            s["font-weight"] = "bold";
        }
        if (tag is "i" or "em" or "cite")
        {
            s["font-style"] = "italic";
        }
        if (tag == "th")
        {
            s["text-align"] = "center";
        }
        if (tag is "sub" or "sup" or "small")
        {
            s["font-size"] = ".83em";
        }
        if (tag == "u")
        {
            s["text-decoration"] = "underline";
        }
        if (tag == "pre")
        {
            s["white-space"] = "pre";
            s["font-family"] = "monospace";
        }
        if (tag == "p")
        {
            CssDeclarations.Set(s, "margin", "1em 0");
        }
        if (tag == "body")
        {
            CssDeclarations.Set(s, "margin", "8px");
        }
        if (tag is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
        {
            s["font-weight"] = "bold";
            s["font-size"] = tag switch
            {
                "h1" => "2em",
                "h2" => "1.5em",
                "h3" => "1.17em",
                "h5" => ".83em",
                "h6" => ".67em",
                _ => "1em"
            };
            CssDeclarations.Set(s, "margin", ".67em 0");
        }
        if (node.Attributes.ContainsKey("hidden"))
        {
            s["display"] = "none";
        }
        foreach (var key in new[] { "width", "height", "align", "valign", "bgcolor", "cellspacing" })
        {
            if (!node.Attributes.TryGetValue(key, out var value))
            {
                continue;
            }
            if (key is "width" or "height" or "cellspacing" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                value += "px";
            }
            s[key switch
            {
                "align" => "text-align",
                "valign" => "vertical-align",
                "bgcolor" => "background-color",
                "cellspacing" => "border-spacing",
                _ => key
            }] = value;
        }
        if (tag == "table" && node.Attributes.TryGetValue("border", out var border) && int.TryParse(border, out var width) && width >= 0)
        {
            CssDeclarations.Set(s, "border", $"{width}px solid currentcolor");
        }
        if (tag is "td" or "th")
        {
            var ancestor = node.Parent;
            while (ancestor is not null && ancestor.Tag != "table")
            {
                ancestor = ancestor.Parent;
            }
            if (ancestor?.Attributes.TryGetValue("cellpadding", out var pad) == true && int.TryParse(pad, out var padding) && padding >= 0)
            {
                CssDeclarations.Set(s, "padding", $"{padding}px");
            }
        }
    }

    private static double ParentFontSize(Dictionary<string, string>? parent)
    {
        var value = parent?.GetValueOrDefault("font-size");
        return value?.EndsWith("pt", StringComparison.Ordinal) == true &&
            double.TryParse(value.AsSpan(0, value.Length - 2), CultureInfo.InvariantCulture, out var size) ? size : 12;
    }

    private static double ResolveFontSize(string? input, double parent, double root)
    {
        if (input is null)
        {
            return parent;
        }
        var value = input.Trim().ToLowerInvariant();
        var pivot = 0;
        while (pivot < value.Length && (char.IsAsciiDigit(value[pivot]) || value[pivot] is '.' or '-' or '+'))
        {
            pivot++;
        }
        if (!double.TryParse(value.AsSpan(0, pivot), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return value switch
            {
                "larger" => parent * 1.2,
                "smaller" => parent / 1.2,
                "small" => 10,
                "large" => 14,
                "medium" => 12,
                _ => parent
            };
        }
        var size = value[pivot..] switch
        {
            "px" => number * .75,
            "pt" => number,
            "pc" => number * 12,
            "in" => number * 72,
            "cm" => number * 72 / 2.54,
            "mm" => number * 72 / 25.4,
            "%" => parent * number / 100,
            "em" => parent * number,
            "rem" => root * number,
            "" when number == 0 => 0,
            _ => parent
        };
        return double.IsFinite(size) ? Math.Clamp(size, 0, 100_000) : parent;
    }

    private static string Points(double value) => value.ToString("0.######", CultureInfo.InvariantCulture) + "pt";

    private static bool TryContent(string raw, Element element, out string content)
    {
        var text = new StringBuilder();
        foreach (var part in CssSyntax.Split(raw, ' '))
        {
            if (part.Length >= 2 && part[0] is '\'' or '"' && part[^1] == part[0])
            {
                text.Append(CssSyntax.Unquote(part));
            }
            else if (part.StartsWith("attr(", StringComparison.OrdinalIgnoreCase) && part.EndsWith(')'))
            {
                text.Append(element.Attributes.GetValueOrDefault(part[5..^1].Trim()));
            }
            else
            {
                content = "";
                return false;
            }
        }
        content = text.ToString();
        return true;
    }
}
