using System.Runtime.InteropServices;
using Glint.Chat;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WinRT.Interop;

namespace Glint;

public sealed partial class ChatHistoryWindow : Window
{
    readonly TextBlock _count = new(), _empty = new();
    public ChatView View { get; }
    public bool IsClosed { get; private set; }

    public ChatHistoryWindow(IntPtr owner, string title, string description, ChatChannel channel, ChatView view, UIElement? action = null)
    {
        View = view;
        view.IsHistoryView = true;
        Title = $"{title} - Glint";
        SystemBackdrop = new MicaBackdrop();
        var root = new Grid { RequestedTheme = ElementTheme.Dark, Padding = new Thickness(24), RowSpacing = 12,
            Background = (Brush)Application.Current.Resources["GlintBackgroundBrush"] };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        if (action is FrameworkElement command)
        {
            command.VerticalAlignment = VerticalAlignment.Center;
            command.Height = 32;
            Grid.SetColumn(command, 1);
            header.Children.Add(command);
        }
        var subtitle = new TextBlock { Text = description, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        var search = new TextBox { PlaceholderText = "Search messages or usernames", Height = 36 };
        AutomationProperties.SetName(search, "Search history");
        search.TextChanged += (_, _) => View.FilterText = search.Text;
        var body = new Grid { Background = (Brush)Application.Current.Resources["GlintPanelBrush"], CornerRadius = new CornerRadius(8) };
        body.Children.Add(view);
        _empty.Text = "Messages will appear here as they arrive.";
        _empty.TextWrapping = TextWrapping.Wrap;
        _empty.TextAlignment = TextAlignment.Center;
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.Margin = new Thickness(30);
        _empty.Opacity = 0.7;
        _empty.IsHitTestVisible = false;
        body.Children.Add(_empty);
        _count.FontSize = 12;
        _count.Opacity = 0.7;
        root.Children.Add(header);
        Grid.SetRow(subtitle, 1); root.Children.Add(subtitle);
        Grid.SetRow(search, 2); root.Children.Add(search);
        Grid.SetRow(body, 3); root.Children.Add(body);
        Grid.SetRow(_count, 4); root.Children.Add(_count);
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) => { Close(); e.Handled = true; };
        root.KeyboardAccelerators.Add(escape);
        Content = root;
        SetWindowLongPtrW(WindowNative.GetWindowHandle(this), -8, owner);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 360;
            presenter.PreferredMinimumHeight = 360;
        }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(520, 640));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Glint.ico"));
        WindowChrome.UseDarkTitleBar(this);
        view.SetChannel(channel);
        view.MessagesChanged += UpdateSummary;
        root.Loaded += (_, _) => UpdateSummary();
        Closed += (_, _) =>
        {
            IsClosed = true;
            view.MessagesChanged -= UpdateSummary;
            view.Dispose();
        };
    }

    void UpdateSummary()
    {
        if (IsClosed) return;
        int count = View.VisibleMessageCount;
        _count.Text = $"{count:N0} message{(count == 1 ? "" : "s")}";
        _empty.Text = View.FilterText.Length > 0 ? "No messages match your search." : "No messages to show yet.";
        _empty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
}
