using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ChatterBoxCLI;

public sealed class ProfileManager
{
    private readonly string _profilesDir;
    public Profile? ActiveProfile { get; private set; }

    public ProfileManager()
    {
        _profilesDir = Path.Combine(AppContext.BaseDirectory, "profiles");
        Directory.CreateDirectory(_profilesDir);
    }

    public IEnumerable<string> ListProfiles()
    {
        if (!Directory.Exists(_profilesDir)) return Array.Empty<string>();
        return Directory.GetFiles(_profilesDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
    }

    public void Create(string name)
    {
        var profile = new Profile
        {
            Name = name,
            Caches = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        };
        profile.Commands = BuildDefaultCommands();
        Save(profile);
        ActiveProfile = profile;
    }

    public bool Load(string name)
    {
        var path = GetProfilePath(name);
        if (!File.Exists(path)) return false;
        var json = File.ReadAllText(path);
        var profile = JsonSerializer.Deserialize<Profile>(json) ?? new Profile { Name = name };
        if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = name;
        profile.Commands ??= new Dictionary<string, ProfileCommand>(StringComparer.OrdinalIgnoreCase);
        profile.Caches ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var normalized = false;
        foreach (var cmd in profile.Commands.Values)
        {
            var cleaned = NormalizeTemplate(cmd.Template);
            if (!string.Equals(cleaned, cmd.Template, StringComparison.Ordinal))
            {
                cmd.Template = cleaned;
                normalized = true;
            }
        }
        ActiveProfile = profile;
        if (normalized) SaveActive();
        return true;
    }

    public void Unload()
    {
        ActiveProfile = null;
    }

    public void SaveActive()
    {
        if (ActiveProfile != null)
        {
            Save(ActiveProfile);
        }
    }

    public bool AddOrUpdateCommand(string name, string template, string description, string cacheOutput)
    {
        if (ActiveProfile == null) return false;
        ActiveProfile.Commands[name] = new ProfileCommand
        {
            Template = NormalizeTemplate(template),
            Description = description ?? "",
            CacheOutput = cacheOutput ?? ""
        };
        SaveActive();
        return true;
    }

    public bool RemoveCommand(string name)
    {
        if (ActiveProfile == null) return false;
        var removed = ActiveProfile.Commands.Remove(name);
        if (removed) SaveActive();
        return removed;
    }

    private void Save(Profile profile)
    {
        var path = GetProfilePath(profile.Name);
        var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private string GetProfilePath(string name)
    {
        var safe = name.Trim();
        if (string.IsNullOrWhiteSpace(safe)) safe = "profile";
        var file = safe.ToLowerInvariant() + ".json";
        return Path.Combine(_profilesDir, file);
    }

    private static string NormalizeTemplate(string template)
    {
        if (string.IsNullOrWhiteSpace(template)) return template;
        var cleaned = template.Trim();
        if ((cleaned.StartsWith("\"") && cleaned.EndsWith("\"")) ||
            (cleaned.StartsWith("'") && cleaned.EndsWith("'")))
        {
            cleaned = cleaned.Substring(1, cleaned.Length - 2);
        }
        cleaned = cleaned
            .Replace("u0022", "\"", StringComparison.OrdinalIgnoreCase)
            .Replace("u003E", ">", StringComparison.OrdinalIgnoreCase)
            .Replace("u003C", "<", StringComparison.OrdinalIgnoreCase)
            .Replace("u0026", "&", StringComparison.OrdinalIgnoreCase)
            .Replace("u003D", "=", StringComparison.OrdinalIgnoreCase)
            .Replace("u0027", "'", StringComparison.OrdinalIgnoreCase)
            .Replace("u005C", "\\", StringComparison.OrdinalIgnoreCase);
        return cleaned;
    }

    private static Dictionary<string, ProfileCommand> BuildDefaultCommands()
    {
        var commands = new Dictionary<string, ProfileCommand>(StringComparer.OrdinalIgnoreCase);
        commands["enum_WindowRoots"] = new ProfileCommand
        {
            Template = NormalizeTemplate("Runtime.evaluate -expression \"JSON.stringify(Object.getOwnPropertyNames(window).filter(n => /electron|ipc|bridge|native|api|host|client|desktop|shell$extra?/i.test(n)).sort())\""),
            Description = "List top-level window roots matching common desktop keywords. Use -extra \"|foo|bar\" to extend the filter.",
            CacheOutput = "windowRoots"
        };
        commands["enum_RootFuncs"] = new ProfileCommand
        {
            Template = NormalizeTemplate("Runtime.evaluate -expression \"JSON.stringify(Object.keys(window.$root:&windowRoots || {}))\""),
            Description = "List top-level window root functions matching the root set",
        };
        
        
        return commands;
    }
}
