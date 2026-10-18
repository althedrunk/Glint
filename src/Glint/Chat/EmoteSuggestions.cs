using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Glint.Chat;

public sealed partial class EmoteSuggestions : UserControl, IDisposable
{
    sealed class Row
    {
        public Button Button = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 4, 8, 4), Height = 44, BorderThickness = new Thickness(0), IsTabStop = false, AllowFocusOnInteraction = false };
        public CanvasControl Preview = new() { Width = 32, Height = 32, ClearColor = Colors.Transparent };
        public TextBlock Name = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        public TextBlock Provider = new() { FontSize = 11, Opacity = 0.65 };
        public EmoteInfo? Emote;
        public int Index;
        public bool Hovered;
    }

    readonly Row[] _rows = Enumerable.Range(0, 6).Select(_ => new Row()).ToArray();
    readonly TextBlock _count = new() { FontSize = 11, Opacity = 0.65, HorizontalAlignment = HorizontalAlignment.Right };
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _animation;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    List<EmoteInfo> _items = new();
    int _selected, _first, _visibleRows = 6;
    bool _disposed;
    public event Action<EmoteInfo>? Chosen;
    public EmoteInfo? Selected => _items.ElementAtOrDefault(_selected);

    public EmoteSuggestions()
    {
        IsTabStop = false;
        AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, e) =>
        {
            Move(e.GetCurrentPoint(this).Properties.MouseWheelDelta > 0 ? -1 : 1);
            e.Handled = true;
        }), true);
        AutomationProperties.SetName(this, "Emote suggestions");
        var panel = new StackPanel { Spacing = 2 };
        var header = new Grid { Margin = new Thickness(8, 4, 8, 4) };
        header.Children.Add(new TextBlock { Text = "Emotes", FontSize = 12, Opacity = 0.8 });
        header.Children.Add(_count); panel.Children.Add(header);
        foreach (var row in _rows)
        {
            var content = new Grid { ColumnSpacing = 10 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition());
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(row.Name); labels.Children.Add(row.Provider);
            content.Children.Add(row.Preview); Grid.SetColumn(labels, 1); content.Children.Add(labels);
            row.Button.Content = content;
            row.Button.Click += (_, _) => { if (row.Emote is { } emote) Chosen?.Invoke(emote); };
            row.Button.PointerEntered += (_, _) => { row.Hovered = true; _selected = row.Index; UpdateRows(); };
            row.Button.PointerExited += (_, _) => { row.Hovered = false; row.Preview.Invalidate(); };
            row.Preview.Draw += (_, e) => DrawPreview(row, e.DrawingSession);
            panel.Children.Add(row.Button);
        }
        panel.Children.Add(new TextBlock { Text = "Arrows choose · Tab / Enter insert · Esc close", FontSize = 11,
            Opacity = 0.65, Margin = new Thickness(8, 4, 8, 4), TextWrapping = TextWrapping.Wrap });
        Content = new Border { Child = panel, Padding = new Thickness(4), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), Background = new SolidColorBrush(Color.FromArgb(255, 32, 32, 38)) };
        _animation = DispatcherQueue.CreateTimer();
        _animation.Interval = TimeSpan.FromMilliseconds(33);
        _animation.Tick += (_, _) =>
        {
            foreach (var row in _rows)
                if (row.Button.Visibility == Visibility.Visible && row.Emote?.Image.IsAnimated == true && Animate(row)) row.Preview.Invalidate();
        };
        Loaded += (_, _) => _animation.Start();
        Unloaded += (_, _) => _animation.Stop();
        ImageCache.ImageLoaded += ImageLoaded;
    }

    public void SetItems(List<EmoteInfo> items, int visibleRows)
    {
        string? selectedName = Selected?.Name;
        _items = items;
        _selected = Math.Max(0, items.FindIndex(e => e.Name == selectedName));
        _visibleRows = Math.Clamp(visibleRows, 1, _rows.Length);
        UpdateRows();
    }

    public void Move(int direction)
    {
        if (_items.Count == 0) return;
        _selected = (_selected + direction + _items.Count) % _items.Count;
        UpdateRows();
    }

    void UpdateRows()
    {
        _first = Math.Clamp(_first, 0, Math.Max(0, _items.Count - _visibleRows));
        if (_selected < _first) _first = _selected;
        if (_selected >= _first + _visibleRows) _first = _selected - _visibleRows + 1;
        _count.Text = _items.Count == 0 ? "" : $"{_selected + 1} / {_items.Count}";
        var selected = Appearance.Accent; selected.A = 45;
        for (int i = 0; i < _rows.Length; i++)
        {
            var row = _rows[i];
            row.Index = _first + i;
            row.Emote = i < _visibleRows ? _items.ElementAtOrDefault(row.Index) : null;
            row.Button.Visibility = row.Emote == null ? Visibility.Collapsed : Visibility.Visible;
            if (row.Emote is not { } emote) continue;
            row.Name.Text = emote.Name; row.Provider.Text = emote.ProviderLabel;
            row.Button.Background = new SolidColorBrush(row.Index == _selected ? selected : Colors.Transparent);
            AutomationProperties.SetName(row.Button, $"Insert emote {emote.Name}");
            AutomationProperties.SetItemStatus(row.Button, row.Index == _selected ? "Selected" : "");
            row.Preview.Invalidate();
        }
    }

    static bool Animate(Row row) => Settings.Current.EmoteAnimation == "Always" || Settings.Current.EmoteAnimation == "On hover" && row.Hovered;

    void DrawPreview(Row row, CanvasDrawingSession ds)
    {
        if (row.Emote?.Image.FrameAt(Animate(row) ? _clock.ElapsedMilliseconds : 0) is not { } frame) return;
        var size = frame.Size;
        double scale = Math.Min(32 / size.Width, 32 / size.Height);
        double width = size.Width * scale, height = size.Height * scale;
        ds.DrawImage(frame, new Rect((32 - width) / 2, (32 - height) / 2, width, height), frame.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
    }

    void ImageLoaded() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_disposed) return;
        foreach (var row in _rows) if (row.Button.Visibility == Visibility.Visible) row.Preview.Invalidate();
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _animation.Stop(); ImageCache.ImageLoaded -= ImageLoaded;
        foreach (var row in _rows) row.Preview.RemoveFromVisualTree();
    }
}
