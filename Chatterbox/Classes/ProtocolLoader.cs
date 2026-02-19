using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChatterBoxCLI;

public static class ProtocolLoader
{
    public static async Task<List<string>> GetDomainsAsync(string address, int port)
    {
        var url = $"http://{address}:{port}/json/protocol";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var json = await http.GetStringAsync(url).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var list = new List<string>();
        if (doc.RootElement.TryGetProperty("domains", out var domains))
        {
            foreach (var d in domains.EnumerateArray())
            {
                if (d.TryGetProperty("domain", out var dn) && dn.GetString() is { Length: > 0 } name)
                {
                    list.Add(name);
                }
            }
        }
        return list;
    }

    public static async Task<ProtocolModel> GetProtocolAsync(string address, int port)
    {
        var url = $"http://{address}:{port}/json/protocol";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var json = await http.GetStringAsync(url).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var model = new ProtocolModel();
        if (!doc.RootElement.TryGetProperty("domains", out var domains))
        {
            return model;
        }

        foreach (var d in domains.EnumerateArray())
        {
            var domainName = d.TryGetProperty("domain", out var dn) ? dn.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(domainName)) continue;
            var domain = new ProtocolDomain
            {
                Name = domainName,
                Description = d.TryGetProperty("description", out var dd) ? dd.GetString() ?? "" : ""
            };

            if (d.TryGetProperty("commands", out var cmds) && cmds.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cmds.EnumerateArray())
                {
                    var cmd = new ProtocolCommand
                    {
                        Name = c.TryGetProperty("name", out var cn) ? cn.GetString() ?? "" : "",
                        Description = c.TryGetProperty("description", out var cd) ? cd.GetString() ?? "" : ""
                    };

                    if (c.TryGetProperty("parameters", out var pars) && pars.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in pars.EnumerateArray())
                        {
                            cmd.Parameters.Add(ParseParam(p));
                        }
                    }

                    if (c.TryGetProperty("returns", out var rets) && rets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in rets.EnumerateArray())
                        {
                            cmd.Returns.Add(ParseParam(r));
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(cmd.Name))
                    {
                        domain.Commands.Add(cmd);
                    }
                }
            }

            model.Domains.Add(domain);
        }

        return model;
    }

    private static ProtocolParam ParseParam(JsonElement p)
    {
        return new ProtocolParam
        {
            Name = p.TryGetProperty("name", out var pn) ? pn.GetString() ?? "" : "",
            Type = p.TryGetProperty("type", out var pt) ? pt.GetString() ?? "object" : "object",
            Optional = p.TryGetProperty("optional", out var po) && po.ValueKind == JsonValueKind.True,
            Description = p.TryGetProperty("description", out var pd) ? pd.GetString() ?? "" : ""
        };
    }
}
