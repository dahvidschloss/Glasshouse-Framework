using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ChatterBoxCLI;

public sealed class DomainContextManager
{
    private readonly object _contextLock = new();
    private readonly Dictionary<long, ExecutionContextInfo> _contexts = new();

    public string CurrentDomain { get; private set; } = "";
    public ProtocolModel? Protocol { get; private set; }
    public HashSet<string> Domains { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> DomainMap { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool RuntimeEnabled { get; set; }

    public void UpdateProtocol(ProtocolModel? protocol)
    {
        Protocol = protocol;
        Domains.Clear();
        DomainMap.Clear();
        if (protocol == null) return;
        foreach (var d in protocol.Domains.Select(d => d.Name))
        {
            Domains.Add(d);
            DomainMap[d] = d;
        }
    }

    public void Clear()
    {
        CurrentDomain = "";
        Protocol = null;
        Domains.Clear();
        DomainMap.Clear();
        RuntimeEnabled = false;
        lock (_contextLock)
        {
            _contexts.Clear();
        }
    }

    public bool TryResolveDomain(string input, out string resolved)
    {
        if (DomainMap.TryGetValue(input, out var exact))
        {
            resolved = exact;
            return true;
        }
        resolved = "";
        return false;
    }

    public bool TrySetDomain(string input, out string message)
    {
        if (input.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            CurrentDomain = "";
            message = "Domain cleared.";
            return true;
        }
        if (TryResolveDomain(input, out var resolved))
        {
            CurrentDomain = resolved;
            message = $"Domain set to: {CurrentDomain}";
            return true;
        }
        message = $"Unknown domain: {input}";
        return false;
    }

    public void HandleRuntimeEvent(JsonElement root)
    {
        if (!root.TryGetProperty("method", out var methodProp))
        {
            return;
        }
        var methodName = methodProp.GetString() ?? "";
        if (methodName == "Runtime.executionContextCreated")
        {
            if (root.TryGetProperty("params", out var p) &&
                p.TryGetProperty("context", out var ctx) &&
                ctx.TryGetProperty("id", out var ctxIdEl) &&
                ctxIdEl.TryGetInt64(out var idVal))
            {
                var info = ExecutionContextInfo.FromJson(ctx);
                lock (_contextLock)
                {
                    _contexts[idVal] = info;
                }
            }
        }
        else if (methodName == "Runtime.executionContextDestroyed")
        {
            if (root.TryGetProperty("params", out var p) &&
                p.TryGetProperty("executionContextId", out var ctxIdEl) &&
                ctxIdEl.TryGetInt64(out var idVal))
            {
                lock (_contextLock)
                {
                    _contexts.Remove(idVal);
                }
            }
        }
        else if (methodName == "Runtime.executionContextsCleared")
        {
            lock (_contextLock)
            {
                _contexts.Clear();
            }
        }
    }

    public List<ExecutionContextInfo> GetContextsSnapshot()
    {
        lock (_contextLock)
        {
            return _contexts.Values.OrderBy(c => c.Id).ToList();
        }
    }
}
