using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ChatterBoxCLI;

public sealed class CommandHandler
{
    private readonly CliState _state;
    private readonly DomainContextManager _domains;
    private readonly ProfileManager _profiles;
    private readonly Func<Task> _connectAsync;
    private readonly Func<Task> _disconnectAsync;
    private readonly Func<string, Dictionary<string, object?>?, string?, Task> _sendMethodAsync;
    private readonly Func<string, Task> _sendRawAsync;
    private readonly Action<string> _writeLine;

    public CommandHandler(
        CliState state,
        DomainContextManager domains,
        ProfileManager profiles,
        Func<Task> connectAsync,
        Func<Task> disconnectAsync,
        Func<string, Dictionary<string, object?>?, string?, Task> sendMethodAsync,
        Func<string, Task> sendRawAsync,
        Action<string> writeLine)
    {
        _state = state;
        _domains = domains;
        _profiles = profiles;
        _connectAsync = connectAsync;
        _disconnectAsync = disconnectAsync;
        _sendMethodAsync = sendMethodAsync;
        _sendRawAsync = sendRawAsync;
        _writeLine = writeLine;
    }

    public async Task<CommandOutcome> TryHandleAsync(string trimmed)
    {
        if (trimmed.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("exit", StringComparison.OrdinalIgnoreCase))
        {
            return CommandOutcome.Exit;
        }

        if (trimmed.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            Console.Clear();
            return CommandOutcome.Handled;
        }

        if (trimmed.StartsWith("help", StringComparison.OrdinalIgnoreCase))
        {
            var arg = trimmed.Length > 4 ? trimmed.Substring(4).Trim() : "";
            if (string.IsNullOrWhiteSpace(arg))
            {
                if (!string.IsNullOrWhiteSpace(_domains.CurrentDomain))
                {
                    HelpPrinter.PrintDomain(_domains.Protocol, _domains.CurrentDomain);
                }
                else
                {
                    HelpPrinter.PrintGeneralWithProfile(_profiles.ActiveProfile, _state.Client is { IsOpen: true });
                }
                return CommandOutcome.Handled;
            }

            if (arg.Equals("profile", StringComparison.OrdinalIgnoreCase))
            {
                HelpPrinter.PrintProfile(_profiles.ActiveProfile);
                return CommandOutcome.Handled;
            }
            if (HelpPrinter.TryPrintLocalCommandHelp(arg))
            {
                return CommandOutcome.Handled;
            }

            if (arg.Contains('.'))
            {
                var parts = arg.Split('.', 2);
                HelpPrinter.PrintCommand(_domains.Protocol, parts[0], parts[1]);
                return CommandOutcome.Handled;
            }

            if (!string.IsNullOrWhiteSpace(_domains.CurrentDomain))
            {
                HelpPrinter.PrintCommand(_domains.Protocol, _domains.CurrentDomain, arg);
            }
            else
            {
                HelpPrinter.PrintDomain(_domains.Protocol, arg);
            }
            return CommandOutcome.Handled;
        }

        if (trimmed.StartsWith("set.address ", StringComparison.OrdinalIgnoreCase))
        {
            _state.CdpAddress = trimmed.Substring(12).Trim().Trim('"');
            _writeLine($"CDP address set to: {_state.CdpAddress}");
            return CommandOutcome.Handled;
        }

        if (trimmed.StartsWith("set.port ", StringComparison.OrdinalIgnoreCase))
        {
            var portText = trimmed.Substring(9).Trim().Trim('"');
            if (int.TryParse(portText, out var newPort))
            {
                _state.CdpPort = newPort;
                _writeLine($"CDP port set to: {_state.CdpPort}");
            }
            else
            {
                _writeLine("Invalid port.");
            }
            return CommandOutcome.Handled;
        }

        if (trimmed.Equals("disconnect", StringComparison.OrdinalIgnoreCase))
        {
            if (_state.Client is { IsOpen: true })
            {
                await _disconnectAsync();
                _writeLine("Disconnected.");
            }
            else
            {
                _writeLine("Not connected.");
            }
            return CommandOutcome.Handled;
        }

        if (trimmed.Equals("connect", StringComparison.OrdinalIgnoreCase))
        {
            if (_state.Client is { IsOpen: true })
            {
                _writeLine("Already connected.");
                return CommandOutcome.Handled;
            }
            await _connectAsync();
            return CommandOutcome.Handled;
        }

        if (trimmed.Equals("searchCDP", StringComparison.OrdinalIgnoreCase))
        {
            if (_state.Client is { IsOpen: true })
            {
                _writeLine("Already connected. Disconnect to use searchCDP.");
                return CommandOutcome.Handled;
            }
            try
            {
                var ports = await CdpInfoLoader.FindListeningCdpPortsAsync();
                if (ports.Count == 0)
                {
                    _writeLine("(no CDP endpoints found)");
                }
                else
                {
                    foreach (var port in ports.OrderBy(p => p))
                    {
                        _writeLine($"CDP detected on port {port}");
                    }
                }
            }
            catch (Exception ex)
            {
                _writeLine("[ERR] " + ex.Message);
            }
            return CommandOutcome.Handled;
        }

        if (trimmed.Equals("list domains", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var d in _domains.Domains) _writeLine(d);
            _writeLine("");
            return CommandOutcome.Handled;
        }

        if (trimmed.Equals("info", StringComparison.OrdinalIgnoreCase))
        {
            if (_state.Client is not { IsOpen: true })
            {
                _writeLine("Not connected. Use 'connect' first.");
                return CommandOutcome.Handled;
            }
            try
            {
                var (browser, ua) = await CdpInfoLoader.GetVersionAsync(_state.CdpAddress, _state.CdpPort);
                var (title, url) = await CdpInfoLoader.GetPrimaryTargetAsync(_state.CdpAddress, _state.CdpPort);
                _writeLine("Browser: " + (string.IsNullOrWhiteSpace(browser) ? "<unknown>" : browser));
                if (!string.IsNullOrWhiteSpace(ua))
                {
                    _writeLine("User-Agent: " + ua);
                }
                if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(url))
                {
                    _writeLine("Target: " + title);
                    _writeLine("URL: " + url);
                }
            }
            catch (Exception ex)
            {
                _writeLine("[ERR] " + ex.Message);
            }
            return CommandOutcome.Handled;
        }

        if (trimmed.Equals("list targets", StringComparison.OrdinalIgnoreCase))
        {
            if (_state.Client is not { IsOpen: true })
            {
                _writeLine("Not connected. Use 'connect' first.");
                return CommandOutcome.Handled;
            }
            try
            {
                var targets = await CdpInfoLoader.GetTargetsAsync(_state.CdpAddress, _state.CdpPort);
                if (targets.Count == 0)
                {
                    _writeLine("(no targets)");
                }
                else
                {
                    PrintTargetsTable(targets);
                }
            }
            catch (Exception ex)
            {
                _writeLine("[ERR] " + ex.Message);
            }
            return CommandOutcome.Handled;
        }

        if (trimmed.Equals("list contexts", StringComparison.OrdinalIgnoreCase))
        {
            if (_state.Client is not { IsOpen: true })
            {
                _writeLine("Not connected. Use 'connect' first.");
                return CommandOutcome.Handled;
            }
            if (!_domains.RuntimeEnabled)
            {
                await _sendMethodAsync("Runtime.enable", null, null);
                _domains.RuntimeEnabled = true;
            }
            var contexts = _domains.GetContextsSnapshot();
            if (contexts.Count == 0)
            {
                _writeLine("(no contexts)");
            }
            else
            {
                PrintContextsTable(contexts);
            }
            return CommandOutcome.Handled;
        }

        if (trimmed.StartsWith("profile ", StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed.Substring(8).Trim();
            var tokens = CliParser.Tokenize(rest);
            if (tokens.Count == 0)
            {
                _writeLine("Usage: profile <create|load|unload|list|show|command>");
                return CommandOutcome.Handled;
            }
            var verb = tokens[0].ToLowerInvariant();
            if (verb == "create" && tokens.Count >= 2)
            {
                _profiles.Create(tokens[1]);
                _writeLine($"Profile created and loaded: {tokens[1]}");
                return CommandOutcome.Handled;
            }
            if (verb == "load" && tokens.Count >= 2)
            {
                if (_profiles.Load(tokens[1]))
                {
                    _writeLine($"Profile loaded: {tokens[1]}");
                    _state.OutputCache.Clear();
                    foreach (var kv in _profiles.ActiveProfile?.Caches ?? new Dictionary<string, List<string>>())
                    {
                        _state.OutputCache[kv.Key] = new List<string>(kv.Value);
                    }
                }
                else
                {
                    _writeLine("Profile not found.");
                }
                return CommandOutcome.Handled;
            }
            if (verb == "unload")
            {
                _profiles.Unload();
                _writeLine("Profile unloaded.");
                _state.OutputCache.Clear();
                return CommandOutcome.Handled;
            }
            if (verb == "list")
            {
                foreach (var p in _profiles.ListProfiles())
                {
                    _writeLine(p);
                }
                return CommandOutcome.Handled;
            }
            if (verb == "show")
            {
                HelpPrinter.PrintProfile(_profiles.ActiveProfile);
                return CommandOutcome.Handled;
            }
            if (verb == "command" && tokens.Count >= 2)
            {
                var sub = tokens[1].ToLowerInvariant();
                var args = tokens.Skip(2).ToList();
                var map = CliParser.ParseDashParams(args);
                if (!map.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
                {
                    _writeLine("Missing -name.");
                    return CommandOutcome.Handled;
                }
                name = TrimQuotes(name);
                if (sub == "add" || sub == "modify")
                {
                    if (!map.TryGetValue("function", out var func))
                    {
                        map.TryGetValue("func", out func);
                    }
                    if (string.IsNullOrWhiteSpace(func))
                    {
                        map.TryGetValue("funcs", out func);
                    }
                    if (string.IsNullOrWhiteSpace(func))
                    {
                        _writeLine("Missing -function.");
                        _writeLine("Tip: wrap -function in single quotes or escape quotes with \\\".");
                        return CommandOutcome.Handled;
                    }
                    map.TryGetValue("desc", out var desc);
                    map.TryGetValue("cacheoutput", out var cacheOutput);
                    func = TrimQuotes(func);
                    desc = TrimQuotes(desc ?? "");
                    cacheOutput = TrimQuotes(cacheOutput ?? "");
                    var ok = _profiles.AddOrUpdateCommand(name, func, desc ?? "", cacheOutput ?? "");
                    if (!ok)
                    {
                        _writeLine("No profile loaded.");
                        return CommandOutcome.Handled;
                    }
                    if (!string.IsNullOrWhiteSpace(cacheOutput) &&
                        _state.OutputCache.ContainsKey(cacheOutput) &&
                        _profiles.ActiveProfile?.Commands.ContainsKey(cacheOutput) != true)
                    {
                        _writeLine($"Cache '{cacheOutput}' exists and will be overwritten.");
                    }
                    _writeLine($"Profile command {(sub == "add" ? "added" : "updated")}: {name}");
                    return CommandOutcome.Handled;
                }
                if (sub == "remove")
                {
                    if (!_profiles.RemoveCommand(name))
                    {
                        _writeLine("No profile loaded or command not found.");
                    }
                    else
                    {
                        _writeLine($"Profile command removed: {name}");
                    }
                    return CommandOutcome.Handled;
                }
            }

            if (verb == "cache" && tokens.Count >= 2)
            {
                var subCache = tokens[1].ToLowerInvariant();
                if (subCache == "save")
                {
                    if (_profiles.ActiveProfile == null)
                    {
                        _writeLine("No profile loaded.");
                        return CommandOutcome.Handled;
                    }
                    _profiles.ActiveProfile.Caches = new Dictionary<string, List<string>>(
                        _state.OutputCache.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value), StringComparer.OrdinalIgnoreCase),
                        StringComparer.OrdinalIgnoreCase);
                    _profiles.SaveActive();
                    _writeLine("Profile caches saved.");
                    return CommandOutcome.Handled;
                }
                _writeLine("Usage: profile cache save");
                return CommandOutcome.Handled;
            }

            _writeLine("Usage: profile create|load|unload|list|show|command|cache");
            return CommandOutcome.Handled;
        }

        if (trimmed.StartsWith("domain ", StringComparison.OrdinalIgnoreCase))
        {
            var arg = trimmed.Substring(7).Trim();
            _domains.TrySetDomain(arg, out var message);
            _writeLine(message);
            return CommandOutcome.Handled;
        }

        if (trimmed.Equals("cache list", StringComparison.OrdinalIgnoreCase))
        {
            if (_state.OutputCache.Count == 0)
            {
                _writeLine("(no caches)");
                return CommandOutcome.Handled;
            }
            foreach (var key in _state.OutputCache.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                _writeLine(key);
            }
            return CommandOutcome.Handled;
        }

        if (trimmed.StartsWith("cache show ", StringComparison.OrdinalIgnoreCase))
        {
            var name = trimmed.Substring(11).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(name))
            {
                _writeLine("Usage: cache show <name>");
                return CommandOutcome.Handled;
            }
            if (!_state.OutputCache.TryGetValue(name, out var items))
            {
                _writeLine("Cache not found.");
                return CommandOutcome.Handled;
            }
            if (items.Count == 0)
            {
                _writeLine("(empty cache)");
                return CommandOutcome.Handled;
            }
            foreach (var item in items)
            {
                _writeLine(item);
            }
            return CommandOutcome.Handled;
        }

        if (trimmed.Equals("output.json", StringComparison.OrdinalIgnoreCase))
        {
            _state.OutputMode = OutputMode.Json;
            _writeLine("Output mode set to JSON.");
            return CommandOutcome.Handled;
        }
        if (trimmed.Equals("output.fulljson", StringComparison.OrdinalIgnoreCase))
        {
            _state.OutputMode = OutputMode.FullJson;
            _writeLine("Output mode set to full JSON.");
            return CommandOutcome.Handled;
        }
        if (trimmed.Equals("output.psobj", StringComparison.OrdinalIgnoreCase))
        {
            _state.OutputMode = OutputMode.Table;
            _writeLine("Output mode set to table.");
            return CommandOutcome.Handled;
        }
        if (trimmed.Equals("input.show", StringComparison.OrdinalIgnoreCase))
        {
            _state.ShowInput = true;
            _writeLine("Input logging enabled.");
            return CommandOutcome.Handled;
        }
        if (trimmed.Equals("input.hide", StringComparison.OrdinalIgnoreCase))
        {
            _state.ShowInput = false;
            _writeLine("Input logging disabled.");
            return CommandOutcome.Handled;
        }

        if (trimmed.StartsWith("raw ", StringComparison.OrdinalIgnoreCase))
        {
            var raw = trimmed.Substring(4).Trim();
            await _sendRawAsync(raw);
            return CommandOutcome.Handled;
        }

        if (_domains.TryResolveDomain(trimmed, out var resolvedDomain))
        {
            _domains.TrySetDomain(resolvedDomain, out var message);
            _writeLine(message);
            return CommandOutcome.Handled;
        }

        var profileExpansion = TryExpandProfileCommand(trimmed);
        if (profileExpansion.Matched)
        {
            if (!string.IsNullOrWhiteSpace(profileExpansion.Error))
            {
                _writeLine(profileExpansion.Error);
                return CommandOutcome.Handled;
            }
            if (!string.IsNullOrWhiteSpace(profileExpansion.Expanded))
            {
                return CommandOutcome.FromExpansion(profileExpansion.Expanded, profileExpansion.CacheOutput);
            }
            return CommandOutcome.Handled;
        }

        return CommandOutcome.NotHandled;
    }

    private void PrintTargetsTable(List<TargetInfo> items)
    {
        var rows = new List<string[]>();
        foreach (var t in items)
        {
            rows.Add(new[]
            {
                t.TargetId,
                t.Type,
                t.Title,
                t.Url,
                t.Attached ? "true" : "false"
            });
        }

        var headers = new[] { "Id", "Type", "Title", "URL", "Attached" };
        var widths = new int[headers.Length];
        for (var i = 0; i < headers.Length; i++)
        {
            widths[i] = headers[i].Length;
        }
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                widths[i] = Math.Max(widths[i], row[i].Length);
            }
        }

        _writeLine(string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))));
        _writeLine(string.Join("  ", headers.Select((h, i) => new string('-', widths[i]))));
        foreach (var row in rows)
        {
            _writeLine(string.Join("  ", row.Select((v, i) => v.PadRight(widths[i]))));
        }
        _writeLine("");
    }

    private void PrintContextsTable(List<ExecutionContextInfo> items)
    {
        var rows = new List<string[]>();
        foreach (var c in items)
        {
            rows.Add(new[]
            {
                c.Id.ToString(),
                c.Name,
                c.Origin,
                c.ContextType,
                c.FrameId,
                c.UniqueId
            });
        }

        var headers = new[] { "Id", "Name", "Origin", "Type", "FrameId", "UniqueId" };
        var widths = new int[headers.Length];
        for (var i = 0; i < headers.Length; i++)
        {
            widths[i] = headers[i].Length;
        }
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                widths[i] = Math.Max(widths[i], row[i].Length);
            }
        }

        _writeLine(string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))));
        _writeLine(string.Join("  ", headers.Select((h, i) => new string('-', widths[i]))));
        foreach (var row in rows)
        {
            _writeLine(string.Join("  ", row.Select((v, i) => v.PadRight(widths[i]))));
        }
        _writeLine("");
    }

    private (bool Matched, string? Expanded, string? Error, string? CacheOutput) TryExpandProfileCommand(string input)
    {
        var profile = _profiles.ActiveProfile;
        if (profile == null) return (false, null, null, null);
        var tokens = CliParser.Tokenize(input);
        if (tokens.Count == 0) return (false, null, null, null);
        var name = TrimQuotes(tokens[0]);
        if (!profile.Commands.TryGetValue(name, out var cmd)) return (false, null, null, null);

        var args = tokens.Skip(1).ToList();
        var dashParams = CliParser.ParseDashParams(args);
        var positional = args.Where(t => !t.StartsWith("-")).ToList();
        string? cacheOverride = null;
        if (dashParams.TryGetValue("cacheoutput", out var cacheValue))
        {
            cacheOverride = TrimQuotes(cacheValue);
            dashParams.Remove("cacheoutput");
        }

        var template = cmd.Template;
        var placeholders = CliParser.ExtractPlaceholders(template);

        if (placeholders.Count == 1 && positional.Count == 1 && dashParams.Count == 0)
        {
            dashParams[placeholders[0].Name] = positional[0];
        }

        foreach (var ph in placeholders)
        {
            if (!dashParams.TryGetValue(ph.Name, out var value))
            {
                if (ph.Optional)
                {
                    var emptyPattern = $@"\${ph.Name}\??(?::&[A-Za-z0-9_\-]+)?";
                    template = System.Text.RegularExpressions.Regex.Replace(
                        template,
                        emptyPattern,
                        "",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    continue;
                }
                return (true, null, $"Missing value for ${ph.Name}. Use -{ph.Name} \"value\".", null);
            }
            var safeValue = TrimQuotes(value);
            var pattern = $@"\${ph.Name}\??(?::&[A-Za-z0-9_\-]+)?";
            template = System.Text.RegularExpressions.Regex.Replace(
                template,
                pattern,
                safeValue,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        var cacheOutput = !string.IsNullOrWhiteSpace(cacheOverride)
            ? cacheOverride
            : (string.IsNullOrWhiteSpace(cmd.CacheOutput) ? null : cmd.CacheOutput);
        return (true, template, null, cacheOutput);
    }

    private static string TrimQuotes(string value)
    {
        if (value.Length >= 2 &&
            ((value.StartsWith("\"") && value.EndsWith("\"")) ||
             (value.StartsWith("'") && value.EndsWith("'"))))
        {
            return value.Substring(1, value.Length - 2);
        }
        return value;
    }
}

public readonly record struct CommandOutcome(CommandResult Result, string? Expanded, string? CacheOutput)
{
    public static CommandOutcome Handled => new(CommandResult.Handled, null, null);
    public static CommandOutcome NotHandled => new(CommandResult.NotHandled, null, null);
    public static CommandOutcome Exit => new(CommandResult.Exit, null, null);
    public static CommandOutcome FromExpansion(string expanded, string? cacheOutput) =>
        new(CommandResult.Expanded, expanded, cacheOutput);
}

public enum CommandResult
{
    NotHandled,
    Handled,
    Exit,
    Expanded
}
