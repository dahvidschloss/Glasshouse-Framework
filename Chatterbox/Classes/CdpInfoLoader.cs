using System;
using System.Net.Http;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChatterBoxCLI;

public static class CdpInfoLoader
{
    public static async Task<(string Browser, string UserAgent)> GetVersionAsync(string address, int port)
    {
        var url = $"http://{address}:{port}/json/version";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var json = await http.GetStringAsync(url).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var browser = root.TryGetProperty("Browser", out var b) ? b.GetString() ?? "" : "";
        var ua = root.TryGetProperty("User-Agent", out var u) ? u.GetString() ?? "" : "";
        return (browser, ua);
    }

    public static async Task<(string Title, string Url)> GetPrimaryTargetAsync(string address, int port)
    {
        var url = $"http://{address}:{port}/json/list";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var json = await http.GetStringAsync(url).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.TryGetProperty("type", out var type) &&
                type.GetString() == "page")
            {
                var title = el.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var urlVal = el.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                return (title, urlVal);
            }
        }
        return ("", "");
    }

    public static async Task<List<TargetInfo>> GetTargetsAsync(string address, int port)
    {
        var url = $"http://{address}:{port}/json/list";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var json = await http.GetStringAsync(url).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var list = new List<TargetInfo>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            list.Add(TargetInfo.FromJson(el));
        }
        return list;
    }

    public static async Task<List<int>> FindListeningCdpPortsAsync()
    {
        var listeners = IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Select(ep => ep.Port)
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        var results = new List<int>();
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
        foreach (var port in listeners)
        {
            var url = $"http://127.0.0.1:{port}/json/version";
            try
            {
                var json = await http.GetStringAsync(url).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var ws) &&
                    ws.GetString() is { Length: > 0 })
                {
                    results.Add(port);
                }
            }
            catch
            {
                // ignore non-CDP ports
            }
        }
        return results;
    }
}
