using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ChatterBoxCLI;

public sealed class CdpClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, string> _pending = new();
    private int _idCounter = 0;

    public event Action<string>? OnMessage;
    public event Action<Exception>? OnError;

    public ConcurrentDictionary<int, string> Pending => _pending;

    public static async Task<string> GetDefaultWsUrlAsync(string address, int port)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var listUrl = $"http://{address}:{port}/json/list";
        try
        {
            var listJson = await http.GetStringAsync(listUrl).ConfigureAwait(false);
            using var listDoc = JsonDocument.Parse(listJson);
            foreach (var el in listDoc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("type", out var type) &&
                    type.GetString() == "page" &&
                    el.TryGetProperty("webSocketDebuggerUrl", out var ws) &&
                    ws.GetString() is { Length: > 0 } wsUrl)
                {
                    return wsUrl;
                }
            }
        }
        catch
        {
            // fall through to /json/version
        }

        var versionUrl = $"http://{address}:{port}/json/version";
        var versionJson = await http.GetStringAsync(versionUrl).ConfigureAwait(false);
        using var verDoc = JsonDocument.Parse(versionJson);
        if (verDoc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var wsUrlProp) &&
            wsUrlProp.GetString() is { Length: > 0 } wsUrl2)
        {
            return wsUrl2;
        }

        throw new InvalidOperationException($"CDP response from {versionUrl} did not include webSocketDebuggerUrl.");
    }

    public async Task ConnectAsync(string wsUrl)
    {
        await _ws.ConnectAsync(new Uri(wsUrl), _cts.Token).ConfigureAwait(false);
        _ = Task.Run(ReceiveLoopAsync, _cts.Token);
    }

    public bool IsOpen => _ws.State == WebSocketState.Open;

    public int NextId() => Interlocked.Increment(ref _idCounter);

    public async Task SendAsync(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
        {
            try
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult? result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                            .ConfigureAwait(false);
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var text = Encoding.UTF8.GetString(ms.ToArray());
                OnMessage?.Invoke(text);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException || ex is ObjectDisposedException)
                {
                    return;
                }
                OnError?.Invoke(ex);
            }
        }
    }

    public bool TryConsumePending(int id, out string? method)
    {
        return _pending.TryRemove(id, out method);
    }

    public void TrackPending(int id, string method)
    {
        _pending[id] = method;
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        if (_ws.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        }
        _ws.Dispose();
        _cts.Dispose();
    }
}
