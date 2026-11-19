using System.Globalization;
using System.Text;

namespace SuperHtmlToPdf;

internal sealed record CssDeclaration(string Name, string Value, bool Important)
{
    internal static List<CssDeclaration> Read(string input)
    {
        var result = new List<CssDeclaration>();
        foreach (var item in CssSyntax.Split(input, ';'))
        {
            var colon = item.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            var name = item[..colon].Trim();
            if (!name.StartsWith("--", StringComparison.Ordinal))
            {
                name = name.ToLowerInvariant();
            }
            var value = item[(colon + 1)..].Trim();
            var bang = value.LastIndexOf('!');
            var important = bang >= 0 && value.AsSpan(bang + 1).Trim().Equals("important", StringComparison.OrdinalIgnoreCase);
            if (important)
            {
                value = value[..bang].TrimEnd();
            }
            if (name.Length != 0 && value.Length != 0)
            {
                result.Add(new(name, value, important));
            }
        }
        return result;
    }
}

internal static class CssDeclarations
{
    private static readonly string[] Sides = ["top", "right", "bottom", "left"];
    internal static readonly HashSet<string> Inherited = new(StringComparer.Ordinal)
    {
        "color", "font-family", "font-size", "font-weight", "font-style", "line-height", "white-space",
        "text-align", "text-transform", "text-decoration", "letter-spacing", "word-spacing", "word-break", "overflow-wrap",
        "visibility", "list-style-type", "border-collapse", "border-spacing", "orphans", "widows", "direction"
    };

    private static readonly HashSet<string> Known = new((
        "display visibility color background background-color font font-family font-size font-weight font-style line-height " +
        "text-align vertical-align text-transform text-decoration text-decoration-line white-space word-break word-wrap overflow-wrap " +
        "letter-spacing word-spacing text-indent direction content width min-width max-width height min-height max-height " +
        "margin padding inset top right bottom left box-sizing border border-width border-style border-color " +
        "border-top border-right border-bottom border-left border-collapse border-spacing border-radius " +
        "table-layout caption-side float clear position overflow overflow-x overflow-y " +
        "flex flex-direction flex-wrap flex-flow flex-grow flex-shrink flex-basis align-items align-self align-content " +
        "justify-content justify-items justify-self place-items place-self place-content order gap column-gap row-gap " +
        "grid-template-columns grid-template-rows grid-template-areas grid-auto-columns grid-auto-rows grid-auto-flow " +
        "grid-column grid-row grid-column-start grid-column-end grid-row-start grid-row-end grid-area " +
        "break-before break-after break-inside page-break-before page-break-after page-break-inside widows orphans " +
        "list-style-type list-style size print-color-adjust -webkit-print-color-adjust").Split(' ', StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal);

    internal static bool IsKnown(string name)
    {
        if (Known.Contains(name))
        {
            return true;
        }
        foreach (var side in Sides)
        {
            if (name == "margin-" + side || name == "padding-" + side ||
                name == "border-" + side + "-color" || name == "border-" + side + "-width" || name == "border-" + side + "-style")
            {
                return true;
            }
        }
        return false;
    }

    internal static string? UnsupportedFunction(string value)
    {
        if (!value.Contains('('))
        {
            return null;
        }
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] is '\'' or '"')
            {
                i = CssSyntax.StringEnd(value, i);
                continue;
            }
            if (value[i] != '(')
            {
                continue;
            }
            var start = i;
            while (start > 0 && (char.IsAsciiLetter(value[start - 1]) || value[start - 1] == '-'))
            {
                start--;
            }
            if (i == start)
            {
                continue;
            }
            var name = value[start..i].ToLowerInvariant();
            if (name is not ("rgb" or "rgba" or "calc" or "repeat" or "minmax" or "attr"))
            {
                return name;
            }
        }
        return null;
    }

    internal static void Set(Dictionary<string, string> style, string name, string value)
    {
        style[name] = value;
        switch (name)
        {
            case "margin":
            case "padding":
            case "inset":
            case "border-width":
            case "border-style":
            case "border-color":
                var box = CssSyntax.Split(value, ' ');
                if (box.Count is < 1 or > 4)
                {
                    break;
                }
                string[] edges = [box[0], box.Count > 1 ? box[1] : box[0], box.Count > 2 ? box[2] : box[0],
                    box.Count == 4 ? box[3] : box.Count > 1 ? box[1] : box[0]];
                for (var i = 0; i < 4; i++)
                {
                    var key = name switch
                    {
                        "inset" => Sides[i],
                        "margin" or "padding" => name + "-" + Sides[i],
                        _ => "border-" + Sides[i] + name[6..]
                    };
                    style[key] = edges[i];
                }
                break;
            case "border":
            case "border-top":
            case "border-right":
            case "border-bottom":
            case "border-left":
                var width = "medium";
                var kind = "none";
                var color = "currentcolor";
                foreach (var token in CssSyntax.Split(value, ' '))
                {
                    if (token is "none" or "hidden" or "dotted" or "dashed" or "solid" or "double" or "inset" or "outset" or "ridge" or "groove")
                    {
                        kind = token;
                    }
                    else if (token is "thin" or "medium" or "thick" || StartsNumber(token))
                    {
                        width = token;
                    }
                    else
                    {
                        color = token;
                    }
                }
                foreach (var side in Sides)
                {
                    if (name != "border" && name != "border-" + side)
                    {
                        continue;
                    }
                    var prefix = "border-" + side;
                    style[prefix + "-width"] = kind is "none" or "hidden" ? "0" : width switch
                    {
                        "thin" => "1px",
                        "medium" => "3px",
                        "thick" => "5px",
                        _ => width
                    };
                    style[prefix + "-style"] = kind;
                    style[prefix + "-color"] = color;
                }
                break;
            case "background":
                style["background-color"] = value;
                break;
            case "page-break-inside":
            case "page-break-before":
            case "page-break-after":
                style[name[5..]] = value == "always" ? "page" : value;
                break;
            case "word-wrap":
                style["overflow-wrap"] = value;
                break;
            case "gap":
            case "place-content":
            case "place-items":
            case "place-self":
                var pair = CssSyntax.Split(value, ' ');
                if (pair.Count is 1 or 2)
                {
                    style[name == "gap" ? "row-gap" : name.Replace("place", "align")] = pair[0];
                    style[name == "gap" ? "column-gap" : name.Replace("place", "justify")] = pair[^1];
                }
                break;
            case "flex-flow":
                style["flex-direction"] = "row";
                style["flex-wrap"] = "nowrap";
                foreach (var token in CssSyntax.Split(value, ' '))
                {
                    style[token is "wrap" or "nowrap" or "wrap-reverse" ? "flex-wrap" : "flex-direction"] = token;
                }
                break;
            case "flex":
                var parts = CssSyntax.Split(value, ' ');
                if (parts.Count == 0)
                {
                    break;
                }
                var numbers = parts.Where(t => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _)).ToArray();
                style["flex-grow"] = value is "none" or "initial" ? "0" : numbers.FirstOrDefault() ?? "1";
                style["flex-shrink"] = value == "none" ? "0" : numbers.Skip(1).FirstOrDefault() ?? "1";
                style["flex-basis"] = value is "none" or "auto" or "initial" ? "auto" :
                    parts.Count == 3 ? parts[2] : parts.Except(numbers).FirstOrDefault() ?? "0%";
                break;
            case "grid-column":
            case "grid-row":
                var grid = CssSyntax.Split(value, '/');
                if (grid.Count > 0)
                {
                    style[name + "-start"] = grid[0];
                    style[name + "-end"] = grid.Count > 1 ? grid[1] : "auto";
                }
                break;
            case "font":
                SetFont(style, value);
                break;
        }
    }

    private static bool StartsNumber(string token) => token.Length > 0 && (char.IsAsciiDigit(token[0]) || token[0] is '.' or '+' or '-');

    private static void SetFont(Dictionary<string, string> style, string value)
    {
        var tokens = CssSyntax.Split(value, ' ');
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            var pair = tokens[i].Split('/', 2);
            if (!StartsNumber(pair[0]) || !pair[0].Any(c => char.IsLetter(c) || c == '%'))
            {
                continue;
            }
            style["font-size"] = pair[0];
            style["font-weight"] = tokens.Take(i).LastOrDefault(t => t == "bold" || double.TryParse(t, out _)) ?? "normal";
            style["font-style"] = tokens.Take(i).LastOrDefault(t => t is "italic" or "oblique") ?? "normal";
            style["line-height"] = pair.Length == 2 ? pair[1] : "normal";
            style["font-family"] = string.Join(' ', tokens.Skip(i + 1));
            return;
        }
    }

    internal static string? Variables(string value, Dictionary<string, string> variables, Action<int> charge)
    {
        if (!value.Contains("var(", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }
        var budget = 100_000;
        return Expand(value, new HashSet<string>(StringComparer.Ordinal), 0);

        string? Expand(string input, HashSet<string> path, int depth)
        {
            charge(input.Length);
            budget -= input.Length;
            if (depth > 16 || budget < 0)
            {
                return null;
            }
            var output = new StringBuilder();
            var start = 0;
            for (var i = 0; i < input.Length; i++)
            {
                if (input[i] is '"' or '\'')
                {
                    i = CssSyntax.StringEnd(input, i);
                    continue;
                }
                if (!input.AsSpan(i).StartsWith("var(", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var end = CssSyntax.GroupEnd(input, i + 3);
                if (end >= input.Length)
                {
                    return null;
                }
                var args = input[(i + 4)..end];
                var comma = args.IndexOf(',');
                var key = (comma < 0 ? args : args[..comma]).Trim();
                string? replacement = null;
                if (variables.TryGetValue(key, out var raw) && path.Add(key))
                {
                    replacement = Expand(raw, path, depth + 1);
                    path.Remove(key);
                }
                if (replacement is null && comma >= 0)
                {
                    replacement = Expand(args[(comma + 1)..].Trim(), path, depth + 1);
                }
                if (replacement is null)
                {
                    return null;
                }
                output.Append(input, start, i - start).Append(replacement);
                start = end + 1;
                i = end;
            }
            output.Append(input, start, input.Length - start);
            charge(output.Length);
            return output.ToString();
        }
    }
}
