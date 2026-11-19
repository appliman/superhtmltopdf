using System.Text;

namespace SuperHtmlToPdf;

internal static class CssSyntax
{
    // Delimiters inside strings, selectors and CSS functions belong to the value.
    internal static List<string> Split(string text, char delimiter)
    {
        var result = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '\'' or '"')
            {
                i = StringEnd(text, i);
                continue;
            }
            if (text[i] is '(' or '[' or '{')
            {
                i = GroupEnd(text, i);
                continue;
            }
            if (delimiter == ' ' ? !char.IsWhiteSpace(text[i]) : text[i] != delimiter)
            {
                continue;
            }
            if (i > start)
            {
                if (result.Count >= 16_384)
                {
                    throw new InvalidDataException("CSS list exceeds the token limit.");
                }
                result.Add(text[start..i].Trim());
            }
            start = i + 1;
        }
        if (start < text.Length)
        {
            result.Add(text[start..].Trim());
        }
        return result;
    }

    internal static int GroupEnd(string text, int open)
    {
        var left = text[open];
        var right = left switch
        {
            '(' => ')',
            '{' => '}',
            '[' => ']',
            _ => throw new ArgumentException(nameof(open))
        };
        var count = 1;
        for (var i = open + 1; i < text.Length; i++)
        {
            if (text[i] is '"' or '\'')
            {
                i = StringEnd(text, i);
                continue;
            }
            if (text[i] == '\\')
            {
                i++;
                continue;
            }
            if (text[i] == left)
            {
                count++;
            }
            if (text[i] == right && --count == 0)
            {
                return i;
            }
        }
        return text.Length;
    }

    internal static int StringEnd(string text, int open)
    {
        for (var i = open + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }
            if (text[i] == text[open])
            {
                return i;
            }
        }
        return text.Length;
    }

    internal static string StripComments(string input)
    {
        if (!input.Contains("/*", StringComparison.Ordinal))
        {
            return input;
        }
        var result = new StringBuilder(input.Length);
        var start = 0;
        for (var i = 0; i < input.Length - 1; i++)
        {
            if (input[i] is '"' or '\'')
            {
                i = StringEnd(input, i);
                continue;
            }
            if (input[i] != '/' || input[i + 1] != '*')
            {
                continue;
            }
            result.Append(input, start, i - start);
            var end = input.IndexOf("*/", i + 2, StringComparison.Ordinal);
            start = end < 0 ? input.Length : end + 2;
            i = start - 1;
        }
        result.Append(input, start, input.Length - start);
        return result.ToString();
    }

    internal static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
        {
            return value[1..^1];
        }
        return value;
    }
}
