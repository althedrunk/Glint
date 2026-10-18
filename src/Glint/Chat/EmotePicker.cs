using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Glint.Chat;

// Searchable, sectioned emote grid. Drawn with Win2D like the chat, so only visible cells cost anything
// and animated emotes play.
public sealed partial class EmotePicker : UserControl, IDisposable
{
    const float Cell = 40, EmoteBox = 30, HeaderH = 26, Pad = 8;

    readonly TextBox _search = new() { PlaceholderText = "Search emotes" };
    readonly TextBlock _status = new() { FontSize = 12, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0, 2, 0) };
    readonly CanvasControl _canvas = new() { ClearColor = Colors.Transparent };
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly CanvasTextFormat _headerFormat = new() { FontFamily = "Segoe UI Variable Text", FontSize = 12, FontWeight = FontWeights.SemiBold };
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _animate;

    List<(string Title, List<EmoteInfo> Emotes)> _sections = new();
    List<(string Title, List<EmoteInfo> Emotes)> _shown = new();
    readonly List<(Rect Rect, EmoteInfo Emote)> _hits = new();
    float _scroll;
    Point? _pointer;
    bool _disposed;

    public event Action<EmoteInfo>? EmoteChosen;

    public EmotePicker()
    {
        Width = 372;
        Height = 420;
        var grid = new Grid { RowSpacing = 6 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_canvas, 1);
        Grid.SetRow(_status, 2);
        grid.Children.Add(_search);
        grid.Children.Add(_canvas);
        grid.Children.Add(_status);
        Content = grid;

        _search.TextChanged += (_, _) => { Filter(); _scroll = 0; _canvas.Invalidate(); };
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter && _shown.FirstOrDefault().Emotes?.FirstOrDefault() is { } first)
            {
                Choose(first);
                e.Handled = true;
            }
        };
        _canvas.Draw += OnDraw;
        _canvas.PointerMoved += (_, e) => { _pointer = e.GetCurrentPoint(_canvas).Position; UpdateHover(); _canvas.Invalidate(); };
        _canvas.PointerExited += (_, _) => { _pointer = null; UpdateHover(); _canvas.Invalidate(); };
        _canvas.PointerWheelChanged += (_, e) =>
        {
            _scroll = Math.Max(0, _scroll - e.GetCurrentPoint(_canvas).Properties.MouseWheelDelta * 0.6f);
            _canvas.Invalidate();
            e.Handled = true;
        };
        _canvas.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed) return;
            var p = e.GetCurrentPoint(_canvas).Position;
            foreach (var (rect, emote) in _hits)
                if (rect.Contains(p)) { Choose(emote); break; }
        };
        ImageCache.ImageLoaded += OnImageLoaded;

        // Animated emotes and images still loading both need redraws; 30fps is plenty here.
        _animate = DispatcherQueue.CreateTimer();
        _animate.Interval = TimeSpan.FromMilliseconds(33);
        _animate.Tick += (_, _) => _canvas.Invalidate();
        Loaded += (_, _) => { _animate.Start(); _search.Focus(FocusState.Programmatic); };
        Unloaded += (_, _) => _animate.Stop();
    }

    void OnImageLoaded() => DispatcherQueue.TryEnqueue(() => { if (!_disposed) _canvas.Invalidate(); });

    public void SetSections(List<(string Title, List<EmoteInfo> Emotes)> sections)
    {
        _sections = sections.Where(s => s.Emotes.Count > 0).ToList();
        Filter();
        _canvas.Invalidate();
    }

    void Filter()
    {
        var q = _search.Text.Trim();
        if (q.Length == 0) { _shown = _sections; return; }
        var matches = _sections.SelectMany(s => s.Emotes)
            .Where(e => e.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.Name).Select(g => g.First())
            .OrderByDescending(e => e.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            .ThenBy(e => e.Name.Length).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _shown = new() { (matches.Count > 0 ? "Results" : "No matches", matches) };
    }

    void Choose(EmoteInfo e)
    {
        var recent = Settings.Current.RecentEmotes;
        recent.Remove(e.Name);
        recent.Insert(0, e.Name);
        if (recent.Count > 27) recent.RemoveRange(27, recent.Count - 27);
        Settings.Save();
        EmoteChosen?.Invoke(e);
    }

    void UpdateHover()
    {
        EmoteInfo? hover = null;
        if (_pointer is { } p)
            foreach (var (rect, emote) in _hits)
                if (rect.Contains(p)) { hover = emote; break; }
        _status.Text = hover == null ? "" : $"{hover.Name}  ·  {hover.ProviderLabel}";
        ProtectedCursor = InputSystemCursor.Create(hover != null ? InputSystemCursorShape.Hand : InputSystemCursorShape.Arrow);
    }

    void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        _hits.Clear();
        long now = _clock.ElapsedMilliseconds;
        float width = (float)sender.ActualWidth, height = (float)sender.ActualHeight;
        int perRow = Math.Max(1, (int)((width - Pad) / Cell));

        float total = 0;
        foreach (var s in _shown) total += HeaderH + (s.Emotes.Count + perRow - 1) / perRow * Cell + 6;
        _scroll = Math.Min(_scroll, Math.Max(0, total - height));

        float y = -_scroll;
        foreach (var (title, emotes) in _shown)
        {
            if (y + HeaderH > 0 && y < height)
                ds.DrawText(title.ToUpperInvariant(), Pad, y + 6, Color.FromArgb(255, 160, 160, 170), _headerFormat);
            y += HeaderH;
            int rows = (emotes.Count + perRow - 1) / perRow;
            for (int row = 0; row < rows; row++, y += Cell)
            {
                if (y + Cell < 0 || y > height) continue;
                for (int col = 0; col < perRow; col++)
                {
                    int i = row * perRow + col;
                    if (i >= emotes.Count) break;
                    var e = emotes[i];
                    var cell = new Rect(Pad / 2 + col * Cell, y, Cell, Cell);
                    if (_pointer is { } p && cell.Contains(p))
                        ds.FillRoundedRectangle(cell, 6, 6, Color.FromArgb(40, 255, 255, 255));
                    if (e.Image.FrameAt(now) is { } frame)
                    {
                        // Fit inside the box, keeping the aspect ratio (wide emotes get narrower).
                        var size = frame.Size;
                        float scale = (float)Math.Min(EmoteBox / size.Width, EmoteBox / size.Height);
                        float w = (float)size.Width * scale, h = (float)size.Height * scale;
                        ds.DrawImage(frame, new Rect(cell.X + (Cell - w) / 2, cell.Y + (Cell - h) / 2, w, h), frame.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
                    }
                    _hits.Add((cell, e));
                }
            }
            y += 6;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _animate.Stop();
        ImageCache.ImageLoaded -= OnImageLoaded;
        _canvas.RemoveFromVisualTree();
        _headerFormat.Dispose();
    }
}
