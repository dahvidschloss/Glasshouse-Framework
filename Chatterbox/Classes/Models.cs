using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace ChatterBoxCLI;

public sealed class CliState
{
    public string WsUrl { get; set; } = "";
    public string CdpAddress { get; set; } = "127.0.0.1";
    public int CdpPort { get; set; } = 9222;
    public bool ShowInput { get; set; }
    public OutputMode OutputMode { get; set; } = OutputMode.Table;
    public int OutputWidth { get; set; } = 120;
    public CdpClient? Client { get; set; }
    public Dictionary<int, string> PendingCacheOutputs { get; } = new();
    public Dictionary<string, List<string>> OutputCache { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class InputState
{
    public string Prompt { get; set; } = "";
    public StringBuilder Buffer { get; } = new();
    public int Cursor { get; set; }
    public int LineTop { get; set; }
    public bool IsReading { get; set; }
    public int RenderedLines { get; set; } = 1;
}

public readonly record struct CompletionResult(int Start, List<string> Items);

public sealed class Profile
{
    public string Name { get; set; } = "";
    public Dictionary<string, ProfileCommand> Commands { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> Caches { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProfileCommand
{
    public string Name { get; set; } = "";
    public string Template { get; set; } = "";
    public string Description { get; set; } = "";
    public string CacheOutput { get; set; } = "";
}

public sealed class ProtocolModel
{
    public List<ProtocolDomain> Domains { get; } = new();
}

public sealed class ProtocolDomain
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public List<ProtocolCommand> Commands { get; } = new();
}

public sealed class ProtocolCommand
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public List<ProtocolParam> Parameters { get; } = new();
    public List<ProtocolParam> Returns { get; } = new();
}

public sealed class ProtocolParam
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "object";
    public bool Optional { get; init; }
    public string Description { get; init; } = "";
}

public sealed class ExecutionContextInfo
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Origin { get; init; } = "";
    public string ContextType { get; init; } = "";
    public string FrameId { get; init; } = "";
    public string UniqueId { get; init; } = "";

    public static ExecutionContextInfo FromJson(JsonElement ctx)
    {
        return new ExecutionContextInfo
        {
            Id = ctx.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var idVal) ? idVal : 0,
            Name = ctx.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
            Origin = ctx.TryGetProperty("origin", out var originEl) ? originEl.GetString() ?? "" : "",
            ContextType = ctx.TryGetProperty("auxData", out var aux) && aux.TryGetProperty("type", out var typeEl)
                ? typeEl.GetString() ?? ""
                : "",
            FrameId = ctx.TryGetProperty("auxData", out var aux2) && aux2.TryGetProperty("frameId", out var frameEl)
                ? frameEl.GetString() ?? ""
                : "",
            UniqueId = ctx.TryGetProperty("uniqueId", out var uniqueEl) ? uniqueEl.GetString() ?? "" : ""
        };
    }
}

public sealed class TargetInfo
{
    public string TargetId { get; init; } = "";
    public string Type { get; init; } = "";
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public bool Attached { get; init; }

    public static TargetInfo FromJson(JsonElement el)
    {
        return new TargetInfo
        {
            TargetId = el.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" :
                (el.TryGetProperty("targetId", out var tidEl) ? tidEl.GetString() ?? "" : ""),
            Type = el.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "" : "",
            Title = el.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "",
            Url = el.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "",
            Attached = el.TryGetProperty("attached", out var attEl) && attEl.ValueKind == JsonValueKind.True
        };
    }
}
