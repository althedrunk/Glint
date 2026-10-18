using System.Diagnostics;
using System.Numerics;
using System.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;

namespace Glint.Chat;

// Reserves space in a text layout for an emote/badge; we draw the image ourselves.
sealed partial class InlineBox : ICanvasTextInlineObject
{
    public InlineBox(float width, float height, float baseline)
    {
        Size = new Size(width, height);
        Baseline = baseline;
    }

    public Size Size { get; }
    public float Baseline { get; }
    public bool SupportsSideways => false;
    public Rect DrawBounds => new(0, 0, Size.Width, Size.Height);
    public CanvasLineBreakCondition BreakBefore => CanvasLineBreakCondition.Neutral;
    public CanvasLineBreakCondition BreakAfter => CanvasLineBreakCondition.Neutral;
    public void Draw(ICanvasTextRenderer textRenderer, Vector2 point, bool isSideways, bool isRightToLeft, object brush) { }
}

sealed class InlineItem
{
    public int Index;
    public float Width, Height, Baseline;
    public required AnimatedImage Image;
    public EmoteInfo? Emote;
    public List<EmoteInfo>? Overlays;
    public string? Title;
    public Rect Rect; // relative to the text origin
}

sealed class MessageLayout
{
    public float Width;
    public int Version;
    public float Height;
    public float TextY;
    public CanvasTextLayout? Text;
    public CanvasTextLayout? Reply;
    public InlineItem[] Inlines = Array.Empty<InlineItem>();
    public List<(int Start, int Length, string Url)> Links = new();
}

// GPU-drawn chat. Messages are laid out once (DirectWrite) and cached; only the visible window is drawn,
// bottom-anchored so trimming old messages never moves the view.
public sealed partial class ChatView : UserControl, IDisposable
{
    const float PadX = 10, PadY = 3, BadgeSize = 18, EmoteFallback = 28;

    static readonly Color TextColor = Color.FromArgb(255, 239, 239, 241);
    static readonly Color SubtleColor = Color.FromArgb(255, 150, 150, 160);
    static readonly Color LinkColor = Color.FromArgb(255, 160, 150, 255);
    static readonly Color AccentColor = Color.FromArgb(255, 145, 71, 255);
    static int _layoutVersion;

    readonly CanvasControl _canvas;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly DispatcherQueue _dispatcher;
    CanvasTextFormat _format = null!, _smallFormat = null!, _tipTitleFormat = null!, _pillFormat = null!;

    ChatChannel? _channel;
    float _scroll, _target;   // pixels scrolled up from the bottom
    int _unseen;
    bool _hooked, _lastDrawAnimated, _needsFrame;
    long _lastTick, _lastDraw;
    int _wakePending;
    volatile bool _listening, _disposed;

    Point? _pointer;
    Rect _pillRect;
    readonly List<(Rect Rect, InlineItem Item)> _emoteHits = new();
    readonly List<(float Top, float Height, float X, ChatMessage Msg, MessageLayout Layout)> _msgHits = new();
    bool _handCursor;

    public ChatView()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _canvas = new CanvasControl { ClearColor = Colors.Transparent };
        _canvas.Draw += OnDraw;
        Content = _canvas;
        IsTabStop = false;
        RebuildFormats();

        PointerWheelChanged += OnWheel;
        PointerMoved += OnPointerMoved;
        PointerExited += (_, _) => { _pointer = null; _canvas.Invalidate(); };
        PointerPressed += OnPointerPressed;
        _canvas.SizeChanged += (_, _) => { _needsFrame = true; RequestFrame(); _canvas.Invalidate(); };

        Loaded += (_, _) =>
        {
            if (_disposed || _listening) return;
            _listening = true;
            ImageCache.ImageLoaded += OnImageLoaded;
            ImageCache.SizeDiscovered += OnImageSizeDiscovered;
            if (_channel != null) _channel.Updated += OnChannelUpdated;
            _needsFrame = true;
            RequestFrame();
        };
        Unloaded += (_, _) => Detach();
    }

    void OnImageLoaded() => Wake(false);

    void OnImageSizeDiscovered()
    {
        Interlocked.Increment(ref _layoutVersion);
        Wake(false);
    }

    void Detach()
    {
        _listening = false;
        ImageCache.ImageLoaded -= OnImageLoaded;
        ImageCache.SizeDiscovered -= OnImageSizeDiscovered;
        if (_channel != null) _channel.Updated -= OnChannelUpdated;
        if (_hooked) CompositionTarget.Rendering -= OnRendering;
        _hooked = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
        _channel = null;
        _canvas.Draw -= OnDraw;
        Content = null;
        _canvas.RemoveFromVisualTree();
        _format.Dispose(); _smallFormat.Dispose(); _tipTitleFormat.Dispose(); _pillFormat.Dispose();
    }

    public void RebuildFormats()
    {
        if (_disposed) return;
        _format?.Dispose(); _smallFormat?.Dispose(); _tipTitleFormat?.Dispose(); _pillFormat?.Dispose();
        var size = (float)Settings.Current.ChatFontSize;
        _format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Text",
            FontSize = size,
            WordWrapping = CanvasWordWrapping.Wrap,
            Options = CanvasDrawTextOptions.EnableColorFont,
        };
        _smallFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Text",
            FontSize = size - 2,
            WordWrapping = CanvasWordWrapping.NoWrap,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            TrimmingSign = CanvasTrimmingSign.Ellipsis,
            Options = CanvasDrawTextOptions.EnableColorFont,
        };
        _tipTitleFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Display",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        _pillFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Text",
            FontSize = 12.5f,
            FontWeight = FontWeights.SemiBold,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        Interlocked.Increment(ref _layoutVersion);
        _canvas.Invalidate();
    }

    public ChatChannel? Channel => _channel;

    public void SetChannel(ChatChannel? channel)
    {
        _lastDrawAnimated = false;
        if (_channel != null) _channel.Updated -= OnChannelUpdated;
        _channel = channel;
        _scroll = _target = 0;
        _unseen = 0;
        _pointer = null;
        if (channel != null)
        {
            if (_listening) channel.Updated += OnChannelUpdated;
            channel.Drain();
        }
        RequestFrame();
        _canvas.Invalidate();
    }

    public void ScrollToBottom()
    {
        _target = 0;
        RequestFrame();
    }

    void OnChannelUpdated(ChatChannel _) => Wake(false);

    // Thread-safe nudge: coalesces many background events into one UI-thread frame request.
    void Wake(bool invalidateLayout)
    {
        if (_disposed || !_listening) return;
        if (Interlocked.Exchange(ref _wakePending, 1) == 1) return;
        _dispatcher.TryEnqueue(() =>
        {
            _wakePending = 0;
            if (_disposed || !_listening) return;
            _needsFrame = true;
            RequestFrame();
        });
    }

    void RequestFrame()
    {
        if (_disposed || !_listening || _hooked) return;
        _hooked = true;
        _lastTick = _clock.ElapsedMilliseconds;
        CompositionTarget.Rendering += OnRendering;
    }

    void OnRendering(object? sender, object e)
    {
        long now = _clock.ElapsedMilliseconds;
        float dt = Math.Clamp((now - _lastTick) / 1000f, 0.001f, 0.1f);
        _lastTick = now;
        bool dirty = _needsFrame, busy = false;
        _needsFrame = false;

        if (_channel is { HasPending: true } ch)
        {
            var added = ch.Drain();
            if (added.Count > 0)
            {
                if (_target > 0.5f || _scroll > 0.5f)
                {
                    // Keep the reader's position while scrolled up.
                    float width = ContentWidth;
                    float h = 0;
                    foreach (var m in added) h += GetLayout(m, width).Height;
                    _scroll += h;
                    _target += h;
                    _unseen += added.Count;
                }
                dirty = true;
            }
        }

        if (MathF.Abs(_target - _scroll) > 0.5f)
        {
            _scroll += (_target - _scroll) * (1 - MathF.Exp(-dt * 22));
            dirty = busy = true;
        }
        else if (_scroll != _target)
        {
            _scroll = _target;
            dirty = true;
        }
        if (_target <= 0 && _scroll < 0.5f) _unseen = 0;

        if (_lastDrawAnimated && now - _lastDraw >= 15) dirty = true;
        if (dirty) _canvas.Invalidate();

        if (!busy && !_lastDrawAnimated && !dirty && _channel?.HasPending != true)
        {
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
        }
    }

    float ContentWidth => Math.Max(50, (float)_canvas.ActualWidth - PadX * 2);


    MessageLayout GetLayout(ChatMessage m, float width)
    {
        if (m.Layout is MessageLayout l && l.Width == width && l.Version == _layoutVersion) return l;
        var layout = BuildLayout(m, width);
        m.Layout = layout;
        return layout;
    }

    MessageLayout BuildLayout(ChatMessage m, float width)
    {
        var device = CanvasDevice.GetSharedDevice();
        var sb = new StringBuilder();
        var inlines = new List<InlineItem>();
        var colors = new List<(int, int, Color)>();
        var bold = new List<(int, int)>();
        var italic = new List<(int, int)>();
        var underline = new List<(int, int)>();
        var result = new MessageLayout { Width = width, Version = _layoutVersion };
        float fontSize = _format.FontSize;

        void Span(string text, Color? color = null, bool b = false, bool i = false)
        {
            int start = sb.Length;
            sb.Append(text);
            if (color is { } c) colors.Add((start, text.Length, c));
            if (b) bold.Add((start, text.Length));
            if (i) italic.Add((start, text.Length));
        }

        void Inline(AnimatedImage image, float w, float h, float margin, EmoteInfo? emote, List<EmoteInfo>? overlays, string? title)
        {
            // Centre the image on the text's x-height.
            float baseline = Math.Min(h, h / 2 + fontSize * 0.33f);
            inlines.Add(new InlineItem
            {
                Index = sb.Length, Width = w, Height = h, Baseline = baseline,
                Image = image, Emote = emote, Overlays = overlays, Title = title,
            });
            sb.Append('￼');
            _ = margin;
        }

        if (Settings.Current.ShowTimestamps && m.Kind != MessageKind.System)
        {
            var stamp = m.ReplayOffset is { } off
                ? (off.TotalHours >= 1 ? off.ToString(@"h\:mm\:ss") : off.ToString(@"m\:ss"))
                : m.Time.ToString("HH:mm");
            Span(stamp + " ", SubtleColor);
        }

        if (m.Kind is MessageKind.System or MessageKind.UserNotice && m.SystemText is { } sys)
        {
            Span(sys, m.Kind == MessageKind.UserNotice ? Color.FromArgb(255, 210, 190, 255) : SubtleColor,
                b: m.Kind == MessageKind.UserNotice, i: m.Kind == MessageKind.System);
            if (m.Fragments.Count > 0) sb.Append(m.Kind == MessageKind.UserNotice ? "\n" : " ");
        }

        if (m.Kind != MessageKind.System || m.Fragments.Count > 0)
        {
            if (m.Kind != MessageKind.System)
            {
                foreach (var badge in m.Badges)
                {
                    Inline(badge.Image, BadgeSize, BadgeSize, 0, null, null, badge.Title);
                    sb.Append(' ');
                }
                var name = m.DisplayName.Equals(m.Login, StringComparison.OrdinalIgnoreCase) || m.Login.Length == 0
                    ? m.DisplayName
                    : $"{m.DisplayName} ({m.Login})";
                Span(name, m.NameColor, b: true);
                sb.Append(m.Kind == MessageKind.Action ? " " : ": ");
            }

            Color? bodyColor = m.Kind == MessageKind.Action ? m.NameColor : null;
            bool first = true;
            foreach (var f in m.Fragments)
            {
                if (!first) sb.Append(' ');
                first = false;
                switch (f)
                {
                    case TextFragment { Link: { } url } t:
                        result.Links.Add((sb.Length, t.Text.Length, url));
                        underline.Add((sb.Length, t.Text.Length));
                        Span(t.Text, LinkColor);
                        break;
                    case TextFragment { IsMention: true } t:
                        Span(t.Text, bodyColor, b: true);
                        break;
                    case TextFragment t:
                        Span(t.Text, bodyColor, i: m.Kind == MessageKind.Action);
                        break;
                    case EmoteFragment e:
                        float h = e.Emote.Height > 0 ? e.Emote.Height : EmoteFallback;
                        float w = e.Emote.Width > 0 ? e.Emote.Width : EmoteFallback;
                        Inline(e.Emote.Image, w, h, 0, e.Emote, e.Overlays.Count > 0 ? e.Overlays : null, null);
                        break;
                }
            }
        }

        var text = sb.ToString();
        var layout = new CanvasTextLayout(device, text, _format, width, 100000) { Options = CanvasDrawTextOptions.EnableColorFont };
        foreach (var (s, n, c) in colors) layout.SetColor(s, n, c);
        foreach (var (s, n) in bold) layout.SetFontWeight(s, n, FontWeights.SemiBold);
        foreach (var (s, n) in italic) layout.SetFontStyle(s, n, FontStyle.Italic);
        foreach (var (s, n) in underline) layout.SetUnderline(s, n, true);
        foreach (var it in inlines)
            layout.SetInlineObject(it.Index, 1, new InlineBox(it.Width, it.Height, it.Baseline));

        // Resolve where DirectWrite put each inline box.
        var lines = layout.LineMetrics;
        foreach (var it in inlines)
        {
            var regions = layout.GetCharacterRegions(it.Index, 1);
            if (regions.Length == 0) continue;
            var b = regions[0].LayoutBounds;
            double mid = b.Y + b.Height / 2, lineTop = 0, baseline = b.Y + b.Height;
            foreach (var line in lines)
            {
                if (mid >= lineTop && mid <= lineTop + line.Height) { baseline = lineTop + line.Baseline; break; }
                lineTop += line.Height;
            }
            it.Rect = new Rect(b.X, baseline - it.Baseline, it.Width, it.Height);
        }

        float y = PadY;
        if (m.ReplyName != null)
        {
            var reply = new CanvasTextLayout(device, $"↩ Replying to @{m.ReplyName}: {m.ReplyBody}", _smallFormat, width, 40)
            {
                Options = CanvasDrawTextOptions.EnableColorFont,
            };
            result.Reply = reply;
            y += (float)reply.LayoutBounds.Height + 1;
        }
        result.TextY = y;
        result.Text = layout;
        result.Inlines = inlines.ToArray();
        result.Height = y + (float)layout.LayoutBounds.Height + PadY;
        return result;
    }


    void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        _emoteHits.Clear();
        _msgHits.Clear();
        long now = _clock.ElapsedMilliseconds;
        _lastDraw = now;
        bool animated = false;

        var ch = _channel;
        if (ch == null) { _lastDrawAnimated = false; return; }

        float viewW = (float)sender.ActualWidth, viewH = (float)sender.ActualHeight;
        float width = ContentWidth;
        var msgs = ch.Messages;
        float y = viewH + _scroll;
        int i = msgs.Count - 1;
        for (; i >= 0; i--)
        {
            var m = msgs[i];
            var lay = GetLayout(m, width);
            float top = y - lay.Height;
            if (top < viewH)
                animated |= DrawMessage(ds, m, lay, top, viewW, now);
            y = top;
            if (y <= 0) break;
        }
        if (i < 0 && y > 0 && _scroll > 0)
        {
            // Scrolled past the oldest message, clamp.
            _scroll = Math.Max(0, _scroll - y);
            _target = Math.Min(_target, _scroll);
            _needsFrame = true;
            RequestFrame();
        }

        DrawPill(ds, viewW, viewH);
        DrawTooltip(ds, viewW, viewH, now);
        _lastDrawAnimated = animated;
        if (animated) RequestFrame();
    }

    bool DrawMessage(CanvasDrawingSession ds, ChatMessage m, MessageLayout lay, float top, float viewW, long now)
    {
        bool animated = false;
        bool hover = _pointer is { } p && p.Y >= top && p.Y < top + lay.Height;

        if (m.Highlighted)
        {
            ds.FillRectangle(0, top, viewW, lay.Height, Color.FromArgb(46, 255, 72, 96));
            ds.FillRectangle(0, top, 3, lay.Height, Color.FromArgb(255, 255, 79, 94));
        }
        else if (m.Kind == MessageKind.UserNotice)
        {
            ds.FillRectangle(0, top, viewW, lay.Height, Color.FromArgb(34, 145, 71, 255));
            ds.FillRectangle(0, top, 3, lay.Height, AccentColor);
        }
        else if (m.FirstMessage)
        {
            ds.FillRectangle(0, top, viewW, lay.Height, Color.FromArgb(26, 60, 220, 140));
            ds.FillRectangle(0, top, 3, lay.Height, Color.FromArgb(255, 60, 220, 140));
        }
        if (hover) ds.FillRectangle(0, top, viewW, lay.Height, Color.FromArgb(14, 255, 255, 255));

        _msgHits.Add((top, lay.Height, PadX, m, lay));

        using (m.Deleted ? ds.CreateLayer(0.35f) : null)
        {
            if (lay.Reply != null) ds.DrawTextLayout(lay.Reply, PadX, top + PadY, SubtleColor);
            float tx = PadX, ty = top + lay.TextY;
            if (lay.Text != null) ds.DrawTextLayout(lay.Text, tx, ty, m.Kind == MessageKind.System ? SubtleColor : TextColor);

            foreach (var it in lay.Inlines)
            {
                var r = new Rect(it.Rect.X + tx, it.Rect.Y + ty, it.Rect.Width, it.Rect.Height);
                if (it.Image.FrameAt(now) is { } frame)
                {
                    ds.DrawImage(frame, r, frame.Bounds, 1f, CanvasImageInterpolation.HighQualityCubic);
                    animated |= it.Image.IsAnimated;
                }
                if (it.Overlays != null)
                {
                    foreach (var o in it.Overlays)
                    {
                        if (o.Image.FrameAt(now) is not { } of) continue;
                        float ow = o.Width > 0 ? o.Width : (float)r.Width, oh = o.Height > 0 ? o.Height : (float)r.Height;
                        var or = new Rect(r.X + (r.Width - ow) / 2, r.Y + (r.Height - oh) / 2, ow, oh);
                        ds.DrawImage(of, or, of.Bounds, 1f, CanvasImageInterpolation.HighQualityCubic);
                        animated |= o.Image.IsAnimated;
                    }
                }
                _emoteHits.Add((r, it));
            }
        }
        return animated;
    }

    void DrawPill(CanvasDrawingSession ds, float viewW, float viewH)
    {
        if (_target <= 2 && _scroll <= 2) { _pillRect = Rect.Empty; return; }
        var label = _unseen > 0 ? $"↓  {_unseen} new message{(_unseen == 1 ? "" : "s")}" : "↓  Chat paused · jump to latest";
        using var tl = new CanvasTextLayout(ds, label, _pillFormat, 1000, 40);
        float w = (float)tl.LayoutBounds.Width + 28, h = 30;
        float x = (viewW - w) / 2, y = viewH - h - 10;
        ds.FillRoundedRectangle(x, y + 2, w, h, h / 2, h / 2, Color.FromArgb(90, 0, 0, 0));
        ds.FillRoundedRectangle(x, y, w, h, h / 2, h / 2, AccentColor);
        ds.DrawTextLayout(tl, x + 14, y + (h - (float)tl.LayoutBounds.Height) / 2 - 1, Colors.White);
        _pillRect = new Rect(x, y, w, h);
    }

    void DrawTooltip(CanvasDrawingSession ds, float viewW, float viewH, long now)
    {
        if (_pointer is not { } p) return;
        InlineItem? item = null;
        foreach (var (r, it) in _emoteHits)
            if (r.Contains(p)) { item = it; break; }
        if (item == null) return;

        var frame = item.Image.FrameAt(now);
        float scale = item.Emote != null ? 3f : 2f;
        float iw = (float)item.Rect.Width * scale, ih = (float)item.Rect.Height * scale;
        if (ih > 112) { iw *= 112 / ih; ih = 112; }

        string title = item.Emote?.Name ?? item.Title ?? "";
        string? sub = item.Emote != null
            ? item.Emote.ProviderLabel + (item.Emote.Author is { Length: > 0 } a ? $" · by {a}" : "")
            : null;
        if (item.Overlays != null) sub += " · + " + string.Join(", ", item.Overlays.Select(o => o.Name));

        using var tTitle = new CanvasTextLayout(ds, title, _tipTitleFormat, 400, 40);
        using var tSub = sub != null ? new CanvasTextLayout(ds, sub, _smallFormat, 400, 40) : null;
        float textW = Math.Max((float)tTitle.LayoutBounds.Width, (float)(tSub?.LayoutBounds.Width ?? 0));
        float boxW = Math.Max(iw, textW) + 24;
        float boxH = 12 + ih + 8 + (float)tTitle.LayoutBounds.Height + (tSub != null ? (float)tSub.LayoutBounds.Height + 2 : 0) + 10;

        float bx = Math.Clamp((float)p.X - boxW / 2, 6, Math.Max(6, viewW - boxW - 6));
        float by = (float)p.Y - boxH - 14;
        if (by < 6) by = (float)p.Y + 22;

        ds.FillRoundedRectangle(bx, by + 3, boxW, boxH, 10, 10, Color.FromArgb(110, 0, 0, 0));
        ds.FillRoundedRectangle(bx, by, boxW, boxH, 10, 10, Color.FromArgb(250, 28, 28, 33));
        ds.DrawRoundedRectangle(bx, by, boxW, boxH, 10, 10, Color.FromArgb(40, 255, 255, 255));
        float cy = by + 12;
        if (frame != null)
            ds.DrawImage(frame, new Rect(bx + (boxW - iw) / 2, cy, iw, ih), frame.Bounds, 1f, CanvasImageInterpolation.HighQualityCubic);
        cy += ih + 8;
        ds.DrawTextLayout(tTitle, bx + (boxW - (float)tTitle.LayoutBounds.Width) / 2 - (float)tTitle.LayoutBounds.X, cy, TextColor);
        cy += (float)tTitle.LayoutBounds.Height + 2;
        if (tSub != null) ds.DrawTextLayout(tSub, bx + (boxW - (float)tSub.LayoutBounds.Width) / 2, cy, SubtleColor);
    }


    void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        _target = Math.Max(0, _target + delta * 0.6f);
        RequestFrame();
        e.Handled = true;
    }

    void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _pointer = e.GetCurrentPoint(this).Position;
        bool hand = _pillRect.Contains(_pointer.Value) || LinkAt(_pointer.Value) != null;
        if (hand != _handCursor)
        {
            _handCursor = hand;
            ProtectedCursor = InputSystemCursor.Create(hand ? InputSystemCursorShape.Hand : InputSystemCursorShape.Arrow);
        }
        _canvas.Invalidate();
    }

    void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        var pos = pt.Position;
        if (pt.Properties.IsLeftButtonPressed)
        {
            if (_pillRect.Contains(pos)) { ScrollToBottom(); return; }
            if (LinkAt(pos) is { } url)
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            }
        }
        else if (pt.Properties.IsRightButtonPressed && MessageAt(pos) is { } hit)
        {
            ShowMessageMenu(hit.Msg, pos);
        }
    }

    (float Top, float Height, float X, ChatMessage Msg, MessageLayout Layout)? MessageAt(Point p)
    {
        foreach (var h in _msgHits)
            if (p.Y >= h.Top && p.Y < h.Top + h.Height) return h;
        return null;
    }

    string? LinkAt(Point p)
    {
        if (MessageAt(p) is not { } h || h.Layout.Links.Count == 0 || h.Layout.Text == null) return null;
        var lx = (float)p.X - h.X;
        var ly = (float)p.Y - h.Top - h.Layout.TextY;
        if (!h.Layout.Text.HitTest(lx, ly, out var region)) return null;
        foreach (var (s, n, url) in h.Layout.Links)
            if (region.CharacterIndex >= s && region.CharacterIndex < s + n) return url;
        return null;
    }

    void ShowMessageMenu(ChatMessage m, Point pos)
    {
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action act)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => act();
            menu.Items.Add(item);
        }
        Add("Copy message", "", () => Copy(m.PlainText));
        if (m.Login.Length > 0)
        {
            Add($"Copy username", "", () => Copy(m.Login));
            Add($"Mention @{m.DisplayName}", "", () => MentionRequested?.Invoke(m.DisplayName));
        }
        menu.ShowAt(this, pos);
    }

    static void Copy(string text)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }

    public event Action<string>? MentionRequested;
}
