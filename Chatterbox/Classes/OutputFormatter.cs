using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ChatterBoxCLI;

public enum OutputMode
{
    Table,
    Json,
    FullJson
}

public static class OutputFormatter
{
    public static bool TryExtractValueList(JsonElement response, out List<string> list)
    {
        list = new List<string>();
        var element = response;
        if (TryGetProperty(response, "result", out var result))
        {
            element = result;
        }
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("result", out var innerResult))
        {
            element = innerResult;
        }

        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("type", out var typeEl) &&
            element.TryGetProperty("value", out var valueEl))
        {
            var type = typeEl.GetString() ?? "";
            if (type == "string" && valueEl.ValueKind == JsonValueKind.String)
            {
                var raw = valueEl.GetString() ?? "";
                var parsed = TryParseJsonString(raw);
                if (parsed.HasValue && TryListFromArray(parsed.Value, list))
                {
                    return true;
                }
            }
            if (valueEl.ValueKind == JsonValueKind.Array && TryListFromArray(valueEl, list))
            {
                return true;
            }
        }

        if (element.ValueKind == JsonValueKind.Array && TryListFromArray(element, list))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("value", out var objValue) &&
            objValue.ValueKind == JsonValueKind.Array &&
            TryListFromArray(objValue, list))
        {
            return true;
        }

        return false;
    }

    public static string Format(JsonElement response, string? reqLabel, OutputMode mode, int width = 120)
    {
        width = Math.Max(80, width);
        if (mode == OutputMode.FullJson)
        {
            return "[OUT] " + ToPrettyJson(response);
        }

        if (TryGetProperty(response, "error", out var err))
        {
            var code = err.TryGetProperty("code", out var c) ? c.ToString() : "ERR";
            var msg = err.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";
            return $"[OUT] {reqLabel ?? "id"} ERROR {code} {msg}";
        }

        var hasResult = TryGetProperty(response, "result", out var result);
        if (!hasResult)
        {
            return "[OUT] " + (mode == OutputMode.Json ? ToPrettyJson(response) : ToPrettyJson(response));
        }

        if (IsEmptyObject(result))
        {
            return $"[OUT] {(reqLabel ?? "Success")}";
        }

        if (mode == OutputMode.Json)
        {
            return "[OUT] " + ToPrettyJson(result);
        }

        var table = FormatTableLike(result, width);
        return table.Length == 0 ? "[OUT] " + ToPrettyJson(result) : table;
    }

    private static string FormatTableLike(JsonElement element, int width)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("result", out var innerResult))
        {
            return FormatTableLike(innerResult, width);
        }

        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("type", out var typeEl) &&
            element.TryGetProperty("value", out var valueEl))
        {
            var type = typeEl.GetString() ?? "";
            if (type == "string" && valueEl.ValueKind == JsonValueKind.String)
            {
                var raw = valueEl.GetString() ?? "";
                var parsed = TryParseJsonString(raw);
                if (parsed.HasValue)
                {
                    return FormatElement(parsed.Value, width, 0, 3);
                }
            }

            var desc = element.TryGetProperty("description", out var descEl) ? descEl.GetString() : null;
            return FormatKeyValueTable(new Dictionary<string, string?>
            {
                ["Type"] = type,
                ["Value"] = valueEl.ToString(),
                ["Description"] = desc
            }, width);
        }

        return FormatElement(element, width, 0, 3);
    }

    private static string FormatElement(JsonElement element, int width, int depth, int maxDepth)
    {
        if (depth > maxDepth) return ToPrettyJson(element);

        return element.ValueKind switch
        {
            JsonValueKind.Array => FormatArray(element, width, depth, maxDepth),
            JsonValueKind.Object => FormatObject(element, width, depth, maxDepth),
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "<null>",
            _ => element.ToString()
        };
    }

    private static string FormatArray(JsonElement array, int width, int depth, int maxDepth)
    {
        var items = array.EnumerateArray().ToList();
        if (items.Count == 0) return "";

        if (items.All(i => i.ValueKind != JsonValueKind.Object && i.ValueKind != JsonValueKind.Array))
        {
            var rows = items.Select(i => new Dictionary<string, string?> { ["Value"] = i.ToString() });
            return FormatTable(rows, new[] { "Value" }, width);
        }

        if (items.All(i => i.ValueKind == JsonValueKind.Object))
        {
            var columns = new LinkedHashSet<string>();
            foreach (var it in items)
            {
                foreach (var prop in it.EnumerateObject())
                {
                    columns.Add(prop.Name);
                }
            }

            var rows = items.Select(it =>
            {
                var row = new Dictionary<string, string?>();
                foreach (var col in columns)
                {
                    if (it.TryGetProperty(col, out var v))
                    {
                        row[col] = v.ValueKind == JsonValueKind.Object || v.ValueKind == JsonValueKind.Array
                            ? v.ToString()
                            : v.ToString();
                    }
                    else
                    {
                        row[col] = "<null>";
                    }
                }
                return row;
            });
            return FormatTable(rows, columns.ToArray(), width);
        }

        var sb = new StringBuilder();
        foreach (var it in items)
        {
            sb.AppendLine(FormatElement(it, width, depth + 1, maxDepth));
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatObject(JsonElement obj, int width, int depth, int maxDepth)
    {
        var scalar = new Dictionary<string, string?>();
        var complex = new List<(string Name, JsonElement Value)>();

        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var raw = prop.Value.GetString() ?? "";
                var parsed = TryParseJsonString(raw);
                if (parsed.HasValue)
                {
                    complex.Add((prop.Name, parsed.Value));
                    continue;
                }
            }

            if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                complex.Add((prop.Name, prop.Value));
            }
            else
            {
                scalar[prop.Name] = prop.Value.ToString();
            }
        }

        var sb = new StringBuilder();
        if (scalar.Count > 0)
        {
            sb.AppendLine(FormatKeyValueTable(scalar, width));
        }

        foreach (var (name, value) in complex)
        {
            sb.AppendLine($"{new string(' ', depth * 2)}{name}:");
            var formatted = FormatElement(value, width, depth + 1, maxDepth);
            foreach (var line in formatted.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                sb.AppendLine($"{new string(' ', (depth + 1) * 2)}{line}");
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatKeyValueTable(Dictionary<string, string?> rows, int width)
    {
        var data = rows.Select(kv => new Dictionary<string, string?> { ["Key"] = kv.Key, ["Value"] = kv.Value });
        return FormatTable(data, new[] { "Key", "Value" }, width);
    }

    private static string FormatTable(IEnumerable<Dictionary<string, string?>> rows, string[] columns, int width)
    {
        var list = rows.ToList();
        if (list.Count == 0) return "";

        var colWidths = columns.ToDictionary(c => c, c => c.Length);
        var maxColWidth = Math.Max(12, (width - (columns.Length - 1)) / columns.Length);
        foreach (var row in list)
        {
            foreach (var col in columns)
            {
                var val = row.TryGetValue(col, out var v) ? v ?? "" : "";
                colWidths[col] = Math.Max(colWidths[col], Math.Min(val.Length, maxColWidth));
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(" ", columns.Select(c => c.PadRight(colWidths[c]))));
        sb.AppendLine(string.Join(" ", columns.Select(c => new string('-', colWidths[c]))));

        foreach (var row in list)
        {
            var wrappedCols = new List<List<string>>();
            foreach (var col in columns)
            {
                var val = row.TryGetValue(col, out var v) ? v ?? "" : "";
                wrappedCols.Add(Wrap(val, colWidths[col]));
            }

            var maxLines = wrappedCols.Max(w => w.Count);
            for (var i = 0; i < maxLines; i++)
            {
                var line = new List<string>();
                for (var c = 0; c < columns.Length; c++)
                {
                    var chunk = i < wrappedCols[c].Count ? wrappedCols[c][i] : "";
                    line.Add(chunk.PadRight(colWidths[columns[c]]));
                }
                sb.AppendLine(string.Join(" ", line));
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static List<string> Wrap(string text, int width)
    {
        if (string.IsNullOrEmpty(text)) return new List<string> { "" };
        if (text.Length <= width) return new List<string> { text };

        var lines = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var len = Math.Min(width, text.Length - i);
            lines.Add(text.Substring(i, len));
            i += len;
        }
        return lines;
    }

    private static string ToPrettyJson(JsonElement element)
    {
        return JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
            return true;
        value = default;
        return false;
    }

    private static bool IsEmptyObject(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object && !element.EnumerateObject().Any();
    }

    private static JsonElement? TryParseJsonString(string raw)
    {
        var trimmed = raw.TrimStart();
        if (!(trimmed.StartsWith("{") || trimmed.StartsWith("["))) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static bool TryListFromArray(JsonElement array, List<string> list)
    {
        if (array.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in array.EnumerateArray())
        {
            list.Add(item.ToString());
        }
        return list.Count > 0;
    }

    private sealed class LinkedHashSet<T> : HashSet<T>
    {
        private readonly List<T> _order = new();
        public new bool Add(T item)
        {
            if (base.Add(item))
            {
                _order.Add(item);
                return true;
            }
            return false;
        }
        public T[] ToArray() => _order.ToArray();
    }
}
