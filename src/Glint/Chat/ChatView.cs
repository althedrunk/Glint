using System.Diagnostics;
using System.Numerics;
using System.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
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
    public CanvasTextLayout? Header;
    public Color HeaderColor;
    public float HeaderY, ReplyY;
    public InlineItem[] Inlines = Array.Empty<InlineItem>();
    public List<(int Start, int Length, string Url)> Links = new();
    public Rect[] UsernameRegions = Array.Empty<Rect>();

    // 7TV paint on the username
    public ICanvasBrush? NameBrush;
    public Rect NameRect;
    public AnimatedImage? PaintImage;
    public AnimatedImage? PendingPaintImage; // image paint not loaded yet; re-lay out once it is
    public List<(ICanvasImage Effect, PaintShadow Shadow)>? NameShadows;
}

// GPU-drawn chat. Messages are laid out once (DirectWrite) and cached; only the visible window is drawn,
// bottom-anchored so trimming old messages never moves the view.
public sealed partial class ChatView : UserControl, IDisposable
{
    const float PadX = 10, BadgeSize = 18, EmoteFallback = 28;
    static float PadY => (float)Settings.Current.ChatMessageSpacing;

    static readonly Color TextColor = Color.FromArgb(255, 239, 239, 241);
    static readonly Color SubtleColor = Color.FromArgb(255, 150, 150, 160);
    static readonly Color LinkColor = Color.FromArgb(255, 160, 150, 255);
    static Color AccentColor => Appearance.Accent;
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
    readonly List<(Rect Rect, ChatMessage Msg)> _replyLineHits = new();
    (Rect Rect, ChatMessage Msg)? _replyButton;
    bool _handCursor;
    bool _hoverPaused, _resumeAfterHover;
    string _filterText = "";

    public event Action? MessagesChanged;
    public bool IsHistoryView { get; set; }
    public bool IsPaused => _hoverPaused || _target > 2 || _scroll > 2;
    public int VisibleMessageCount => _channel?.Messages.Count(IsVisible) ?? 0;
    public string FilterText
    {
        get => _filterText;
        set { _filterText = value.Trim(); RefreshMessages(); }
    }

    bool IsVisible(ChatMessage message) => !ChatFilters.IsIgnored(message) && (_filterText.Length == 0 ||
        message.PlainText.Contains(_filterText, StringComparison.OrdinalIgnoreCase) ||
        message.DisplayName.Contains(_filterText, StringComparison.OrdinalIgnoreCase) ||
        message.ChannelLabel?.Contains(_filterText, StringComparison.OrdinalIgnoreCase) == true ||
        message.SystemText?.Contains(_filterText, StringComparison.OrdinalIgnoreCase) == true);

    public void RefreshMessages(bool resetScroll = true)
    {
        if (_disposed) return;
        if (resetScroll) { _scroll = _target = 0; _unseen = 0; }
        if (IsHistoryView || !Settings.Current.PauseChatOnHover) EndHoverPause();
        _needsFrame = true;
        RequestFrame();
        _canvas.Invalidate();
        MessagesChanged?.Invoke();
    }

    void StartHoverPause()
    {
        if (_hoverPaused || IsHistoryView || !Settings.Current.PauseChatOnHover) return;
        _resumeAfterHover = _target <= 2 && _scroll <= 2;
        _hoverPaused = true;
        _canvas.Invalidate();
    }

    void EndHoverPause()
    {
        bool resume = _hoverPaused && _resumeAfterHover;
        _hoverPaused = false;
        if (resume) ScrollToBottom();
    }

    public ChatView()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _canvas = new CanvasControl { ClearColor = Colors.Transparent };
        _canvas.Draw += OnDraw;
        Content = _canvas;
        IsTabStop = false;
        AutomationProperties.SetName(this, "Chat messages");
        RebuildFormats();

        AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnWheel), true);
        PointerMoved += OnPointerMoved;
        PointerEntered += (_, e) => { _pointer = e.GetCurrentPoint(this).Position; StartHoverPause(); };
        PointerExited += (_, _) => { _pointer = null; EndHoverPause(); _canvas.Invalidate(); };
        PointerPressed += OnPointerPressed;
        _canvas.SizeChanged += (_, _) => { _needsFrame = true; RequestFrame(); _canvas.Invalidate(); };

        Loaded += (_, _) =>
        {
            if (_disposed || _listening) return;
            _listening = true;
            ImageCache.ImageLoaded += OnImageLoaded;
            ImageCache.SizeDiscovered += OnImageSizeDiscovered;
            SevenTvCosmetics.Changed += OnImageSizeDiscovered;
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
        if (_hoverPaused && _resumeAfterHover) { _scroll = _target = 0; _unseen = 0; }
        _hoverPaused = false;
        _pointer = null;
        _listening = false;
        ImageCache.ImageLoaded -= OnImageLoaded;
        ImageCache.SizeDiscovered -= OnImageSizeDiscovered;
        SevenTvCosmetics.Changed -= OnImageSizeDiscovered;
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
            FontFamily = Settings.Current.ChatFontFamily,
            FontSize = size,
            WordWrapping = CanvasWordWrapping.Wrap,
            Options = CanvasDrawTextOptions.EnableColorFont,
        };
        if (Settings.Current.ChatLineSpacing > 1)
        {
            _format.LineSpacingMode = CanvasLineSpacingMode.Proportional;
            _format.LineSpacing = (float)Settings.Current.ChatLineSpacing;
        }
        _smallFormat = new CanvasTextFormat
        {
            FontFamily = Settings.Current.ChatFontFamily,
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
        _hoverPaused = false;
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
        _hoverPaused = false;
        // Resume immediately so incoming messages cannot preserve the old scroll offset.
        _scroll = _target = 0;
        _unseen = 0;
        _needsFrame = true;
        RequestFrame();
        _canvas.Invalidate();
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
            if (IsHistoryView) _canvas.Invalidate();
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

        dirty |= DrainMessages();

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
        if (!_hoverPaused && _target <= 0 && _scroll < 0.5f) _unseen = 0;

        if (_lastDrawAnimated && now - _lastDraw >= 15) dirty = true;
        if (dirty) _canvas.Invalidate();

        if (!busy && !_lastDrawAnimated && !dirty && _channel?.HasPending != true)
        {
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
        }
    }

    bool DrainMessages()
    {
        if (_channel is not { HasPending: true } channel) return false;
        var added = channel.Drain().Where(IsVisible).ToList();
        if (added.Count > 0 && (_hoverPaused || _target > 0.5f || _scroll > 0.5f))
        {
            // Keep the reader's position while scrolled up.
            float height = 0;
            foreach (var message in added) height += GetLayout(message, ContentWidth).Height;
            _scroll += height;
            _target += height;
            _unseen += added.Count;
        }
        MessagesChanged?.Invoke();
        return added.Count > 0;
    }

    float ContentWidth => Math.Max(50, (float)_canvas.ActualWidth - PadX * 2);


    MessageLayout GetLayout(ChatMessage m, float width)
    {
        if (m.Layout is MessageLayout l && l.Width == width && l.Version == _layoutVersion
            && l.PendingPaintImage is not { IsLoaded: true }) return l;
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
        int nameStart = -1;
        string name = "";

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
                : m.Time.ToString(Settings.Current.TimestampFormat);
            Span(stamp + " ", SubtleColor);
        }
        if (m.ChannelLabel is { } channelLabel) Span(channelLabel + " ", SubtleColor, b: true);

        if (m.Kind is MessageKind.System or MessageKind.UserNotice && m.SystemText is { Length: > 0 } sys)
        {
            Span(sys, m.Kind == MessageKind.UserNotice ? Color.FromArgb(255, 210, 190, 255) : SubtleColor,
                b: m.Kind == MessageKind.UserNotice, i: m.Kind == MessageKind.System);
            if (m.Fragments.Count > 0) sb.Append(m.Kind == MessageKind.UserNotice ? "\n" : " ");
        }

        if (m.Kind != MessageKind.System || m.Fragments.Count > 0)
        {
            if (m.Kind != MessageKind.System)
            {
                foreach (var badge in Settings.Current.ShowBadges ? m.Badges : Enumerable.Empty<BadgeRef>())
                {
                    Inline(badge.Image, BadgeSize, BadgeSize, 0, null, null, badge.Title);
                    sb.Append(' ');
                }
                if (SevenTvCosmetics.BadgeFor(m.UserId) is { } stvBadge)
                {
                    Inline(ImageCache.Get(stvBadge.Url), BadgeSize, BadgeSize, 0, null, null, stvBadge.Tooltip);
                    sb.Append(' ');
                }
                name = m.DisplayName.Equals(m.Login, StringComparison.OrdinalIgnoreCase) || m.Login.Length == 0
                    ? m.DisplayName
                    : $"{m.DisplayName} ({m.Login})";
                nameStart = sb.Length;
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
                        h *= (float)Settings.Current.EmoteScale;
                        w *= (float)Settings.Current.EmoteScale;
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
        if (nameStart >= 0 && name.Length > 0)
            result.UsernameRegions = layout.GetCharacterRegions(nameStart, name.Length).Select(r => r.LayoutBounds).ToArray();

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

        if (nameStart >= 0 && SevenTvCosmetics.PaintFor(m.UserId) is { } paint)
            ApplyPaint(result, layout, paint, nameStart, name, device);

        float y = PadY;
        if (HeaderFor(m) is { } header)
        {
            result.Header = new CanvasTextLayout(device, header.Text, _smallFormat, width, 40) { Options = CanvasDrawTextOptions.EnableColorFont };
            result.Header.SetFontWeight(0, header.Text.Length, FontWeights.SemiBold);
            result.HeaderColor = header.Color;
            result.HeaderY = y;
            y += (float)result.Header.LayoutBounds.Height + 1;
        }
        if (m.ReplyName != null && ShowReplyContext)
        {
            var reply = new CanvasTextLayout(device, $"↩ Replying to @{m.ReplyName}: {m.ReplyBody}", _smallFormat, width, 40)
            {
                Options = CanvasDrawTextOptions.EnableColorFont,
            };
            result.Reply = reply;
            result.ReplyY = y;
            y += (float)reply.LayoutBounds.Height + 1;
        }
        result.TextY = y;
        result.Text = layout;
        result.Inlines = inlines.ToArray();
        result.Height = y + (float)layout.LayoutBounds.Height + PadY;
        return result;
    }


    static readonly Color FirstChatColor = Color.FromArgb(255, 70, 220, 150);
    static readonly Color MentionColor = Color.FromArgb(255, 255, 79, 94);
    static readonly Color UserHighlightColor = Color.FromArgb(255, 255, 176, 46);

    static (string Text, Color Color)? HeaderFor(ChatMessage m)
    {
        if (m.ChannelPointsHighlight) return ("Highlighted message", Color.FromArgb(255, 200, 170, 255));
        if (m.NoticeId == "announcement") return ("Announcement", AnnouncementColor(m.AnnouncementColor));
        if (m.FirstMessage) return ("First-time chatter", FirstChatColor);
        return null;
    }

    static Color AnnouncementColor(string? name) => name switch
    {
        "BLUE" => Color.FromArgb(255, 30, 160, 255),
        "GREEN" => Color.FromArgb(255, 0, 200, 120),
        "ORANGE" => Color.FromArgb(255, 255, 150, 30),
        _ => AccentColor, // PRIMARY / PURPLE
    };

    // Background tint and left bar for messages that should stand out.
    static (Color Fill, Color Bar)? Emphasis(ChatMessage m)
    {
        static Color Tint(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
        if (m.Highlighted) return (Tint(MentionColor, 46), MentionColor);
        if (m.Highlight == HighlightKind.User) return (Tint(UserHighlightColor, 34), UserHighlightColor);
        if (m.ChannelPointsHighlight) return (Tint(AccentColor, 56), AccentColor);
        if (m.NoticeId == "announcement") { var c = AnnouncementColor(m.AnnouncementColor); return (Tint(c, 40), c); }
        if (m.Kind == MessageKind.UserNotice) return (Tint(AccentColor, 34), AccentColor);
        if (m.FirstMessage) return (Tint(FirstChatColor, 26), FirstChatColor);
        return null;
    }

    // Set by the window: replying needs a login and a live channel.
    public bool AllowReplies { get; set; }

    // The thread view turns this off; every message in it is a reply to the same thing.
    public bool ShowReplyContext { get; set; } = true;

    bool CanReply(ChatMessage m) =>
        AllowReplies && m.Id != null && m.Kind is MessageKind.Normal or MessageKind.Action && !m.Deleted && _channel?.IsReplay != true;

    void ApplyPaint(MessageLayout result, CanvasTextLayout layout, SevenTvPaint paint, int start, string name, CanvasDevice device)
    {
        var regions = layout.GetCharacterRegions(start, name.Length);
        if (regions.Length == 0) return;
        var r = regions[0].LayoutBounds;
        foreach (var reg in regions.Skip(1)) r.Union(reg.LayoutBounds);
        float w = (float)r.Width, h = (float)r.Height;
        var centre = new Vector2((float)(r.X + w / 2), (float)(r.Y + h / 2));

        // CSS repeating gradients repeat after the last stop, so stretch the stops over [0, last] and wrap.
        float last = paint.Repeat ? Math.Clamp(paint.Stops.LastOrDefault()?.At ?? 1, 0.05f, 1) : 1;
        var stops = paint.Stops.Select(s => new CanvasGradientStop { Position = Math.Clamp(s.At / last, 0, 1), Color = s.Color }).ToArray();
        var edge = paint.Repeat ? CanvasEdgeBehavior.Wrap : CanvasEdgeBehavior.Clamp;

        ICanvasBrush brush;
        switch (paint.Kind)
        {
            case PaintKind.Image:
                var image = ImageCache.Get(paint.ImageUrl!);
                if (!image.IsLoaded)
                {
                    // Keep the normal name colour until the texture arrives.
                    if (!image.Failed) result.PendingPaintImage = image;
                    return;
                }
                brush = new CanvasImageBrush(device) { ExtendX = CanvasEdgeBehavior.Wrap, ExtendY = CanvasEdgeBehavior.Wrap };
                result.PaintImage = image;
                break;
            case PaintKind.Radial:
                // CSS default size is farthest-corner.
                float rx = w / 2 * MathF.Sqrt(2), ry = h / 2 * MathF.Sqrt(2);
                if (paint.Circle) rx = ry = MathF.Sqrt(w * w + h * h) / 2;
                brush = new CanvasRadialGradientBrush(device, stops, edge, CanvasAlphaMode.Premultiplied)
                {
                    Center = centre, RadiusX = rx * last, RadiusY = ry * last,
                };
                break;
            default:
                // CSS angles: 0deg points up, 90deg points right.
                float a = paint.Angle * MathF.PI / 180;
                var dir = new Vector2(MathF.Sin(a), -MathF.Cos(a));
                float len = MathF.Abs(w * dir.X) + MathF.Abs(h * dir.Y);
                var from = centre - dir * len / 2;
                brush = new CanvasLinearGradientBrush(device, stops, edge, CanvasAlphaMode.Premultiplied)
                {
                    StartPoint = from, EndPoint = from + dir * len * last,
                };
                break;
        }
        layout.SetBrush(start, name.Length, brush);
        result.NameBrush = brush;
        result.NameRect = r;

        if (paint.Shadows.Count == 0) return;
        // Paint shadows are drop-shadows of the name's glyphs, so draw the name on its own and shadow that.
        using var nameLayout = new CanvasTextLayout(device, name, _format, 10000, 1000);
        nameLayout.SetFontWeight(0, name.Length, FontWeights.SemiBold);
        double lineTop = 0, baseline = r.Y + r.Height * 0.8;
        foreach (var line in layout.LineMetrics)
        {
            if (centre.Y >= lineTop && centre.Y <= lineTop + line.Height) { baseline = lineTop + line.Baseline; break; }
            lineTop += line.Height;
        }
        var shape = new CanvasCommandList(device);
        using (var cds = shape.CreateDrawingSession())
            cds.DrawTextLayout(nameLayout, (float)r.X, (float)(baseline - nameLayout.LineMetrics[0].Baseline), Colors.White);
        result.NameShadows = paint.Shadows
            .Select(s => ((ICanvasImage)new ShadowEffect { Source = shape, ShadowColor = s.Color, BlurAmount = Math.Max(0, s.Radius / 2) }, s))
            .ToList();
    }

    void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (IsHistoryView) DrainMessages();
        var ds = args.DrawingSession;
        _emoteHits.Clear();
        _msgHits.Clear();
        _replyLineHits.Clear();
        _replyButton = null;
        long now = _clock.ElapsedMilliseconds;
        _lastDraw = now;
        bool animated = false;

        var ch = _channel;
        if (ch == null) { _lastDrawAnimated = false; return; }
        AutomationProperties.SetItemStatus(this, IsHistoryView
            ? IsPaused ? "Reading earlier messages" : "Latest messages"
            : IsPaused ? "Chat paused" : "Chat live");

        float viewW = (float)sender.ActualWidth, viewH = (float)sender.ActualHeight;
        float width = ContentWidth;
        var msgs = ch.Messages;
        float y = viewH + _scroll;
        int i = msgs.Count - 1;
        for (; i >= 0; i--)
        {
            var m = msgs[i];
            if (!IsVisible(m)) continue;
            var lay = GetLayout(m, width);
            float top = y - lay.Height;
            if (top < viewH)
            {
                if (Settings.Current.AlternateChatRows && i % 2 == 0)
                    ds.FillRectangle(0, top, viewW, lay.Height, Color.FromArgb(9, 255, 255, 255));
                animated |= DrawMessage(ds, m, lay, top, viewW, now);
            }
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
        bool animateEmotes = Settings.Current.EmoteAnimation == "Always" || Settings.Current.EmoteAnimation == "On hover" && hover;
        long emoteTime = animateEmotes ? now : 0;

        if (Emphasis(m) is { } em)
        {
            ds.FillRectangle(0, top, viewW, lay.Height, em.Fill);
            ds.FillRectangle(0, top, 3, lay.Height, em.Bar);
        }
        if (hover) ds.FillRectangle(0, top, viewW, lay.Height, Color.FromArgb(14, 255, 255, 255));

        _msgHits.Add((top, lay.Height, PadX, m, lay));

        using (m.Deleted ? ds.CreateLayer(0.35f) : null)
        {
            if (lay.Header != null) ds.DrawTextLayout(lay.Header, PadX, top + lay.HeaderY, lay.HeaderColor);
            if (lay.Reply != null)
            {
                var replyRect = new Rect(PadX, top + lay.ReplyY, lay.Reply.LayoutBounds.Width, lay.Reply.LayoutBounds.Height);
                bool overReply = _pointer is { } pp && replyRect.Contains(pp);
                ds.DrawTextLayout(lay.Reply, PadX, top + lay.ReplyY, overReply ? TextColor : SubtleColor);
                _replyLineHits.Add((replyRect, m));
            }
            float tx = PadX, ty = top + lay.TextY;
            if (lay.NameBrush != null) animated |= PreparePaint(ds, lay, tx, ty, now);
            if (lay.Text != null) ds.DrawTextLayout(lay.Text, tx, ty, m.Kind == MessageKind.System ? SubtleColor : TextColor);

            foreach (var it in lay.Inlines)
            {
                var r = new Rect(it.Rect.X + tx, it.Rect.Y + ty, it.Rect.Width, it.Rect.Height);
                if (it.Image.FrameAt(emoteTime) is { } frame)
                {
                    ds.DrawImage(frame, r, frame.Bounds, 1f, CanvasImageInterpolation.HighQualityCubic);
                    animated |= animateEmotes && it.Image.IsAnimated;
                }
                if (it.Overlays != null)
                {
                    foreach (var o in it.Overlays)
                    {
                        if (o.Image.FrameAt(emoteTime) is not { } of) continue;
                        float ow = o.Width > 0 ? o.Width * (float)Settings.Current.EmoteScale : (float)r.Width;
                        float oh = o.Height > 0 ? o.Height * (float)Settings.Current.EmoteScale : (float)r.Height;
                        var or = new Rect(r.X + (r.Width - ow) / 2, r.Y + (r.Height - oh) / 2, ow, oh);
                        ds.DrawImage(of, or, of.Bounds, 1f, CanvasImageInterpolation.HighQualityCubic);
                        animated |= animateEmotes && o.Image.IsAnimated;
                    }
                }
                _emoteHits.Add((r, it));
            }
        }

        if (hover && CanReply(m))
        {
            var rect = new Rect(viewW - 34, top + 2, 28, 22);
            bool over = _pointer is { } bp && rect.Contains(bp);
            ds.FillRoundedRectangle(rect, 5, 5, Color.FromArgb(over ? (byte)230 : (byte)200, 45, 45, 52));
            ds.DrawRoundedRectangle(rect, 5, 5, Color.FromArgb(50, 255, 255, 255));
            using var path = new CanvasPathBuilder(ds);
            float x = (float)rect.X, y = (float)rect.Y;
            path.BeginFigure(x + 13, y + 5);
            path.AddLine(x + 7, y + 11);
            path.AddLine(x + 13, y + 17);
            path.EndFigure(CanvasFigureLoop.Open);
            path.BeginFigure(x + 7, y + 11);
            path.AddLine(x + 16, y + 11);
            path.AddCubicBezier(new Vector2(x + 21, y + 11), new Vector2(x + 22, y + 13), new Vector2(x + 22, y + 18));
            path.EndFigure(CanvasFigureLoop.Open);
            using var geometry = CanvasGeometry.CreatePath(path);
            ds.DrawGeometry(geometry, over ? Colors.White : TextColor, 1.7f);
            _replyButton = (rect, m);
        }
        return animated;
    }

    // Brushes live in drawing-session space, so move them to wherever this message is being drawn.
    static bool PreparePaint(CanvasDrawingSession ds, MessageLayout lay, float tx, float ty, long now)
    {
        bool animated = false;
        if (lay.NameShadows != null)
            foreach (var (effect, s) in lay.NameShadows)
                ds.DrawImage(effect, tx + s.X, ty + s.Y);

        if (lay.NameBrush is CanvasImageBrush ib && lay.PaintImage?.FrameAt(now) is { } frame)
        {
            var r = lay.NameRect;
            var size = frame.Size;
            float scale = (float)Math.Max(r.Width / size.Width, r.Height / size.Height);
            ib.Image = frame;
            ib.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation((float)r.X + tx, (float)r.Y + ty);
            animated = lay.PaintImage.IsAnimated;
        }
        else if (lay.NameBrush is CanvasLinearGradientBrush lb) lb.Transform = Matrix3x2.CreateTranslation(tx, ty);
        else if (lay.NameBrush is CanvasRadialGradientBrush rb) rb.Transform = Matrix3x2.CreateTranslation(tx, ty);
        return animated;
    }

    void DrawPill(CanvasDrawingSession ds, float viewW, float viewH)
    {
        if (!IsPaused) { _pillRect = Rect.Empty; return; }
        var label = IsHistoryView ? (_unseen > 0 ? $"↓  {_unseen} new message{(_unseen == 1 ? "" : "s")} · latest" : "↓  Jump to latest")
            : _hoverPaused ? (_unseen > 0 ? $"Chat paused · {_unseen} new · resume" : "Chat paused · hovering")
            : _unseen > 0 ? $"↓  {_unseen} new message{(_unseen == 1 ? "" : "s")}" : "↓  Chat paused · jump to latest";
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

        var frame = item.Image.FrameAt(Settings.Current.EmoteAnimation == "Still" ? 0 : now);
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
        if (IsHistoryView) _scroll = _target;
        if (_hoverPaused) _resumeAfterHover = _target <= 2;
        _needsFrame = true;
        RequestFrame();
        _canvas.Invalidate();
        e.Handled = true;
    }

    void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        bool entering = _pointer == null;
        _pointer = e.GetCurrentPoint(this).Position;
        if (entering) StartHoverPause();
        var pos = _pointer.Value;
        bool hand = _pillRect.Contains(pos) || LinkAt(pos) != null || UserHistoryRequested != null && UsernameAt(pos) != null
                    || _replyButton is { } rb && rb.Rect.Contains(pos) || _replyLineHits.Any(h => h.Rect.Contains(pos));
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
            if (_replyButton is { } rb && rb.Rect.Contains(pos)) { ReplyRequested?.Invoke(rb.Msg); return; }
            foreach (var (rect, msg) in _replyLineHits)
                if (rect.Contains(pos)) { ThreadRequested?.Invoke(msg); return; }
            if (UsernameAt(pos) is { } user && UserHistoryRequested != null)
            {
                UserHistoryRequested.Invoke(user);
                return;
            }
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

    ChatMessage? UsernameAt(Point p)
    {
        if (MessageAt(p) is not { } hit || hit.Msg.Login.Length == 0) return null;
        var local = new Point(p.X - hit.X, p.Y - hit.Top - hit.Layout.TextY);
        return hit.Layout.UsernameRegions.Any(r => r.Contains(local)) ? hit.Msg : null;
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
        if (CanReply(m))
        {
            var reply = new MenuFlyoutItem { Text = "Reply", Icon = new SymbolIcon(Symbol.Back) };
            reply.Click += (_, _) => ReplyRequested?.Invoke(m);
            menu.Items.Add(reply);
        }
        if (m.ThreadId != null || m.ReplyParentId != null) Add("View thread", "\uE8BD", () => ThreadRequested?.Invoke(m));
        Add("Copy message", "", () => Copy(m.PlainText));
        if (m.Login.Length > 0)
        {
            if (UserHistoryRequested != null) Add("View user history", "\uE77B", () => UserHistoryRequested.Invoke(m));
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
    public event Action<ChatMessage>? ReplyRequested;
    public event Action<ChatMessage>? ThreadRequested;
    public event Action<ChatMessage>? UserHistoryRequested;
}
