using System.Globalization;

namespace SuperHtmlToPdf;

internal sealed class CssSelector
{
    private sealed record Part(char Relation, List<Func<Element, bool>> Conditions, string Key, int Cost, string[] TestedAttributes);
    private readonly List<Part> _parts = [];
    internal long Weight
    {
        get; private set;
    }
    internal string? Pseudo
    {
        get; private set;
    }
    internal string IndexKey => _parts[^1].Key;

    internal bool Matches(Element node, Action<int> charge)
    {
        return MatchPart(node, _parts.Count - 1, charge);
    }

    private bool MatchPart(Element node, int index, Action<int> charge)
    {
        var part = _parts[index];
        charge(part.Cost);
        foreach (var name in part.TestedAttributes)
        {
            if (node.Attributes.TryGetValue(name, out var val))
            {
                charge(1 + val.Length / 16);
            }
        }
        foreach (var predicate in part.Conditions)
        {
            if (!predicate(node))
            {
                return false;
            }
        }
        if (index == 0)
        {
            return true;
        }
        var relation = _parts[index].Relation;
        var sibling = relation is '+' or '~';
        var candidate = sibling ? node.PreviousSibling : node.Parent;
        while (candidate is not null && candidate.Tag != "#document")
        {
            if (MatchPart(candidate, index - 1, charge))
            {
                return true;
            }
            if (relation is '>' or '+')
            {
                break;
            }
            candidate = sibling ? candidate.PreviousSibling : candidate.Parent;
        }
        return false;
    }

    internal static CssSelector? Parse(string text, int nesting = 0)
    {
        if (nesting > 4 || text.Length > 2048)
        {
            return null;
        }
        var result = new CssSelector();
        char relation = ' ';
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }
            if (result._parts.Count >= 32)
            {
                return null;
            }
            if (text[i] is '>' or '+' or '~')
            {
                if (result._parts.Count == 0)
                {
                    return null;
                }
                relation = text[i++];
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }
            }
            if (result.Pseudo is not null)
            {
                return null;
            }
            var start = i;
            var conditions = new List<Func<Element, bool>>();
            var tested = new List<string>();
            var key = "*";
            if (i < text.Length && text[i] == '*')
            {
                i++;
            }
            else if (i < text.Length && IsName(text[i]))
            {
                var name = ReadName(text, ref i).ToLowerInvariant();
                conditions.Add(e => e.Tag == name);
                key = name;
                result.Weight++;
            }
            while (i < text.Length && text[i] is '#' or '.' or '[' or ':')
            {
                var marker = text[i++];
                if (marker is '#' or '.')
                {
                    var name = ReadName(text, ref i);
                    if (name.Length == 0)
                    {
                        return null;
                    }
                    if (marker == '#')
                    {
                        tested.Add("id");
                        conditions.Add(e => e.Attributes.GetValueOrDefault("id") == name);
                        result.Weight += 1L << 32;
                        key = "#" + name;
                    }
                    else
                    {
                        tested.Add("class");
                        conditions.Add(e => ContainsWord(e.Attributes.GetValueOrDefault("class"), name, StringComparison.Ordinal));
                        result.Weight += 1L << 16;
                        if (!key.StartsWith('#'))
                        {
                            key = "." + name;
                        }
                    }
                    continue;
                }
                if (marker == '[')
                {
                    var end = CssSyntax.GroupEnd(text, i - 1);
                    if (end >= text.Length || !TryAttribute(text[i..end].Trim(), conditions))
                    {
                        return null;
                    }
                    var attributeText = text[i..end].TrimStart();
                    int attributeOffset = 0;
                    tested.Add(ReadName(attributeText, ref attributeOffset));
                    i = end + 1;
                    result.Weight += 1L << 16;
                    continue;
                }
                var doubled = i < text.Length && text[i] == ':';
                if (doubled)
                {
                    i++;
                }
                var pseudo = ReadName(text, ref i).ToLowerInvariant();
                string? args = null;
                if (i < text.Length && text[i] == '(')
                {
                    var end = CssSyntax.GroupEnd(text, i);
                    if (end >= text.Length)
                    {
                        return null;
                    }
                    args = text[(i + 1)..end].Trim();
                    i = end + 1;
                }
                if (pseudo is "before" or "after")
                {
                    result.Pseudo = pseudo;
                    result.Weight++;
                }
                else if (doubled || !result.TryPseudo(pseudo, args, conditions, tested, nesting))
                {
                    return null;
                }
            }
            if (i == start || (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('>' or '+' or '~')))
            {
                return null;
            }
            // The entire compound's length includes nested :is/:not predicates.
            // Charge their worst-case evaluation even on a positive match.
            result._parts.Add(new Part(relation, conditions, key, 1 + conditions.Count + (i - start) / 4, tested.ToArray()));
            relation = ' ';
        }
        return result._parts.Count == 0 ? null : result;
    }

    private bool TryPseudo(string name, string? args, List<Func<Element, bool>> conditions, List<string> tested, int nesting)
    {
        if (name is "not" or "is" or "where" && args is not null)
        {
            var choices = CssSyntax.Split(args, ',').Select(arg => Parse(arg, nesting + 1)).ToArray();
            if (choices.Length is 0 or > 32 || choices.Any(choice => choice is null || choice._parts.Count != 1 || choice.Pseudo is not null))
            {
                return false;
            }
            foreach (var choice in choices)
            {
                tested.AddRange(choice!._parts[0].TestedAttributes);
            }
            conditions.Add(element => choices.Any(choice => choice!._parts[0].Conditions.All(test => test(element))) != (name == "not"));
            if (name != "where")
            {
                Weight += choices.Max(choice => choice!.Weight);
            }
            return true;
        }
        Func<Element, bool>? test = name switch
        {
            "root" => e => e.Parent?.Tag == "#document",
            "first-child" => e => e.ElementIndex == 1,
            "last-child" => e => e.ElementIndex == e.Parent?.ElementCount,
            "only-child" => e => e.Parent?.ElementCount == 1,
            "empty" => e => e.Children.Count == 0,
            "link" or "any-link" => e => e.Tag == "a" && e.Attributes.ContainsKey("href"),
            "hover" or "active" or "focus" or "focus-visible" or "focus-within" or "visited" or "target" => _ => false,
            "checked" => e => e.Attributes.ContainsKey("checked") || e.Attributes.ContainsKey("selected"),
            "disabled" => e => e.Attributes.ContainsKey("disabled"),
            "nth-child" or "nth-last-child" when args is not null => Nth(args, name == "nth-last-child"),
            _ => null
        };
        if (test is null)
        {
            return false;
        }
        Weight += 1L << 16;
        conditions.Add(test);
        return true;
    }

    private static Func<Element, bool>? Nth(string expression, bool reverse)
    {
        var value = string.Concat(expression.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
        value = value switch
        {
            "odd" => "2n+1",
            "even" => "2n",
            _ => value
        };
        var pivot = value.IndexOf('n');
        var a = 0;
        var b = 0;
        if (pivot < 0)
        {
            if (!int.TryParse(value, CultureInfo.InvariantCulture, out b))
            {
                return null;
            }
        }
        else
        {
            var factor = value[..pivot];
            if (factor is "" or "+")
            {
                a = 1;
            }
            else if (factor == "-")
            {
                a = -1;
            }
            else if (!int.TryParse(factor, CultureInfo.InvariantCulture, out a))
            {
                return null;
            }
            if (pivot + 1 < value.Length && !int.TryParse(value[(pivot + 1)..], CultureInfo.InvariantCulture, out b))
            {
                return null;
            }
        }
        return e =>
        {
            long index = reverse ? (e.Parent?.ElementCount ?? 0) + 1 - e.ElementIndex : e.ElementIndex;
            return a == 0 ? index == b : (index - b) % a == 0 && (index - b) / a >= 0;
        };
    }

    private static bool TryAttribute(string input, List<Func<Element, bool>> conditions)
    {
        var i = 0;
        var name = ReadName(input, ref i);
        if (name.Length == 0)
        {
            return false;
        }
        var tail = input[i..].Trim();
        if (tail.Length == 0)
        {
            conditions.Add(e => e.Attributes.ContainsKey(name));
            return true;
        }
        var equals = tail.IndexOf('=');
        if (equals < 0 || equals > 1)
        {
            return false;
        }
        var operation = tail[..equals];
        var expected = tail[(equals + 1)..].Trim();
        var comparison = StringComparison.Ordinal;
        if (expected.EndsWith(" i", StringComparison.OrdinalIgnoreCase) || expected.EndsWith(" s", StringComparison.OrdinalIgnoreCase))
        {
            comparison = char.ToLowerInvariant(expected[^1]) == 'i' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            expected = expected[..^2].TrimEnd();
        }
        expected = CssSyntax.Unquote(expected);
        if (operation is not ("" or "~" or "|" or "^" or "$" or "*"))
        {
            return false;
        }
        conditions.Add(e => e.Attributes.TryGetValue(name, out var value) && operation switch
        {
            "" => value.Equals(expected, comparison),
            "~" => ContainsWord(value, expected, comparison),
            "|" => value.Equals(expected, comparison) || value.StartsWith(expected + "-", comparison),
            "^" => expected.Length != 0 && value.StartsWith(expected, comparison),
            "$" => expected.Length != 0 && value.EndsWith(expected, comparison),
            "*" => expected.Length != 0 && value.Contains(expected, comparison),
            _ => false
        });
        return true;
    }

    private static bool ContainsWord(string? words, string word, StringComparison comparison)
    {
        if (words is null)
        {
            return false;
        }
        foreach (var range in words.AsSpan().SplitAny(" \t\r\n\f"))
        {
            if (words.AsSpan(range).Equals(word, comparison))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsName(char c) => char.IsLetterOrDigit(c) || c is '-' or '_' || c > 127;
    private static string ReadName(string text, ref int i)
    {
        var start = i;
        while (i < text.Length && IsName(text[i]))
        {
            i++;
        }
        return text[start..i];
    }
}
