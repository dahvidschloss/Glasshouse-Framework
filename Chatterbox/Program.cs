using System.Text;
using System.Text.Json;
using ChatterBoxCLI;
using System.Linq;

var state = new CliState();
var domains = new DomainContextManager();
var profiles = new ProfileManager();
var consoleLock = new object();
var inputState = new InputState();
var history = new List<string>();
var historyIndex = -1;

for (var i = 0; i < args.Length; i++)
{
    var arg = args[i];
    if (arg.Equals("--wsUrl", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        state.WsUrl = args[++i];
    }
    else if (arg.Equals("--cdpAddress", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        state.CdpAddress = args[++i];
    }
    else if (arg.Equals("--cdpPort", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        if (int.TryParse(args[++i], out var p)) state.CdpPort = p;
    }
}

Console.WriteLine("ChatterBox CLI ready. Type 'connect' to attach or 'help' for commands.");

var handler = new CommandHandler(
    state,
    domains,
    profiles,
    ConnectAsync,
    DisconnectAsync,
    SendMethodAsync,
    SendRawAsync,
    Console.WriteLine);

while (true)
{
    var prompt = domains.CurrentDomain.Length > 0 ? $"[ChatterBox::{domains.CurrentDomain}]: " : "[ChatterBox]: ";
    var line = ReadLineWithCompletion(prompt);
    if (line == null) continue;
    var trimmed = line.Trim();
    if (string.IsNullOrWhiteSpace(trimmed)) continue;
    history.Add(trimmed);
    historyIndex = -1;

    var outcome = await handler.TryHandleAsync(trimmed);
    if (outcome.Result == CommandResult.Exit)
    {
        await DisconnectAsync();
        break;
    }
    if (outcome.Result == CommandResult.Handled)
    {
        continue;
    }
    if (outcome.Result == CommandResult.Expanded && !string.IsNullOrWhiteSpace(outcome.Expanded))
    {
        trimmed = outcome.Expanded;
    }

    var space = trimmed.IndexOf(' ');
    if (space < 0)
    {
        var methodOnly = ApplyDomain(trimmed);
        await SendMethodAsync(methodOnly, null, outcome.CacheOutput);
        continue;
    }

    var method = trimmed.Substring(0, space).Trim();
    method = ApplyDomain(method);
    var paramText = trimmed.Substring(space + 1).Trim();
    if (paramText.StartsWith("{"))
    {
        try
        {
            using var doc = JsonDocument.Parse(paramText);
            var map = JsonToDictionary(doc.RootElement);
            await SendMethodAsync(method, map, outcome.CacheOutput);
        }
        catch
        {
            Console.WriteLine("Could not parse params JSON.");
        }
        continue;
    }

    if (paramText.StartsWith("-"))
    {
        var map = ParseDashParams(paramText);
        var cacheOutput = ExtractCacheOutput(map) ?? outcome.CacheOutput;
        await SendMethodAsync(method, map, cacheOutput);
        continue;
    }

    Console.WriteLine("Params must be JSON or -param value pairs.");
}

await DisconnectAsync();

string ApplyDomain(string methodName)
{
    if (methodName.Contains('.')) return methodName;
    if (!string.IsNullOrWhiteSpace(domains.CurrentDomain)) return $"{domains.CurrentDomain}.{methodName}";
    return methodName;
}

async Task SendMethodAsync(string method, Dictionary<string, object?>? parameters = null, string? cacheOutput = null)
{
    if (state.Client is not { IsOpen: true })
    {
        Console.WriteLine("Not connected. Use 'connect' first.");
        return;
    }
    var id = state.Client.NextId();
    var json = BuildRequestJson(id, method, parameters);
    state.Client.TrackPending(id, method);
    if (!string.IsNullOrWhiteSpace(cacheOutput))
    {
        if (state.OutputCache.ContainsKey(cacheOutput) &&
            (profiles.ActiveProfile?.Commands.ContainsKey(cacheOutput) != true))
        {
            Console.WriteLine($"Cache '{cacheOutput}' exists and will be overwritten.");
        }
        state.PendingCacheOutputs[id] = cacheOutput;
    }
    if (state.ShowInput) Console.WriteLine("[IN] " + json);
    await state.Client.SendAsync(json);
    if (method.Equals("Runtime.enable", StringComparison.OrdinalIgnoreCase))
    {
        domains.RuntimeEnabled = true;
    }
}

async Task SendRawAsync(string raw)
{
    if (state.Client is not { IsOpen: true })
    {
        Console.WriteLine("Not connected. Use 'connect' first.");
        return;
    }
    if (state.ShowInput) Console.WriteLine("[IN] " + raw);
    await state.Client.SendAsync(raw);
}

static string BuildRequestJson(int id, string method, Dictionary<string, object?>? parameters)
{
    using var ms = new MemoryStream();
    using var writer = new Utf8JsonWriter(ms);
    writer.WriteStartObject();
    writer.WriteNumber("id", id);
    writer.WriteString("method", method);
    if (parameters != null && parameters.Count > 0)
    {
        writer.WritePropertyName("params");
        writer.WriteStartObject();
        foreach (var kv in parameters)
        {
            writer.WritePropertyName(kv.Key);
            WriteJsonValue(writer, kv.Value);
        }
        writer.WriteEndObject();
    }
    writer.WriteEndObject();
    writer.Flush();
    return Encoding.UTF8.GetString(ms.ToArray());
}

static void WriteJsonValue(Utf8JsonWriter writer, object? value)
{
    switch (value)
    {
        case null:
            writer.WriteNullValue();
            break;
        case bool b:
            writer.WriteBooleanValue(b);
            break;
        case int i:
            writer.WriteNumberValue(i);
            break;
        case long l:
            writer.WriteNumberValue(l);
            break;
        case double d:
            writer.WriteNumberValue(d);
            break;
        case float f:
            writer.WriteNumberValue(f);
            break;
        case JsonElement el:
            el.WriteTo(writer);
            break;
        default:
            writer.WriteStringValue(value.ToString());
            break;
    }
}

static Dictionary<string, object?> JsonToDictionary(JsonElement obj)
{
    var map = new Dictionary<string, object?>();
    foreach (var prop in obj.EnumerateObject())
    {
        map[prop.Name] = prop.Value.ValueKind switch
        {
            JsonValueKind.String => prop.Value.GetString(),
            JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => prop.Value.Clone(),
            JsonValueKind.Array => prop.Value.Clone(),
            _ => prop.Value.ToString()
        };
    }
    return map;
}

static Dictionary<string, object?> ParseDashParams(string text)
{
    var tokens = Tokenize(text);
    var map = new Dictionary<string, object?>();
    for (var i = 0; i < tokens.Count; i++)
    {
        var tok = tokens[i];
        if (!tok.StartsWith("-")) continue;
        var name = tok.TrimStart('-');
        object? value = true;
        if (i + 1 < tokens.Count && !tokens[i + 1].StartsWith("-"))
        {
            value = ConvertParamValue(tokens[i + 1]);
            i++;
        }
        map[name] = value;
    }
    return map;
}

static string? ExtractCacheOutput(Dictionary<string, object?> map)
{
    string? keyToRemove = null;
    foreach (var key in map.Keys)
    {
        if (key.Equals("cacheOutput", StringComparison.OrdinalIgnoreCase))
        {
            keyToRemove = key;
            break;
        }
    }
    if (keyToRemove == null) return null;
    var value = map[keyToRemove];
    map.Remove(keyToRemove);
    return value?.ToString();
}

static object? ConvertParamValue(string text)
{
    var t = text.Trim();
    if ((t.StartsWith("\"") && t.EndsWith("\"")) || (t.StartsWith("'") && t.EndsWith("'")))
    {
        return t.Substring(1, t.Length - 2);
    }
    if (bool.TryParse(t, out var b)) return b;
    if (long.TryParse(t, out var l)) return l;
    if (double.TryParse(t, out var d)) return d;
    if (t.StartsWith("{") || t.StartsWith("["))
    {
        try
        {
            using var doc = JsonDocument.Parse(t);
            return doc.RootElement.Clone();
        }
        catch { }
    }
    return t;
}

static List<string> Tokenize(string text)
{
    var tokens = new List<string>();
    var sb = new StringBuilder();
    var inQuote = false;
    char quote = '\0';

    foreach (var ch in text)
    {
        if (inQuote)
        {
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

async Task ConnectAsync()
{
    var resolved = state.WsUrl;
    if (string.IsNullOrWhiteSpace(resolved))
    {
        try
        {
            resolved = await CdpClient.GetDefaultWsUrlAsync(state.CdpAddress, state.CdpPort);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[ERR] " + ex.Message);
            return;
        }
    }

    var nextClient = new CdpClient();
    nextClient.OnMessage += text =>
    {
        lock (consoleLock)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                domains.HandleRuntimeEvent(root);
                if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id))
                {
                    nextClient.TryConsumePending(id, out var method);
                    if (state.PendingCacheOutputs.TryGetValue(id, out var cacheName))
                    {
                        state.PendingCacheOutputs.Remove(id);
                        if (!string.IsNullOrWhiteSpace(cacheName) &&
                            OutputFormatter.TryExtractValueList(root, out var list))
                        {
                            state.OutputCache[cacheName] = list;
                        }
                    }
                    var outputWidth = Math.Max(80, Console.WindowWidth - 1);
                    var output = OutputFormatter.Format(root, method, state.OutputMode, outputWidth);
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        if (inputState.IsReading)
                        {
                            PrepareForOutput(inputState);
                        }
                        foreach (var line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                        {
                            Console.WriteLine(line);
                        }
                        if (inputState.IsReading)
                        {
                            inputState.LineTop = Console.CursorTop;
                            RedrawLine(inputState);
                        }
                    }
                }
                else if (state.OutputMode == OutputMode.FullJson)
                {
                    if (inputState.IsReading)
                    {
                        PrepareForOutput(inputState);
                    }
                    Console.WriteLine("[OUT] " + JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
                    if (inputState.IsReading)
                    {
                        inputState.LineTop = Console.CursorTop;
                        RedrawLine(inputState);
                    }
                }
            }
            catch (Exception ex)
            {
                if (inputState.IsReading)
                {
                    PrepareForOutput(inputState);
                }
                Console.WriteLine("[ERR] " + ex.Message);
                if (inputState.IsReading)
                {
                    inputState.LineTop = Console.CursorTop;
                    RedrawLine(inputState);
                }
            }
        }
    };

    nextClient.OnError += ex =>
    {
        lock (consoleLock)
        {
            Console.WriteLine("[ERR] " + ex.Message);
        }
    };

    Console.WriteLine($"Connecting to: {resolved}");
    await nextClient.ConnectAsync(resolved);
    state.Client = nextClient;
    Console.WriteLine("Connected. Type 'help' for commands, 'quit' to exit.");

    try
    {
        var protocol = await ProtocolLoader.GetProtocolAsync(state.CdpAddress, state.CdpPort);
        domains.UpdateProtocol(protocol);
    }
    catch
    {
        domains.UpdateProtocol(null);
    }

    if (!domains.RuntimeEnabled)
    {
        await SendMethodAsync("Runtime.enable");
        domains.RuntimeEnabled = true;
    }
    await SendMethodAsync("Log.enable");
    await SendMethodAsync("Page.enable");

    Console.WriteLine();
    Console.WriteLine("Try: Runtime.evaluate {\"expression\":\"2+2\"}");
    Console.WriteLine("Or : Runtime.evaluate -expression \"2+2\"");
    Console.WriteLine();
}

async Task DisconnectAsync()
{
    if (state.Client is { IsOpen: true })
    {
        await state.Client.DisposeAsync();
        state.Client = null;
    }
    domains.Clear();
    state.PendingCacheOutputs.Clear();
    state.OutputCache.Clear();
}

string? ReadLineWithCompletion(string prompt)
{
    inputState.IsReading = true;
    inputState.Prompt = prompt;
    inputState.Buffer.Clear();
    inputState.Cursor = 0;
    inputState.RenderedLines = 1;

    lock (consoleLock)
    {
        Console.Write(prompt);
    }
    var buffer = new StringBuilder();
    var cursor = 0;
    inputState.LineTop = Console.CursorTop;

    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            lock (consoleLock)
            {
                Console.WriteLine();
            }
            inputState.IsReading = false;
            return buffer.ToString();
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (cursor > 0)
            {
                var atEnd = cursor == buffer.Length;
                buffer.Remove(cursor - 1, 1);
                cursor--;
                lock (consoleLock)
                {
                    SyncInputState(buffer, cursor);
                    var wraps = inputState.Prompt.Length + cursor >= Console.BufferWidth - 1;
                    if (atEnd && !wraps)
                    {
                        Console.Write("\b \b");
                    }
                    else
                    {
                        RedrawLine(inputState);
                    }
                }
            }
            continue;
        }

        if (key.Key == ConsoleKey.LeftArrow)
        {
            if (cursor > 0)
            {
                cursor--;
                lock (consoleLock)
                {
                    SetCursorSafe(prompt.Length + cursor, inputState.LineTop);
                    SyncInputState(buffer, cursor);
                }
            }
            continue;
        }

        if (key.Key == ConsoleKey.UpArrow)
        {
            if (history.Count == 0) continue;
            if (historyIndex < 0) historyIndex = history.Count - 1;
            else if (historyIndex > 0) historyIndex--;
            ReplaceWithHistory(history[historyIndex], buffer, ref cursor);
            continue;
        }

        if (key.Key == ConsoleKey.DownArrow)
        {
            if (history.Count == 0) continue;
            if (historyIndex < 0) continue;
            if (historyIndex < history.Count - 1)
            {
                historyIndex++;
                ReplaceWithHistory(history[historyIndex], buffer, ref cursor);
            }
            else
            {
                historyIndex = -1;
                ReplaceWithHistory("", buffer, ref cursor);
            }
            continue;
        }

        if (key.Key == ConsoleKey.RightArrow)
        {
            if (cursor < buffer.Length)
            {
                cursor++;
                lock (consoleLock)
                {
                    SetCursorSafe(prompt.Length + cursor, inputState.LineTop);
                    SyncInputState(buffer, cursor);
                }
            }
            continue;
        }

        if (key.Key == ConsoleKey.Home)
        {
            cursor = 0;
            lock (consoleLock)
            {
                SetCursorSafe(prompt.Length, inputState.LineTop);
                SyncInputState(buffer, cursor);
            }
            continue;
        }

        if (key.Key == ConsoleKey.End)
        {
            cursor = buffer.Length;
            lock (consoleLock)
            {
                SetCursorSafe(prompt.Length + cursor, inputState.LineTop);
                SyncInputState(buffer, cursor);
            }
            continue;
        }

        if (key.Key == ConsoleKey.Tab)
        {
            var completion = GetCompletions(buffer.ToString(), cursor);
            if (completion.Items.Count == 0)
            {
                continue;
            }
            if (completion.Items.Count == 1)
            {
                ReplaceRange(buffer, completion.Start, cursor - completion.Start, completion.Items[0], ref cursor);
                lock (consoleLock)
                {
                    SyncInputState(buffer, cursor);
                    RedrawLine(inputState);
                }
                continue;
            }

            var tokenLen = cursor - completion.Start;
            var bestPrefix = completion.Items
                .OrderBy(i => i.Length)
                .FirstOrDefault(i => completion.Items.All(o =>
                    o.StartsWith(i, StringComparison.OrdinalIgnoreCase)));

            if (!string.IsNullOrWhiteSpace(bestPrefix) && bestPrefix.Length > tokenLen)
            {
                ReplaceRange(buffer, completion.Start, tokenLen, bestPrefix, ref cursor);
                lock (consoleLock)
                {
                    SyncInputState(buffer, cursor);
                    RedrawLine(inputState);
                }
                continue;
            }

            var common = completion.Items[0];
            foreach (var item in completion.Items)
            {
                var max = Math.Min(common.Length, item.Length);
                var i = 0;
                while (i < max && common[i] == item[i]) i++;
                common = common.Substring(0, i);
                if (common.Length == 0) break;
            }

            if (common.Length > (cursor - completion.Start))
            {
                ReplaceRange(buffer, completion.Start, cursor - completion.Start, common, ref cursor);
                lock (consoleLock)
                {
                    SyncInputState(buffer, cursor);
                    RedrawLine(inputState);
                }
                continue;
            }

            lock (consoleLock)
            {
                Console.WriteLine();
                foreach (var item in completion.Items)
                {
                    Console.WriteLine(item);
                }
                Console.Write(prompt);
                Console.Write(buffer.ToString());
                inputState.LineTop = Console.CursorTop;
                SetCursorSafe(prompt.Length + cursor, inputState.LineTop);
                SyncInputState(buffer, cursor);
            }
            continue;
        }

        if (!char.IsControl(key.KeyChar))
        {
            var atEnd = cursor == buffer.Length;
            buffer.Insert(cursor, key.KeyChar);
            cursor++;
            historyIndex = -1;
            lock (consoleLock)
            {
                SyncInputState(buffer, cursor);
                if (atEnd)
                {
                    Console.Write(key.KeyChar);
                }
                else
                {
                    RedrawLine(inputState);
                }
            }
        }
    }
}

void ReplaceWithHistory(string value, StringBuilder buffer, ref int cursor)
{
    buffer.Clear();
    buffer.Append(value);
    cursor = buffer.Length;
    lock (consoleLock)
    {
        SyncInputState(buffer, cursor);
        RedrawLine(inputState);
    }
}

static void ReplaceRange(StringBuilder buffer, int start, int length, string replacement, ref int cursor)
{
    buffer.Remove(start, length);
    buffer.Insert(start, replacement);
    cursor = start + replacement.Length;
}

static void PrepareForOutput(InputState state)
{
    // Move off the input line so output doesn't overwrite it.
    var width = Console.BufferWidth;
    var lines = Math.Max(1, state.RenderedLines);
    for (var i = 0; i < lines; i++)
    {
        SetCursorSafe(0, state.LineTop + i);
        if (width > 0)
        {
            Console.Write(new string(' ', Math.Max(0, width - 1)));
        }
    }
    SetCursorSafe(0, state.LineTop + lines);
    state.LineTop = Console.CursorTop;
    state.RenderedLines = 1;
}

static void RedrawLine(InputState state)
{
    var width = Console.BufferWidth;
    if (width <= 0)
    {
        SetCursorSafe(0, state.LineTop);
        Console.Write(state.Prompt);
        Console.Write(state.Buffer.ToString());
        SetCursorSafe(state.Prompt.Length + state.Cursor, state.LineTop);
        state.RenderedLines = 1;
        return;
    }

    var totalLen = state.Prompt.Length + state.Buffer.Length;
    var neededLines = Math.Max(1, (totalLen + width - 1) / width);
    var clearLines = Math.Max(state.RenderedLines, neededLines);

    for (var i = 0; i < clearLines; i++)
    {
        SetCursorSafe(0, state.LineTop + i);
        Console.Write(new string(' ', Math.Max(0, width - 1)));
    }

    SetCursorSafe(0, state.LineTop);
    Console.Write(state.Prompt);
    Console.Write(state.Buffer.ToString());

    var cursorPos = state.Prompt.Length + state.Cursor;
    var cursorRow = cursorPos / width;
    var cursorCol = cursorPos % width;
    SetCursorSafe(cursorCol, state.LineTop + cursorRow);
    state.RenderedLines = neededLines;
}

static void SetCursorSafe(int left, int top)
{
    try
    {
        var maxLeft = Math.Max(0, Console.BufferWidth - 1);
        var maxTop = Math.Max(0, Console.BufferHeight - 1);
        var safeLeft = Math.Clamp(left, 0, maxLeft);
        var safeTop = Math.Clamp(top, 0, maxTop);
        Console.SetCursorPosition(safeLeft, safeTop);
    }
    catch
    {
        // Ignore cursor errors when buffer size changes unexpectedly.
    }
}

CompletionResult GetCompletions(string line, int cursor)
{
    var left = cursor <= line.Length ? line.Substring(0, cursor) : line;
    var tokenMatch = System.Text.RegularExpressions.Regex.Match(left, @"([^\s]+)$");
    var token = tokenMatch.Success ? tokenMatch.Groups[1].Value : "";
    var tokenStart = tokenMatch.Success ? left.Length - token.Length : left.Length;

    var items = new List<string>();
    var tokens = CliParser.Tokenize(left);
    var leftTrimStart = left.TrimStart();
    if (leftTrimStart.Contains(' '))
    {
        var localItems = LocalCommands(state.Client is { IsOpen: true }).ToList();
        var basePrefix = left.Substring(0, tokenStart);
        var matches = localItems
            .Where(c => c.StartsWith(leftTrimStart, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (matches.Count > 0)
        {
            var nextTokens = new List<string>();
            foreach (var match in matches)
            {
                if (!match.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                var remainder = match.Substring(basePrefix.Length);
                remainder = remainder.TrimStart();
                if (remainder.Length == 0) continue;
                var next = remainder.Split(' ', 2)[0];
                if (next.Length > 0) nextTokens.Add(next);
            }
            if (nextTokens.Count > 0)
            {
                var distinct = nextTokens.Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new CompletionResult(tokenStart, distinct);
            }
        }
    }
    if (profiles.ActiveProfile != null && tokens.Count > 0)
    {
        var cmdName = tokens[0];
        if (profiles.ActiveProfile.Commands.TryGetValue(cmdName, out var profileCmd))
        {
            var placeholders = CliParser.ExtractPlaceholders(profileCmd.Template);
            var placeholderMap = placeholders.ToDictionary(p => p.Name, p => p.CacheName, StringComparer.OrdinalIgnoreCase);
            var leftEndsWithSpace = left.Length > 0 && char.IsWhiteSpace(left[^1]);
            var lastToken = tokens.Count > 0 ? tokens[^1] : "";
            var prevToken = tokens.Count > 1 ? tokens[^2] : "";

            if (tokens.Count == 1)
            {
                var argItems = placeholders.Select(p => "-" + p.Name).ToList();
                if (!argItems.Contains("-cacheOutput", StringComparer.OrdinalIgnoreCase))
                {
                    argItems.Add("-cacheOutput");
                }
                return new CompletionResult(leftEndsWithSpace ? cursor : cursor, argItems);
            }

            if (token.StartsWith("-", StringComparison.Ordinal))
            {
                items.AddRange(placeholders
                    .Select(p => "-" + p.Name)
                    .Where(p => p.StartsWith(token, StringComparison.OrdinalIgnoreCase)));
                if ("-cacheOutput".StartsWith(token, StringComparison.OrdinalIgnoreCase))
                {
                    items.Add("-cacheOutput");
                }
                return new CompletionResult(tokenStart, items);
            }

            if (leftEndsWithSpace && lastToken.StartsWith("-", StringComparison.Ordinal))
            {
                var key = lastToken.TrimStart('-');
                if (placeholderMap.TryGetValue(key, out var cacheName) &&
                    !string.IsNullOrWhiteSpace(cacheName) &&
                    state.OutputCache.TryGetValue(cacheName, out var cached))
                {
                    items.AddRange(cached.Where(c => c.StartsWith(token, StringComparison.OrdinalIgnoreCase)));
                    return new CompletionResult(tokenStart, items);
                }
            }

            if (!leftEndsWithSpace && prevToken.StartsWith("-", StringComparison.Ordinal))
            {
                var key = prevToken.TrimStart('-');
                if (placeholderMap.TryGetValue(key, out var cacheName) &&
                    !string.IsNullOrWhiteSpace(cacheName) &&
                    state.OutputCache.TryGetValue(cacheName, out var cached))
                {
                    items.AddRange(cached.Where(c => c.StartsWith(token, StringComparison.OrdinalIgnoreCase)));
                    return new CompletionResult(tokenStart, items);
                }
            }

            if (placeholders.Count == 1 &&
                placeholders[0].CacheName is { Length: > 0 } soloCache &&
                state.OutputCache.TryGetValue(soloCache, out var soloList))
            {
                items.AddRange(soloList.Where(c => c.StartsWith(token, StringComparison.OrdinalIgnoreCase)));
                return new CompletionResult(tokenStart, items);
            }
        }
    }
    if (token.StartsWith("-", StringComparison.Ordinal))
    {
        var first = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
        {
            return new CompletionResult(tokenStart, items);
        }
        var method = first;
        if (!method.Contains('.') && !string.IsNullOrWhiteSpace(domains.CurrentDomain))
        {
            method = $"{domains.CurrentDomain}.{method}";
        }
        var cmd = domains.Protocol?.Domains
            .FirstOrDefault(d => d.Name.Equals(method.Split('.')[0], StringComparison.OrdinalIgnoreCase))?
            .Commands.FirstOrDefault(c => c.Name.Equals(method.Split('.')[1], StringComparison.OrdinalIgnoreCase));
        if (cmd != null)
        {
            items.AddRange(cmd.Parameters.Select(p => "-" + p.Name)
                .Where(p => p.StartsWith(token, StringComparison.OrdinalIgnoreCase)));
        }
        return new CompletionResult(tokenStart, items);
    }

    if (token.EndsWith("."))
    {
        var domPart = token.TrimEnd('.');
        if (domains.TryResolveDomain(domPart, out var domResolved))
        {
            var domain = domains.Protocol?.Domains.FirstOrDefault(d => d.Name.Equals(domResolved, StringComparison.OrdinalIgnoreCase));
            if (domain != null)
            {
                items.AddRange(domain.Commands.Select(c => $"{domResolved}.{c.Name}"));
            }
        }
        return new CompletionResult(tokenStart, items);
    }

    if (token.Contains('.'))
    {
        var parts = token.Split('.', 2);
        if (domains.TryResolveDomain(parts[0], out var domResolved))
        {
            var domain = domains.Protocol?.Domains.FirstOrDefault(d => d.Name.Equals(domResolved, StringComparison.OrdinalIgnoreCase));
            if (domain != null)
            {
                items.AddRange(domain.Commands
                    .Select(c => $"{domResolved}.{c.Name}")
                    .Where(c => c.StartsWith($"{domResolved}.{parts[1]}", StringComparison.OrdinalIgnoreCase)));
            }
        }
        return new CompletionResult(tokenStart, items);
    }

    if (!string.IsNullOrWhiteSpace(domains.CurrentDomain))
    {
        var domain = domains.Protocol?.Domains.FirstOrDefault(d => d.Name.Equals(domains.CurrentDomain, StringComparison.OrdinalIgnoreCase));
        if (domain != null)
        {
            items.AddRange(domain.Commands.Select(c => c.Name)
                .Where(c => c.StartsWith(token, StringComparison.OrdinalIgnoreCase)));
            return new CompletionResult(tokenStart, items);
        }
    }

    items.AddRange(domains.Domains.Where(d => d.StartsWith(token, StringComparison.OrdinalIgnoreCase)));
    if (profiles.ActiveProfile != null)
    {
        items.AddRange(profiles.ActiveProfile.Commands.Keys.Where(c => c.StartsWith(token, StringComparison.OrdinalIgnoreCase)));
    }
    items.AddRange(LocalCommands(state.Client is { IsOpen: true })
        .Where(c => c.StartsWith(token, StringComparison.OrdinalIgnoreCase)));
    return new CompletionResult(tokenStart, items.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
}

IEnumerable<string> LocalCommands(bool isConnected)
{
    return new[]
    {
        "connect",
        "disconnect",
        "set.address",
        "set.port",
        "help",
        "list domains",
        "list targets",
        "cache list",
        "cache show",
        "profile",
        "profile create",
        "profile load",
        "profile unload",
        "profile list",
        "profile show",
        "profile command add",
        "profile command remove",
        "profile command modify",
        "profile cache save",
        "domain",
        "domain clear",
        "clear",
        "output.json",
        "output.fulljson",
        "output.psobj",
        "input.show",
        "input.hide",
        "raw",
        "quit",
        "exit",
        "searchCDP"
    }.Where(c => isConnected ? c != "searchCDP" : true);
}

void SyncInputState(StringBuilder buffer, int cursor)
{
    inputState.Buffer.Clear();
    inputState.Buffer.Append(buffer);
    inputState.Cursor = cursor;
}
