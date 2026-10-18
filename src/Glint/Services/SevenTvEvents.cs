using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Glint;

// 7TV EventAPI client: live emote-set updates (emote added / removed / renamed) for every open channel.
public sealed class SevenTvEvents : IDisposable
{
    readonly HashSet<string> _sets = new();
    readonly SemaphoreSlim _sendLock = new(1, 1);
    ClientWebSocket? _ws;
    CancellationTokenSource? _cts;

    // (setId, actor, added, removed, renamed-from-old-name)
    public event Action<string, string?, JsonElement?, JsonElement?, JsonElement?>? EmoteSetChanged;

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Subscribe(string setId)
    {
        lock (_sets) if (!_sets.Add(setId)) return;
        _ = SendSubAsync(35, setId);
    }

    public void Unsubscribe(string setId)
    {
        lock (_sets) if (!_sets.Remove(setId)) return;
        _ = SendSubAsync(36, setId);
    }

    Task SendSubAsync(int op, string setId) =>
        SendAsync(JsonSerializer.Serialize(new { op, d = new { type = "emote_set.update", condition = new { object_id = setId } } }));

    async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var ws = new ClientWebSocket();
            _ws = ws;
            try
            {
                await ws.ConnectAsync(new Uri("wss://events.7tv.io/v3"), ct);
                attempt = 0;
                string[] sets;
                lock (_sets) sets = _sets.ToArray();
                foreach (var s in sets) await SendSubAsync(35, s);
                await ReadLoopAsync(ws, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Error("7TV EventAPI", ex); }
            catch { }
            finally { try { ws.Dispose(); } catch { } }
            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 2 << Math.Min(attempt++, 5))), ct); } catch { break; }
        }
    }

    async Task ReadLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return;
            ms.Write(buffer, 0, r.Count);
            if (!r.EndOfMessage) continue;
            try
            {
                using var doc = JsonDocument.Parse(ms.GetBuffer().AsMemory(0, (int)ms.Length));
                Handle(doc.RootElement);
            }
            catch (Exception ex) { Log.Error("7TV EventAPI message", ex); }
            ms.SetLength(0);
        }
    }

    void Handle(JsonElement root)
    {
        var op = root.Int("op");
        if (op == 4) throw new IOException("7TV requested reconnect"); // RECONNECT
        if (op != 0) return; // only DISPATCH matters

        var d = root.Obj("d");
        if (d.Str("type") != "emote_set.update") return;
        var body = d.Obj("body");
        var setId = body.Str("id");
        if (setId == null) return;
        var actor = body.Obj("actor").Str("display_name");

        foreach (var p in body.Arr("pushed"))
            if (p.Str("key") == "emotes") EmoteSetChanged?.Invoke(setId, actor, p.Obj("value").Clone(), null, null);
        foreach (var p in body.Arr("pulled"))
            if (p.Str("key") == "emotes") EmoteSetChanged?.Invoke(setId, actor, null, p.Obj("old_value").Clone(), null);
        foreach (var p in body.Arr("updated"))
            if (p.Str("key") == "emotes") EmoteSetChanged?.Invoke(setId, actor, p.Obj("value").Clone(), null, p.Obj("old_value").Clone());
    }

    async Task SendAsync(string json)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return;
        await _sendLock.WaitAsync();
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None); }
        catch (Exception ex) { Log.Error("7TV send", ex); }
        finally { _sendLock.Release(); }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _ws?.Abort(); } catch { }
    }
}
