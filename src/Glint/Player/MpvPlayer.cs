using System.Runtime.InteropServices;
using System.Text;

namespace Glint.Player;

static partial class Mpv
{
    const string Lib = "libmpv-2.dll";

    [LibraryImport(Lib)] public static partial IntPtr mpv_create();
    [LibraryImport(Lib)] public static partial int mpv_initialize(IntPtr ctx);
    [LibraryImport(Lib)] public static partial void mpv_terminate_destroy(IntPtr ctx);
    [LibraryImport(Lib)] public static partial int mpv_set_option_string(IntPtr ctx, byte[] name, byte[] data);
    [LibraryImport(Lib)] public static partial int mpv_set_property_string(IntPtr ctx, byte[] name, byte[] data);
    [LibraryImport(Lib)] public static partial IntPtr mpv_get_property_string(IntPtr ctx, byte[] name);
    [LibraryImport(Lib)] public static partial int mpv_command(IntPtr ctx, IntPtr[] args);
    [LibraryImport(Lib)] public static partial int mpv_observe_property(IntPtr ctx, ulong id, byte[] name, int format);
    [LibraryImport(Lib)] public static partial IntPtr mpv_wait_event(IntPtr ctx, double timeout);
    [LibraryImport(Lib)] public static partial void mpv_wakeup(IntPtr ctx);
    [LibraryImport(Lib)] public static partial void mpv_free(IntPtr data);

    public static byte[] U8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    public const int EventClientMessage = 16;
    public const int EventShutdown = 1, EventStartFile = 6, EventEndFile = 7, EventFileLoaded = 8,
        EventPlaybackRestart = 21, EventPropertyChange = 22;
    public const int FormatFlag = 3, FormatDouble = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public int EventId;
        public int Error;
        public ulong ReplyUserdata;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventProperty
    {
        public IntPtr Name;
        public int Format;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClientMessageData
    {
        public int NumArgs;
        public IntPtr Args;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EndFileData
    {
        public int Reason;
        public int Error;
    }
}

public enum PlayerState { Idle, Loading, Playing, Buffering, Ended, Error }

// libmpv embedded into a native child window. All public members are safe to call from the UI thread.
public sealed class MpvPlayer : IDisposable
{
    IntPtr _ctx;
    readonly Thread _eventThread;
    volatile bool _disposed;

    // Raised on the mpv event thread.
    public event Action<PlayerState>? StateChanged;

    // Mouse input on the video ("click", "dblclick", "wheel 1"/"wheel -1"). Raised on the mpv event thread.
    public event Action<string>? Input;

    public PlayerState State { get; private set; } = PlayerState.Idle;

    public MpvPlayer(IntPtr hwnd, bool lowLatency)
    {
        _ctx = Mpv.mpv_create();
        if (_ctx == IntPtr.Zero) throw new InvalidOperationException("mpv_create failed");

        Opt("wid", hwnd.ToInt64().ToString());
        Opt("vo", "gpu-next");
        Opt("gpu-api", "d3d11");
        Opt("hwdec", "auto-safe");
        Opt("keepaspect", "yes");
        Opt("background-color", "#000000");
        Opt("osc", "no");
        Opt("osd-level", "0");
        Opt("input-default-bindings", "no");
        Opt("input-vo-keyboard", "no");
        Opt("input-cursor", "yes");
        Opt("cursor-autohide", "1500");
        Opt("force-window", "yes");
        Opt("idle", "yes");
        Opt("ytdl", "no");
        Opt("terminal", "no");
        Opt("audio-client-name", "Glint");
        Opt("network-timeout", "15");
        Opt("cache", "yes");
        Opt("demuxer-max-bytes", "48MiB");
        Opt("demuxer-max-back-bytes", "0");
        Opt("video-sync", "audio");
        Opt("interpolation", "no");
        if (lowLatency)
        {
            // Start two segments from the live edge (Twitch's default is three).
            Opt("demuxer-lavf-o", "live_start_index=-2");
        }

        // Dev hook for tuning: GLINT_MPV_OPTS="name=value;name=value"
        // Use {hwnd} in IPC pipe names when testing multiple players.
        foreach (var kv in (Environment.GetEnvironmentVariable("GLINT_MPV_OPTS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (kv.IndexOf('=') is var eq and > 0) Opt(kv[..eq], kv[(eq + 1)..].Replace("{hwnd}", hwnd.ToInt64().ToString()));

        Check(Mpv.mpv_initialize(_ctx), "mpv_initialize");
        Mpv.mpv_observe_property(_ctx, 1, Mpv.U8("paused-for-cache"), Mpv.FormatFlag);
        Mpv.mpv_observe_property(_ctx, 2, Mpv.U8("core-idle"), Mpv.FormatFlag);

        // Mouse input on the video surface comes back to us as script messages.
        Command("keybind", "MBTN_LEFT_DBL", "script-message glint dblclick");
        Command("keybind", "MBTN_LEFT", "script-message glint click");
        Command("keybind", "MOUSE_MOVE", "script-message glint move");
        Command("keybind", "WHEEL_UP", "script-message glint wheel 1");
        Command("keybind", "WHEEL_DOWN", "script-message glint wheel -1");

        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv events" };
        _eventThread.Start();
    }

    void Opt(string name, string value)
    {
        var r = Mpv.mpv_set_option_string(_ctx, Mpv.U8(name), Mpv.U8(value));
        if (r < 0) Log.Info($"mpv option {name}={value} rejected ({r})");
    }

    static void Check(int r, string what)
    {
        if (r < 0) throw new InvalidOperationException($"{what} failed ({r})");
    }

    static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    public void Load(string url, double? startSeconds = null)
    {
        SetState(PlayerState.Loading);
        Set("start", startSeconds is { } t ? t.ToString("0.0", Inv) : "none");
        Command("loadfile", url, "replace");
        Set("pause", "no");
        Set("speed", "1");
    }

    public void Seek(double seconds) => Command("seek", Math.Max(0, seconds).ToString("0.0", Inv), "absolute");
    public void SetSpeed(double speed) => Set("speed", speed.ToString("0.00", Inv));

    public double? TimePos => double.TryParse(Get("time-pos"), System.Globalization.NumberStyles.Float, Inv, out var v) ? v : null;
    public double? Duration => double.TryParse(Get("duration"), System.Globalization.NumberStyles.Float, Inv, out var v) ? v : null;
    public bool Paused => Get("pause") == "yes";

    public void Stop()
    {
        Command("stop");
        SetState(PlayerState.Idle);
    }

    public void SetVolume(double volume) => Set("volume", volume.ToString("0", Inv));
    public void SetMuted(bool muted) => Set("mute", muted ? "yes" : "no");
    public void SetPaused(bool paused) => Set("pause", paused ? "yes" : "no");

    public string? Get(string name)
    {
        if (_ctx == IntPtr.Zero) return null;
        var p = Mpv.mpv_get_property_string(_ctx, Mpv.U8(name));
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { Mpv.mpv_free(p); }
    }

    void Set(string name, string value)
    {
        if (_ctx == IntPtr.Zero) return;
        Mpv.mpv_set_property_string(_ctx, Mpv.U8(name), Mpv.U8(value));
    }

    void Command(params string[] args)
    {
        if (_ctx == IntPtr.Zero) return;
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                var bytes = Mpv.U8(args[i]);
                ptrs[i] = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, ptrs[i], bytes.Length);
            }
            var r = Mpv.mpv_command(_ctx, ptrs);
            if (r < 0) Log.Info($"mpv command {args[0]} failed ({r})");
        }
        finally
        {
            foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        }
    }

    void SetState(PlayerState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    void EventLoop()
    {
        bool buffering = false;
        while (!_disposed)
        {
            var ctx = _ctx;
            if (ctx == IntPtr.Zero) break;
            var evPtr = Mpv.mpv_wait_event(ctx, 1);
            if (evPtr == IntPtr.Zero) continue;
            var ev = Marshal.PtrToStructure<Mpv.Event>(evPtr);
            switch (ev.EventId)
            {
                case Mpv.EventShutdown:
                    return;
                case Mpv.EventPlaybackRestart:
                    // First frames are actually flowing (FILE_LOADED fires before the demuxer has buffered).
                    SetState(buffering ? PlayerState.Buffering : PlayerState.Playing);
                    break;
                case Mpv.EventClientMessage when ev.Data != IntPtr.Zero:
                    var cm = Marshal.PtrToStructure<Mpv.ClientMessageData>(ev.Data);
                    var args = new string[cm.NumArgs];
                    for (int i = 0; i < cm.NumArgs; i++)
                        args[i] = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(cm.Args, i * IntPtr.Size)) ?? "";
                    if (args.Length > 1 && args[0] == "glint") Input?.Invoke(string.Join(' ', args[1..]));
                    break;
                case Mpv.EventEndFile when ev.Data != IntPtr.Zero:
                    var end = Marshal.PtrToStructure<Mpv.EndFileData>(ev.Data);
                    // reason: 0=eof, 2=stop, 3=quit, 4=error, 5=redirect
                    if (end.Reason == 4) SetState(PlayerState.Error);
                    else if (end.Reason == 0) SetState(PlayerState.Ended);
                    break;
                case Mpv.EventPropertyChange when ev.Data != IntPtr.Zero:
                    var prop = Marshal.PtrToStructure<Mpv.EventProperty>(ev.Data);
                    if (prop.Format == Mpv.FormatFlag && prop.Data != IntPtr.Zero && ev.ReplyUserdata == 1)
                    {
                        buffering = Marshal.ReadInt32(prop.Data) != 0;
                        if (State is PlayerState.Playing or PlayerState.Buffering)
                            SetState(buffering ? PlayerState.Buffering : PlayerState.Playing);
                    }
                    break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var ctx = _ctx;
        _ctx = IntPtr.Zero;
        if (ctx != IntPtr.Zero)
        {
            Mpv.mpv_wakeup(ctx);
            _eventThread.Join(500);
            Mpv.mpv_terminate_destroy(ctx);
        }
    }
}
