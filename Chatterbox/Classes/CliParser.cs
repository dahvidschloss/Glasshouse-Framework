using System;
using System.Collections.Generic;
using System.Text;

namespace ChatterBoxCLI;

public static class CliParser
{
    public readonly record struct PlaceholderInfo(string Name, string? CacheName, bool Optional);

    public static List<PlaceholderInfo> ExtractPlaceholders(string template)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(
            template,
            @"\$(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<opt>\?)?(?::&(?<cache>[A-Za-z0-9_\-]+))?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<PlaceholderInfo>();
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var name = m.Groups["name"].Value;
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
            var cache = m.Groups["cache"].Success ? m.Groups["cache"].Value : null;
            var optional = m.Groups["opt"].Success;
            list.Add(new PlaceholderInfo(name, cache, optional));
        }
        return list;
    }

    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var inQuote = false;
        char quote = '\0';

        var escape = false;
        foreach (var ch in text)
        {
            if (inQuote)
            {
                if (escape)
                {
                    sb.Append(ch);
                    escape = false;
                    continue;
                }
                if (ch == '\\')
                {
                    escape = true;
                    continue;
                }
                if (ch == quote)
                {
                    inQuote = false;
                    sb.Append(ch);
                    continue;
                }
                sb.Append(ch);
                continue;
            }

            if (ch == '"' || ch == '\'')
            {
                inQuote = true;
                quote = ch;
                sb.Append(ch);
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }
                continue;
            }

            sb.Append(ch);
        }

        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }

    public static Dictionary<string, string> ParseDashParams(List<string> tokens)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tokens.Count; i++)
        {
            var tok = tokens[i];
            if (!tok.StartsWith("-")) continue;
            var name = tok.TrimStart('-');
            string value = "true";
            if (i + 1 < tokens.Count && !tokens[i + 1].StartsWith("-"))
            {
                value = tokens[i + 1];
                i++;
            }
            map[name] = value;
        }
        return map;
    }
}
