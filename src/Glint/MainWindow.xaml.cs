using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Glint.Chat;
using Glint.Player;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using WinRT.Interop;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Glint;

// The window is split across a few partial files:
//   MainWindow.Channels.cs  sidebar, tabs, info bar
//   MainWindow.Player.cs    live playback, ad skipping, fullscreen
//   MainWindow.Vods.cs      past broadcasts + chat replay
//   MainWindow.ChatInput.cs sending messages, tab completion
public sealed partial class MainWindow : Window
{
    readonly IntPtr _hwnd;
    readonly DispatcherQueue _dispatcher;
    readonly TwitchIrc _irc = new();
    readonly SevenTvEvents _stv = new();
    readonly ConcurrentDictionary<string, ChatChannel> _chats = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ChannelEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    readonly ObservableCollection<ChannelEntry> _sidebarItems = new();
    readonly List<ChannelTab> _tabs = new();
    readonly DispatcherQueueTimer _refreshTimer, _clockTimer, _vodTimer;

    ChannelTab? _active;
    bool _dialogOpen, _fullscreen;

    static AppSettings S => Settings.Current;

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _dispatcher = DispatcherQueue;

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.Title = "Glint";
        AppWindow.SetIcon(Asset("Glint.ico"));
        RestorePlacement();

        BrandLogo.Source = new BitmapImage(new Uri(Asset("Logo64.png")));
        EmptyLogo.Source = new BitmapImage(new Uri(Asset("Logo512.png")));

        ChannelList.ItemsSource = _sidebarItems;
        _sidebarItems.CollectionChanged += (_, _) =>
            SidebarEmpty.Visibility = _sidebarItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        WireUi();
        ApplyLayoutSettings();

        _refreshTimer = Timer(TimeSpan.FromSeconds(60), async () => await RefreshLiveAsync());
        _clockTimer = Timer(TimeSpan.FromSeconds(1), UpdateInfoMeta);
        _vodTimer = Timer(TimeSpan.FromMilliseconds(250), VodTick);

        AppWindow.Closing += (_, _) => Shutdown();
        Root.Loaded += async (_, _) =>
        {
            UpdateCaptionInset();
            await StartupAsync();
        };
    }

    static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    DispatcherQueueTimer Timer(TimeSpan interval, Action tick)
    {
        var t = _dispatcher.CreateTimer();
        t.Interval = interval;
        t.Tick += (_, _) => tick();
        return t;
    }

    async Task StartupAsync()
    {
        _irc.MessageReceived += m =>
        {
            if (m.Channel is { } c && _chats.TryGetValue(c, out var chat)) chat.OnIrc(m);
        };
        _irc.ConnectionChanged += ok => _dispatcher.TryEnqueue(() =>
            ConnectionDot.Fill = new SolidColorBrush(ok ? Windows.UI.Color.FromArgb(255, 60, 220, 140) : Windows.UI.Color.FromArgb(255, 255, 79, 94)));
        _stv.EmoteSetChanged += (set, actor, added, removed, old) =>
        {
            foreach (var chat in _chats.Values)
                if (chat.Emotes.SevenTvSetId == set) chat.OnSevenTvChange(actor, added, removed, old);
        };
        _stv.Start();

        foreach (var fav in S.Favorites) GetEntry(fav).IsFavorite = true;
        RebuildSidebar();

        var activeLogin = S.ActiveTab;
        foreach (var login in S.OpenTabs.ToList()) OpenChannel(login, activate: false);
        var toSelect = _tabs.FirstOrDefault(t => t.Login == activeLogin) ?? _tabs.FirstOrDefault();
        if (toSelect != null) Tabs.SelectedItem = toSelect.Item;
        UpdateEmptyState();

        var token = await TwitchAuth.EnsureTokenAsync();
        StartIrc(token);
        _ = RefreshLiveAsync();
        if (token != null) await LoadFollowedAsync(token);
        _refreshTimer.Start();
        _clockTimer.Start();
    }

    void StartIrc(string? token)
    {
        _irc.Start(S.UserLogin, token);
        foreach (var t in _tabs)
        {
            t.Chat.SelfLogin = token != null ? S.UserLogin : null;
            _irc.Join(t.Login);
        }
        UpdateAccountUi();
        UpdateChatInput();
    }

    async Task LoadFollowedAsync(string token)
    {
        try
        {
            var follows = await TwitchAuth.GetFollowedLoginsAsync(token);
            foreach (var e in _entries.Values) e.IsFollowed = false;
            foreach (var login in follows) GetEntry(login).IsFollowed = true;
            await RefreshLiveAsync();
        }
        catch (Exception ex) { Log.Error("Followed", ex); }
    }

    void Shutdown()
    {
        SavePlacement();
        SaveTabs();
        Settings.SaveNow();
        try { _player?.Dispose(); } catch { }
        _ads?.Dispose();
        _irc.Dispose();
        _stv.Dispose();
    }

    void RestorePlacement()
    {
        if (S.WindowWidth is int w && S.WindowHeight is int h && w > 400 && h > 300)
        {
            var area = DisplayArea.GetFromRect(new Windows.Graphics.RectInt32(S.WindowX ?? 0, S.WindowY ?? 0, w, h), DisplayAreaFallback.Nearest);
            int x = Math.Clamp(S.WindowX ?? 0, area.WorkArea.X, Math.Max(area.WorkArea.X, area.WorkArea.X + area.WorkArea.Width - 200));
            int y = Math.Clamp(S.WindowY ?? 0, area.WorkArea.Y, Math.Max(area.WorkArea.Y, area.WorkArea.Y + area.WorkArea.Height - 200));
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
        }
        else
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            int width = Math.Min(1600, (int)(area.Width * 0.85)), height = Math.Min(940, (int)(area.Height * 0.85));
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        }
        if (S.WindowMaximized && AppWindow.Presenter is OverlappedPresenter p) p.Maximize();
    }

    void SavePlacement()
    {
        if (_fullscreen || AppWindow.Presenter is not OverlappedPresenter p) return;
        S.WindowMaximized = p.State == OverlappedPresenterState.Maximized;
        if (p.State == OverlappedPresenterState.Restored)
        {
            S.WindowX = AppWindow.Position.X;
            S.WindowY = AppWindow.Position.Y;
            S.WindowWidth = AppWindow.Size.Width;
            S.WindowHeight = AppWindow.Size.Height;
        }
    }

    void UpdateCaptionInset()
    {
        var scale = Root.XamlRoot?.RasterizationScale ?? 1;
        CaptionInset.Width = new GridLength(Math.Max(0, AppWindow.TitleBar.RightInset / scale) + 4);
    }

    void WireUi()
    {
        SidebarToggle.Click += (_, _) => { S.SidebarCollapsed = !S.SidebarCollapsed; Settings.Save(); ApplyLayoutSettings(); };
        SettingsButton.Click += async (_, _) => await ShowSettingsAsync();
        AccountButton.Click += async (_, _) => await ShowSettingsAsync();

        AddBox.KeyDown += (_, e) => AddBoxKey(AddBox, e);
        AddBoxCompact.KeyDown += (_, e) => { AddBoxKey(AddBoxCompact, e); if (e.Handled) AddFlyout.Hide(); };
        EmptyAddBox.KeyDown += (_, e) => AddBoxKey(EmptyAddBox, e);

        ChannelList.ItemClick += (_, e) => { if (e.ClickedItem is ChannelEntry ce) OpenChannel(ce.Login, true); };
        ChannelList.RightTapped += ChannelList_RightTapped;

        Tabs.SelectionChanged += (_, _) =>
        {
            if (Tabs.SelectedItem is TabViewItem { Tag: ChannelTab t }) ActivateTab(t);
        };
        Tabs.TabCloseRequested += (_, e) => { if (e.Tab.Tag is ChannelTab t) CloseTab(t); };
        Tabs.TabItemsChanged += (_, _) => SaveTabs();

        WirePlayerControls();
        WireVodControls();

        VideoArea.SizeChanged += (_, _) => UpdateVideoBounds();
        Root.LayoutUpdated += (_, _) => UpdateVideoBounds();
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidPresenterChange || e.DidSizeChange) UpdateCaptionInset();
        };

        ChatInput.KeyDown += ChatInput_KeyDown;
        ChatInput.PreviewKeyDown += ChatInput_PreviewKeyDown;
        Chat.MentionRequested += name =>
        {
            ChatInput.Text = $"@{name} " + ChatInput.Text;
            ChatInput.SelectionStart = ChatInput.Text.Length;
            ChatInput.Focus(FocusState.Programmatic);
        };

        WireSplitter();
        WireKeyboard();
    }

    void WireKeyboard()
    {
        void Accel(VirtualKey key, VirtualKeyModifiers mods, Action action)
        {
            var a = new KeyboardAccelerator { Key = key, Modifiers = mods };
            a.Invoked += (_, e) => { action(); e.Handled = true; };
            Root.KeyboardAccelerators.Add(a);
        }
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        Accel(VirtualKey.W, VirtualKeyModifiers.Control, () => { if (_active != null) CloseTab(_active); });
        Accel(VirtualKey.Tab, VirtualKeyModifiers.Control, () => CycleTab(1));
        Accel(VirtualKey.Tab, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => CycleTab(-1));
        Accel(VirtualKey.L, VirtualKeyModifiers.Control, FocusAddBox);
        Accel(VirtualKey.T, VirtualKeyModifiers.Control, FocusAddBox);
        Accel(VirtualKey.F11, VirtualKeyModifiers.None, ToggleFullscreen);
        Accel(VirtualKey.F5, VirtualKeyModifiers.None, Reload);

        // Single-letter shortcuts only when you're not typing somewhere.
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox or ButtonBase or Slider or ComboBox) return;
            switch (e.Key)
            {
                case VirtualKey.F: ToggleFullscreen(); break;
                case VirtualKey.T: ToggleTheatre(); break;
                case VirtualKey.C when _fullscreen: ToggleFullscreenChat(); break;
                case VirtualKey.M: ToggleMute(); break;
                case VirtualKey.Space: TogglePause(); break;
                case VirtualKey.Left when _active?.Vod != null: SeekBy(-10); break;
                case VirtualKey.Right when _active?.Vod != null: SeekBy(10); break;
                case VirtualKey.Escape when _fullscreen: ToggleFullscreen(); break;
                case VirtualKey.Escape when _vodPanelOpen: CloseVodPanel(); break;
                case VirtualKey.Escape when S.Theatre: ToggleTheatre(); break;
                default: return;
            }
            e.Handled = true;
        }), true);
    }

    void FocusAddBox()
    {
        if (_tabs.Count == 0) EmptyAddBox.Focus(FocusState.Keyboard);
        else if (S.SidebarCollapsed) AddFlyout.ShowAt(AddCompactButton);
        else AddBox.Focus(FocusState.Keyboard);
    }

    void WireSplitter()
    {
        double startX = 0, startWidth = 0;
        bool dragging = false;
        Splitter.PointerPressed += (_, e) =>
        {
            dragging = true;
            startX = e.GetCurrentPoint(Body).Position.X;
            startWidth = ChatCol.ActualWidth;
            Splitter.CapturePointer(e.Pointer);
            e.Handled = true;
        };
        Splitter.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var x = e.GetCurrentPoint(Body).Position.X;
            var w = Math.Clamp(startWidth - (x - startX), 260, Math.Max(260, Body.ActualWidth - SidebarCol.ActualWidth - 320));
            ChatCol.Width = new GridLength(w);
        };
        Splitter.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            Splitter.ReleasePointerCapture(e.Pointer);
            S.ChatWidth = ChatCol.ActualWidth;
            Settings.Save();
        };
        Splitter.PointerCaptureLost += (_, _) => dragging = false;
    }

    // Layouts: normal, theatre (no sidebar or info bar), fullscreen (video only, or video + chat),
    // and chat-only. Everything that depends on them is decided here.
    void ApplyLayoutSettings()
    {
        bool fs = _fullscreen;
        bool theatre = S.Theatre && !fs;
        bool chrome = !fs && !theatre;
        TitleRow.Height = new GridLength(fs ? 0 : 44);
        TitleBar.Visibility = fs ? Visibility.Collapsed : Visibility.Visible;
        InfoBar.Visibility = chrome ? Visibility.Visible : Visibility.Collapsed;
        VideoArea.CornerRadius = new CornerRadius(chrome ? 8 : 0, 0, 0, 0);

        bool collapsed = S.SidebarCollapsed;
        SidebarCol.Width = new GridLength(!chrome ? 0 : collapsed ? 56 : 244);
        Sidebar.Visibility = chrome ? Visibility.Visible : Visibility.Collapsed;
        Sidebar.Padding = collapsed ? new Thickness(4, 2, 4, 8) : new Thickness(8, 2, 6, 8);
        AddBox.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        AddCompactButton.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        SidebarHeader.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarEmpty.Opacity = collapsed ? 0 : 1;
        AccountButton.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ChannelList.ItemTemplate = (DataTemplate)Root.Resources[collapsed ? "ChannelCompact" : "ChannelExpanded"];
        ChannelList.Margin = collapsed ? new Thickness(-2, 0, -2, 0) : new Thickness(-4, 0, -4, 0);

        bool chatOnly = S.ChatOnly && !fs;
        // Column widths don't depend on whether tabs are open yet (visibility handles that), otherwise
        // opening the first tab at startup leaves the chat column at zero width.
        bool chat = !fs || S.FullscreenChat;
        MainCol.Width = chatOnly ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ChatCol.Width = !chat ? new GridLength(0) : chatOnly ? new GridLength(1, GridUnitType.Star) : new GridLength(S.ChatWidth);
        SplitterCol.Width = new GridLength(chat && !chatOnly ? 5 : 0);
        ShowVideoButton.Visibility = chatOnly ? Visibility.Visible : Visibility.Collapsed;
        ExitTheatreButton.Visibility = theatre ? Visibility.Visible : Visibility.Collapsed;
        HideChatButton.Visibility = fs ? Visibility.Visible : Visibility.Collapsed;
        UpdateEmptyState();
        UpdateModeUi();
    }

    bool ChatShown => _tabs.Count > 0 && (!_fullscreen || S.FullscreenChat);

    void UpdateEmptyState()
    {
        bool empty = _tabs.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        MainArea.Visibility = empty || S.ChatOnly ? Visibility.Collapsed : Visibility.Visible;
        ChatPanel.Visibility = ChatShown ? Visibility.Visible : Visibility.Collapsed;
        Splitter.Visibility = ChatShown && !(S.ChatOnly && !_fullscreen) ? Visibility.Visible : Visibility.Collapsed;
        UpdateVideoBounds();
    }

    static void Launch(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    void UpdateAccountUi()
    {
        AccountText.Text = _irc.IsAuthenticated && S.UserLogin != null ? S.UserLogin : "Log in";
    }

    async Task ShowSettingsAsync()
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        UpdateVideoBounds(); // dialogs draw under the video window otherwise
        try
        {
            var dialog = new SettingsDialog(this) { XamlRoot = Root.XamlRoot };
            await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
            UpdateVideoBounds();
        }
    }

    internal async Task OnLoggedInAsync()
    {
        var token = await TwitchAuth.EnsureTokenAsync();
        StartIrc(token);
        if (token != null) await LoadFollowedAsync(token);
    }

    internal void OnLoggedOut()
    {
        TwitchAuth.Logout();
        foreach (var e in _entries.Values) e.IsFollowed = false;
        RebuildSidebar();
        StartIrc(null);
    }

    internal void OnChatStyleChanged() => Chat.RebuildFormats();
}
