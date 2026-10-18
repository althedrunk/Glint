using System.Net.WebSockets;
using System.Text;

namespace Glint;

public sealed class IrcMessage
{
    public Dictionary<string, string> Tags { get; } = new();
    public string? Prefix { get; private set; }
    public string Command { get; private set; } = "";
    public List<string> Params { get; } = new();

    public string? Channel => Params.Count > 0 && Params[0].StartsWith('#') ? Params[0][1..] : null;
    public string? Trailing => Params.Count > 1 ? Params[^1] : null;
    public string Nick => Prefix is { } p && p.IndexOf('!') is var i and > 0 ? p[..i] : Prefix ?? "";
    public string Tag(string key) => Tags.TryGetValue(key, out var v) ? v : "";

    public static IrcMessage? Parse(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var msg = new IrcMessage();
        int pos = 0;

        if (line[0] == '@')
        {
            int end = line.IndexOf(' ');
            if (end < 0) return null;
            foreach (var part in line.AsSpan(1, end - 1).ToString().Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq < 0) msg.Tags[part] = "";
                else msg.Tags[part[..eq]] = Unescape(part[(eq + 1)..]);
            }
            pos = end + 1;
        }
        while (pos < line.Length && line[pos] == ' ') pos++;

        if (pos < line.Length && line[pos] == ':')
        {
            int end = line.IndexOf(' ', pos);
            if (end < 0) return null;
            msg.Prefix = line[(pos + 1)..end];
            pos = end + 1;
        }

        int cmdEnd = line.IndexOf(' ', pos);
        msg.Command = cmdEnd < 0 ? line[pos..] : line[pos..cmdEnd];
        if (cmdEnd < 0) return msg;
        pos = cmdEnd + 1;

        while (pos < line.Length)
        {
            if (line[pos] == ':')
            {
                msg.Params.Add(line[(pos + 1)..]);
                break;
            }
            int next = line.IndexOf(' ', pos);
            if (next < 0) { msg.Params.Add(line[pos..]); break; }
            if (next > pos) msg.Params.Add(line[pos..next]);
            pos = next + 1;
        }
        return msg;
    }

    static string Unescape(string v)
    {
        if (v.IndexOf('\\') < 0) return v;
        var sb = new StringBuilder(v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] == '\\' && i + 1 < v.Length)
            {
                i++;
                sb.Append(v[i] switch { 's' => ' ', ':' => ';', 'r' => '\r', 'n' => '\n', _ => v[i] });
            }
            else if (v[i] != '\\') sb.Append(v[i]);
        }
        return sb.ToString();
    }
}

// One WebSocket connection to Twitch chat, shared by every open channel. Reconnects with backoff and
// rejoins channels automatically. Anonymous (read-only) unless a token is supplied.
public sealed class TwitchIrc : IDisposable
{
    readonly HashSet<string> _channels = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim _sendLock = new(1, 1);
    ClientWebSocket? _ws;
    CancellationTokenSource? _cts;
    string? _login;
    string? _token;

    public event Action<IrcMessage>? MessageReceived;
    public event Action<bool>? ConnectionChanged;

    public bool IsAuthenticated => _token != null;
    public string? Login => _login;

    public void Start(string? login, string? token)
    {
        Stop();
        _login = token != null ? login?.ToLowerInvariant() : null;
        _token = token;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        try { _ws?.Abort(); } catch { }
        _ws = null;
    }

    public void Join(string channel)
    {
        channel = channel.ToLowerInvariant();
        lock (_channels)
            if (!_channels.Add(channel)) return;
        _ = SendRawAsync($"JOIN #{channel}");
    }

    public void Part(string channel)
    {
        channel = channel.ToLowerInvariant();
        lock (_channels)
            if (!_channels.Remove(channel)) return;
        _ = SendRawAsync($"PART #{channel}");
    }

    public Task SendMessageAsync(string channel, string text, string? replyParentId = null)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ');
        var prefix = replyParentId != null ? $"@reply-parent-msg-id={replyParentId} " : "";
        return SendRawAsync($"{prefix}PRIVMSG #{channel.ToLowerInvariant()} :{text}");
    }

    async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            _ws = ws;
            try
            {
                await ws.ConnectAsync(new Uri("wss://irc-ws.chat.twitch.tv:443"), ct);
                attempt = 0;
                await SendRawAsync("CAP REQ :twitch.tv/tags twitch.tv/commands");
                if (_token != null && _login != null)
                {
                    await SendRawAsync($"PASS oauth:{_token}");
                    await SendRawAsync($"NICK {_login}");
                }
                else
                {
                    await SendRawAsync("PASS SCHMOOPIIE");
                    await SendRawAsync($"NICK justinfan{Random.Shared.Next(10000, 99999)}");
                }
                string[] channels;
                lock (_channels) channels = _channels.ToArray();
                foreach (var c in channels) await SendRawAsync($"JOIN #{c}");
                ConnectionChanged?.Invoke(true);
                await ReadLoopAsync(ws, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Error("IRC connection", ex);
            }
            catch { }
            finally
            {
                try { ws.Dispose(); } catch { }
            }
            if (ct.IsCancellationRequested) break;
            ConnectionChanged?.Invoke(false);
            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt++)));
            try { await Task.Delay(delay, ct); } catch { break; }
        }
    }

    async Task ReadLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var pending = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            pending.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)pending.Length);
            pending.SetLength(0);
            foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var msg = IrcMessage.Parse(line);
                if (msg == null) continue;
                switch (msg.Command)
                {
                    case "PING":
                        await SendRawAsync("PONG :" + (msg.Trailing ?? msg.Params.FirstOrDefault() ?? "tmi.twitch.tv"));
                        break;
                    case "RECONNECT":
                        return;
                    case "NOTICE" when msg.Trailing?.Contains("Login authentication failed") == true:
                        Log.Error("IRC auth failed");
                        MessageReceived?.Invoke(msg);
                        break;
                    default:
                        try { MessageReceived?.Invoke(msg); }
                        catch (Exception ex) { Log.Error("IRC handler", ex); }
                        break;
                }
            }
        }
    }

    async Task SendRawAsync(string line)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return;
        await _sendLock.WaitAsync();
        try
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(line + "\r\n"), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex) { Log.Error("IRC send", ex); }
        finally { _sendLock.Release(); }
    }

    public void Dispose() => Stop();
}
