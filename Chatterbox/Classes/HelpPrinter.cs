using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ChatterBoxCLI;

public static class HelpPrinter
{
    public static void PrintGeneral(bool isConnected)
    {
        Console.WriteLine("Commands:");
        PrintSection("Connection",
            ("connect", "Connect to CDP (uses set.address/port or --wsUrl)"),
            ("disconnect", "Disconnect from current CDP session"),
            ("set.address <addr>", "Set default CDP address (host/IP)"),
            ("set.port <port>", "Set default CDP port"),
            ("info", "Show connected browser/target info"));

        PrintSection("Help",
            ("help", "Show this help"),
            ("help <Domain>", "Show commands in a domain"),
            ("help <Domain>.<Cmd>", "Show command details"),
            ("help <Cmd>", "Show command details in current domain"),
            ("help profile <command>", "Show local help for profile subcommands"));

        PrintSection("Profiles",
            ("profile create <name>", "Create and load a profile"),
            ("profile load <name>", "Load a profile"),
            ("profile unload", "Unload current profile"),
            ("profile list", "List available profiles"),
            ("profile show", "Show active profile commands"),
            ("profile command add", "Add a profile command"),
            ("profile command remove", "Remove a profile command"),
            ("profile command modify", "Modify a profile command"),
            ("profile cache save", "Persist caches into the active profile"));

        PrintSection("Cache",
            ("cache list", "List cached values"),
            ("cache show <name>", "Show items in a cache"));

        PrintSection("Domains",
            ("list domains", "List available CDP domains"),
            ("domain <Name>", "Set domain prefix"),
            ("domain clear", "Clear domain prefix"),
            ("list targets", "List CDP targets"),
            ("list contexts", "List runtime execution contexts"));

        PrintSection("Output",
            ("output.json", "JSON output (result only)"),
            ("output.fulljson", "JSON output (full response)"),
            ("output.psobj", "Table output"),
            ("input.show|hide", "Show/hide outbound JSON"));

        PrintSection("Send",
            ("raw <json>", "Send raw JSON"),
            ("<method> <json>", "Send with params object"),
            ("<method> -p v", "Send with -param value pairs"));

        PrintSection("Misc",
            ("clear", "Clear the terminal"),
            ("quit / exit", "Close"));

        if (!isConnected)
        {
            PrintSection("Discovery",
                ("searchCDP", "Scan listening ports for CDP endpoints"));
        }
    }

    public static void PrintGeneralWithProfile(Profile? profile, bool isConnected)
    {
        PrintGeneral(isConnected);
        if (profile == null) return;
        Console.WriteLine();
        PrintColoredLine($"Profile Commands ({profile.Name}):", ConsoleColor.Cyan);
        if (profile.Commands.Count == 0)
        {
            Console.WriteLine("  (no commands)");
            return;
        }
        foreach (var kv in profile.Commands.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var cmd = kv.Value;
            PrintColoredLine($"  {kv.Key}", ConsoleColor.Cyan);
            if (!string.IsNullOrWhiteSpace(cmd.Description))
            {
                PrintWrappedBlock(cmd.Description, "    ");
            }
            Console.WriteLine();
        }
    }

    public static bool TryPrintLocalCommandHelp(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var key = NormalizeKey(command);
        if (!_localHelp.TryGetValue(key, out var entry)) return false;
        PrintLocalHelp(entry);
        return true;
    }

    public static void PrintDomain(ProtocolModel? protocol, string domainName)
    {
        if (protocol == null)
        {
            Console.WriteLine("Help is unavailable until you connect.");
            return;
        }

        var domain = protocol.Domains.FirstOrDefault(d =>
            d.Name.Equals(domainName, StringComparison.OrdinalIgnoreCase));
        if (domain == null)
        {
            Console.WriteLine($"Unknown domain: {domainName}");
            return;
        }

        PrintColoredLine($"Commands for {domain.Name}:", ConsoleColor.Cyan);
        if (!string.IsNullOrWhiteSpace(domain.Description))
        {
            
            PrintWrappedBlock(domain.Description, "  ");
            Console.WriteLine();
        }

        if (domain.Commands.Count == 0)
        {
            Console.WriteLine("  (no commands)");
            return;
        }

        var sorted = domain.Commands.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var cmd in sorted)
        {
            var desc = string.IsNullOrWhiteSpace(cmd.Description) ? "No Description" : cmd.Description;
            PrintColoredLine($"  {cmd.Name}", ConsoleColor.Cyan);
            PrintWrappedBlock(desc, "    ");
            Console.WriteLine();
        }
    }

    public static void PrintProfile(Profile? profile)
    {
        if (profile == null)
        {
            Console.WriteLine("No profile loaded.");
            return;
        }

        PrintColoredLine($"Profile: {profile.Name}", ConsoleColor.Cyan);
        if (profile.Commands.Count == 0)
        {
            Console.WriteLine("  (no commands)");
            return;
        }

        foreach (var kv in profile.Commands.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var cmd = kv.Value;
            PrintColoredLine($"  {kv.Key}", ConsoleColor.Cyan);
            if (!string.IsNullOrWhiteSpace(cmd.Description))
            {
                PrintWrappedBlock(cmd.Description, "    ");
            }
            Console.WriteLine();
        }
    }

    public static void PrintCommand(ProtocolModel? protocol, string domainName, string commandName)
    {
        if (protocol == null)
        {
            Console.WriteLine("Help is unavailable until you connect.");
            return;
        }

        var domain = protocol.Domains.FirstOrDefault(d =>
            d.Name.Equals(domainName, StringComparison.OrdinalIgnoreCase));
        if (domain == null)
        {
            Console.WriteLine($"Unknown domain: {domainName}");
            return;
        }

        var cmd = domain.Commands.FirstOrDefault(c =>
            c.Name.Equals(commandName, StringComparison.OrdinalIgnoreCase));
        if (cmd == null)
        {
            Console.WriteLine($"Unknown command: {domain.Name}.{commandName}");
            return;
        }

        PrintColoredLine($"{domain.Name}.{cmd.Name}", ConsoleColor.Cyan);
        if (!string.IsNullOrWhiteSpace(cmd.Description))
        {
            Console.WriteLine();
            PrintWrappedBlock(cmd.Description, "  ");
            Console.WriteLine();
        }

        PrintColoredLine("Parameters:", ConsoleColor.Cyan);
        if (cmd.Parameters.Count == 0)
        {
            Console.WriteLine("  (none)");
        }
        else
        {
            PrintParamList(cmd.Parameters, includeRequired: true);
        }

        Console.WriteLine();
        PrintColoredLine("Returns:", ConsoleColor.Cyan);
        if (cmd.Returns.Count == 0)
        {
            Console.WriteLine("  (none)");
        }
        else
        {
            PrintParamList(cmd.Returns, includeRequired: false);
        }
    }

    private static void PrintParamList(List<ProtocolParam> items, bool includeRequired)
    {
        foreach (var p in items)
        {
            var required = includeRequired ? (p.Optional ? "optional" : "required") : "";
            var header = includeRequired
                ? $"  {p.Name} ({required}, {p.Type})"
                : $"  {p.Name} ({p.Type})";
            PrintColoredLine(header, ConsoleColor.Cyan);
            if (!string.IsNullOrWhiteSpace(p.Description))
            {
                PrintWrappedBlock(p.Description, "    ");
            }
            Console.WriteLine();
        }
    }

    private static void PrintWrappedBlock(string text, string indent)
    {
        foreach (var line in Wrap(text, GetWrapWidth(indent), indent))
        {
            Console.WriteLine(line);
        }
    }

    private static void PrintColoredLine(string text, ConsoleColor color)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }

    private static void PrintCommandLine(string command, string description)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Cyan;
        var padWidth = 30;
        if (command.Length < padWidth)
        {
            Console.Write(command.PadRight(padWidth));
        }
        else
        {
            Console.Write(command);
            Console.Write(' ');
        }
        Console.ForegroundColor = previous;
        Console.WriteLine(description);
    }

    private static void PrintSection(string title, params (string Command, string Description)[] items)
    {
        Console.WriteLine();
        PrintColoredLine(title + ":", ConsoleColor.Cyan);
        foreach (var (cmd, desc) in items)
        {
            PrintCommandLine("  " + cmd, desc);
        }
    }

    private static void PrintLocalHelp(LocalHelpEntry entry)
    {
        PrintColoredLine("NAME", ConsoleColor.Cyan);
        Console.WriteLine("    " + entry.Name);
        Console.WriteLine();

        PrintColoredLine("SYNTAX", ConsoleColor.Cyan);
        foreach (var syntax in entry.Syntax)
        {
            Console.WriteLine("    " + syntax);
        }
        Console.WriteLine();

        if (entry.Aliases.Count > 0)
        {
            PrintColoredLine("ALIASES", ConsoleColor.Cyan);
            Console.WriteLine("    " + string.Join(", ", entry.Aliases));
            Console.WriteLine();
        }

        if (!string.IsNullOrWhiteSpace(entry.Description))
        {
            PrintColoredLine("DESCRIPTION", ConsoleColor.Cyan);
            PrintWrappedBlock(entry.Description, "    ");
            Console.WriteLine();
        }

        if (entry.Remarks.Count > 0)
        {
            PrintColoredLine("REMARKS", ConsoleColor.Cyan);
            foreach (var remark in entry.Remarks)
            {
                PrintWrappedBlock(remark, "    ");
            }
            Console.WriteLine();
        }
    }

    private static string NormalizeKey(string value)
    {
        return Regex.Replace(value.Trim(), @"\s+", " ").ToLowerInvariant();
    }

    private sealed class LocalHelpEntry
    {
        public string Name { get; init; } = "";
        public List<string> Syntax { get; init; } = new();
        public List<string> Aliases { get; init; } = new();
        public string Description { get; init; } = "";
        public List<string> Remarks { get; init; } = new();
    }

    private static readonly Dictionary<string, LocalHelpEntry> _localHelp =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["profile create"] = new LocalHelpEntry
            {
                Name = "profile create",
                Syntax = { "profile create <name>" },
                Description = "Create and load a new profile with the given name.",
                Remarks = { "Profiles are stored under the profiles directory beside the executable." }
            },
            ["profile load"] = new LocalHelpEntry
            {
                Name = "profile load",
                Syntax = { "profile load <name>" },
                Description = "Load an existing profile from disk.",
                Remarks = { "Also loads any saved caches from the profile." }
            },
            ["profile unload"] = new LocalHelpEntry
            {
                Name = "profile unload",
                Syntax = { "profile unload" },
                Description = "Unload the active profile and clear cached values."
            },
            ["profile list"] = new LocalHelpEntry
            {
                Name = "profile list",
                Syntax = { "profile list" },
                Description = "List available profiles on disk."
            },
            ["profile show"] = new LocalHelpEntry
            {
                Name = "profile show",
                Syntax = { "profile show" },
                Description = "Show commands defined in the active profile."
            },
            ["profile command add"] = new LocalHelpEntry
            {
                Name = "profile command add",
                Syntax =
                {
                    "profile command add -name <name> -func <template> [-desc <text>] [-cacheOutput <name>]"
                },
                Description = "Add a profile command from a template. Use $placeholders in the template and pass values with -placeholder.",
                Remarks =
                {
                    "Use single quotes around -func if it contains quotes.",
                    "Use :&cacheName in placeholders to enable tab completion from caches."
                }
            },
            ["profile command modify"] = new LocalHelpEntry
            {
                Name = "profile command modify",
                Syntax =
                {
                    "profile command modify -name <name> -func <template> [-desc <text>] [-cacheOutput <name>]"
                },
                Description = "Modify an existing profile command."
            },
            ["profile command remove"] = new LocalHelpEntry
            {
                Name = "profile command remove",
                Syntax = { "profile command remove -name <name>" },
                Description = "Remove a profile command."
            },
            ["profile cache save"] = new LocalHelpEntry
            {
                Name = "profile cache save",
                Syntax = { "profile cache save" },
                Description = "Persist in-memory caches into the active profile."
            },
            ["cache list"] = new LocalHelpEntry
            {
                Name = "cache list",
                Syntax = { "cache list" },
                Description = "List cached value sets in memory."
            },
            ["cache show"] = new LocalHelpEntry
            {
                Name = "cache show",
                Syntax = { "cache show <name>" },
                Description = "Show the items stored in a cache."
            },
            ["connect"] = new LocalHelpEntry
            {
                Name = "connect",
                Syntax = { "connect" },
                Description = "Connect to CDP using set.address / set.port or --wsUrl."
            },
            ["disconnect"] = new LocalHelpEntry
            {
                Name = "disconnect",
                Syntax = { "disconnect" },
                Description = "Disconnect from the current CDP session."
            },
            ["domain clear"] = new LocalHelpEntry
            {
                Name = "domain clear",
                Syntax = { "domain clear" },
                Description = "Clear the current domain prefix."
            },
            ["domain"] = new LocalHelpEntry
            {
                Name = "domain",
                Syntax = { "domain <Name>" },
                Description = "Set the current domain prefix."
            }
        };

    private static int GetWrapWidth(string indent)
    {
        var width = 96;
        try
        {
            width = Console.WindowWidth > 0 ? Console.WindowWidth - 1 : 96;
        }
        catch
        {
            width = 96;
        }
        return Math.Max(40, width - indent.Length);
    }

    private static IEnumerable<string> Wrap(string text, int width, string indent)
    {
        if (string.IsNullOrWhiteSpace(text)) return new[] { indent };
        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var line = indent;
        foreach (var w in words)
        {
            if ((line.Length - indent.Length) + w.Length + 1 > width && line.Length > indent.Length)
            {
                lines.Add(line.TrimEnd());
                line = indent + w + " ";
            }
            else
            {
                line += w + " ";
            }
        }
        if (line.Length > indent.Length)
        {
            lines.Add(line.TrimEnd());
        }
        return lines;
    }

    private static string Pad(string value, int width)
    {
        if (value.Length >= width) return value.Substring(0, width);
        return value.PadRight(width);
    }
}
