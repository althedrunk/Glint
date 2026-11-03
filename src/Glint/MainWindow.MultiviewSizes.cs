using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Glint;

// Drag handles in the gaps between multiview videos and between chat panes. Sizes are saved as
// relative weights per layout, and the toolbar's reset button puts everything back to even.
public sealed partial class MainWindow
{
    // Star size for column/row i, from saved weights when they match the current count.
    static GridLength Weight(List<double> saved, int i, int count, double fallback = 1) =>
        new(saved.Count == count && saved[i] > 0 ? saved[i] : fallback, GridUnitType.Star);

    // One handle per gap. cross pins the handles to a single row/column (otherwise they span them all).
    void AddSplitters(Grid grid, bool columns, List<double> saved, int cross = -1)
    {
        int count = columns ? grid.ColumnDefinitions.Count : grid.RowDefinitions.Count;
        for (int i = 0; i < count - 1; i++) grid.Children.Add(MakeSplitter(grid, columns, i, count, saved, cross));
    }

    GripSplitter MakeSplitter(Grid grid, bool columns, int index, int count, List<double> saved, int cross)
    {
        var idle = new SolidColorBrush(Colors.Transparent);
        var splitter = new GripSplitter { Background = idle };
        splitter.SetVerticalResize(!columns);
        double gap = columns ? grid.ColumnSpacing : grid.RowSpacing;
        // Sits over the gap after its column/row, a couple of pixels wider so it's easy to grab.
        if (columns)
        {
            splitter.Width = gap + 4;
            splitter.HorizontalAlignment = HorizontalAlignment.Right;
            splitter.Margin = new Thickness(0, 0, -(gap + 2), 0);
            Grid.SetColumn(splitter, index);
            if (cross >= 0) Grid.SetRow(splitter, cross);
            else Grid.SetRowSpan(splitter, Math.Max(1, grid.RowDefinitions.Count));
        }
        else
        {
            splitter.Height = gap + 4;
            splitter.VerticalAlignment = VerticalAlignment.Bottom;
            splitter.Margin = new Thickness(0, 0, 0, -(gap + 2));
            Grid.SetRow(splitter, index);
            if (cross >= 0) Grid.SetColumn(splitter, cross);
            else Grid.SetColumnSpan(splitter, Math.Max(1, grid.ColumnDefinitions.Count));
        }
        Canvas.SetZIndex(splitter, 10);
        ToolTipService.SetToolTip(splitter, "Drag to resize, double-click to even out");

        bool dragging = false;
        double start = 0, first = 0, second = 0;
        double Pos(PointerRoutedEventArgs e) => columns ? e.GetCurrentPoint(grid).Position.X : e.GetCurrentPoint(grid).Position.Y;
        double Size(int i) => columns ? grid.ColumnDefinitions[i].ActualWidth : grid.RowDefinitions[i].ActualHeight;
        void Set(int i, double px)
        {
            var length = new GridLength(Math.Max(1, px), GridUnitType.Star);
            if (columns) grid.ColumnDefinitions[i].Width = length;
            else grid.RowDefinitions[i].Height = length;
        }
        void Finish()
        {
            if (!dragging) return;
            dragging = false;
            splitter.Background = idle;
            double total = Enumerable.Range(0, count).Sum(Size);
            if (total <= 0) return;
            saved.Clear();
            saved.AddRange(Enumerable.Range(0, count).Select(i => Math.Round(Size(i) / total * count, 4)));
            Settings.Save();
        }

        splitter.PointerEntered += (_, _) => splitter.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(90, Appearance.Accent.R, Appearance.Accent.G, Appearance.Accent.B));
        splitter.PointerExited += (_, _) => { if (!dragging) splitter.Background = idle; };
        splitter.PointerPressed += (_, e) =>
        {
            dragging = true;
            start = Pos(e);
            first = Size(index);
            second = Size(index + 1);
            for (int i = 0; i < count; i++) Set(i, Size(i)); // hold the others where they are
            splitter.CapturePointer(e.Pointer);
            e.Handled = true;
        };
        splitter.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            const double min = 120;
            double size = Math.Clamp(first + Pos(e) - start, min, Math.Max(min, first + second - min));
            Set(index, size);
            Set(index + 1, first + second - size);
        };
        splitter.PointerReleased += (_, e) => { Finish(); splitter.ReleasePointerCapture(e.Pointer); };
        splitter.PointerCaptureLost += (_, _) => Finish();
        splitter.DoubleTapped += (_, e) =>
        {
            saved.Clear();
            Settings.Save();
            ApplyMultiviewLayout();
            e.Handled = true;
        };
        return splitter;
    }

    void ResetMultiviewSizes()
    {
        foreach (var list in new[] { S.MultiviewVideoColumns, S.MultiviewVideoRows, S.MultiviewMainColumns, S.MultiviewMainRows, S.MultiviewChatColumns, S.MultiviewChatRows })
            list.Clear();
        var defaults = new AppSettings();
        S.MultiviewChatWidth = defaults.MultiviewChatWidth;
        S.MultiviewChatHeight = defaults.MultiviewChatHeight;
        _multiviewUpdating = true;
        MultiviewChatWidth.Value = S.MultiviewChatWidth;
        MultiviewChatHeight.Value = S.MultiviewChatHeight;
        _multiviewUpdating = false;
        Settings.Save();
        ApplyMultiviewLayout();
    }
}
