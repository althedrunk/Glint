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
    readonly DispatcherQueueTimer _refreshTimer, _viewerTimer, _clockTimer, _vodTimer;

    ChannelTab? _active;
    bool _fullscreen, _closing;
    SettingsWindow? _settingsWindow;
    Microsoft.UI.Dispatching.DispatcherQueueTimer _cutoutWatch = null!;

    static AppSettings S => Settings.Current;

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _dispatcher = DispatcherQueue;
        _overlay = new PlayerOverlay(_hwnd);
        _overlayTimer = Timer(TimeSpan.FromSeconds(2.5), HideIdleOverlay);

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
        ApplyAppearance();
        ApplyPlayerPreferences();

        _refreshTimer = Timer(TimeSpan.FromSeconds(60), async () => await RefreshLiveAsync());
        _viewerTimer = Timer(TimeSpan.FromSeconds(30), async () => await RefreshLiveAsync(openTabsOnly: true));
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
        SetMultiview(S.Multiview);
        UpdateEmptyState();

        _ = RefreshLiveAsync();
        await ApplyAccountAsync();
        _refreshTimer.Start();
        _viewerTimer.Start();
        _clockTimer.Start();
    }

    // The account chat, follows and emotes run as: the twitch.tv session when it's connected,
    // otherwise the API login. Null when logged out.
    string? _chatLogin;

    async Task ApplyAccountAsync()
    {
        ResetUserEmotes();
        await TwitchSession.CheckAsync();
        var apiToken = await TwitchAuth.EnsureTokenAsync();
        if (TwitchSession.IsConnected) StartIrc(TwitchSession.Login, S.WebSessionToken);
        else StartIrc(apiToken != null ? S.UserLogin : null, apiToken);
        await LoadFollowedAsync(apiToken);
    }

    void StartIrc(string? login, string? token)
    {
        _chatLogin = token != null ? login : null;
        _irc.Start(_chatLogin, token);
        foreach (var t in _tabs)
        {
            t.Chat.SelfLogin = _chatLogin;
            _irc.Join(t.Login);
        }
        UpdateAccountUi();
        UpdateChatInput();
    }

    async Task LoadFollowedAsync(string? apiToken)
    {
        try
        {
            var follows = TwitchSession.IsConnected ? await TwitchSession.GetFollowedLoginsAsync()
                        : apiToken != null ? await TwitchAuth.GetFollowedLoginsAsync(apiToken)
                        : new List<string>();
            if (follows == null) return; // request failed; keep what we had
            foreach (var e in _entries.Values) e.IsFollowed = false;
            foreach (var login in follows) GetEntry(login).IsFollowed = true;
            RebuildSidebar();
            await RefreshLiveAsync();
        }
        catch (Exception ex) { Log.Error("Followed", ex); }
    }

    void Shutdown()
    {
        _closing = true;
        _overlayTimer.Stop();
        _refreshTimer.Stop();
        _viewerTimer.Stop();
        _clockTimer.Stop();
        _vodTimer.Stop();
        _settingsWindow?.Close();
        NotificationSound.Stop();
        HideEmoteSuggestions();
        _emoteSuggestions?.Dispose();
        _overlay.Close();
        foreach (var window in _chatHistoryWindows.ToList()) window.Close();
        DisposeMultiviewChats();
        Chat.Dispose();
        SavePlacement();
        SaveTabs();
        Settings.SaveNow();
        foreach (var tab in _tabs)
        {
            try { tab.Playback.Dispose(); }
            catch (Exception ex) { Log.Error("Player shutdown", ex); }
        }
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

    // Tabs hang from the bottom of the title bar (like Edge), so line the logo and the buttons on the right
    // up with the tab row rather than the middle of the bar.
    void AlignTitleBar()
    {
        if (Tabs.TabItems.FirstOrDefault() is not TabViewItem first || first.ActualHeight <= 0)
        {
            BrandPanel.VerticalAlignment = TitleActions.VerticalAlignment = VerticalAlignment.Center;
            BrandPanel.Margin = TitleActions.Margin = new Thickness(0);
            return;
        }
        var top = first.TransformToVisual(TitleBar).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        foreach (var panel in new FrameworkElement[] { BrandPanel, TitleActions })
        {
            panel.VerticalAlignment = VerticalAlignment.Top;
            panel.Height = first.ActualHeight;
            panel.Margin = new Thickness(0, top, 0, 0);
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
        SettingsButton.Click += (_, _) => ShowSettings();
        AccountButton.Click += (_, _) => ShowSettings();

        AddBox.KeyDown += (_, e) => AddBoxKey(AddBox, e);
        AddBoxCompact.KeyDown += (_, e) => { AddBoxKey(AddBoxCompact, e); if (e.Handled) AddFlyout.Hide(); };
        EmptyAddBox.KeyDown += (_, e) => AddBoxKey(EmptyAddBox, e);

        ChannelList.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is not ChannelEntry entry) return;
            if (CtrlDown) OpenChannel(entry.Login, true);
            else OpenChannelInCurrentTab(entry.Login);
        };
        ChannelList.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (!e.GetCurrentPoint(ChannelList).Properties.IsMiddleButtonPressed) return;
            for (DependencyObject? node = e.OriginalSource as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node))
                if (node is FrameworkElement { DataContext: ChannelEntry entry })
                {
                    OpenChannel(entry.Login, true);
                    e.Handled = true;
                    break;
                }
        }), true);
        ChannelList.RightTapped += ChannelList_RightTapped;

        Tabs.SelectionChanged += (_, _) =>
        {
            if (_changingTabs) return;
            if (Tabs.SelectedItem is TabViewItem { Tag: ChannelTab t }) ActivateTab(t);
            else ActivateEmptyTab();
        };
        Tabs.AddTabButtonClick += (_, _) => OpenNewTab();
        Tabs.TabCloseRequested += (_, e) => CloseTabItem(e.Tab);
        Tabs.TabItemsChanged += (_, _) => SaveTabs();

        WirePlayerControls();
        WireVodControls();
        WireMultiview();

        VideoArea.SizeChanged += (_, _) => UpdateVideoBounds();
        Root.LayoutUpdated += (_, _) => UpdateVideoBounds();
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidPresenterChange || e.DidSizeChange) UpdateCaptionInset();
            if (e.DidPositionChange || e.DidSizeChange || e.DidPresenterChange) UpdateVideoBounds();
        };

        ChatInput.KeyDown += ChatInput_KeyDown;
        ChatInput.PreviewKeyDown += ChatInput_PreviewKeyDown;
        ChatInput.TextChanged += (_, _) => SaveChatDraft();
        Chat.MentionRequested += name =>
        {
            ChatInput.Text = $"@{name} " + ChatInput.Text;
            ChatInput.SelectionStart = ChatInput.Text.Length;
            ChatInput.Focus(FocusState.Programmatic);
        };

        WireReplies();
        WireEmotePicker();
        WireEmoteSuggestions();
        WireBrowse();
        WireClips();
        WireSession();
        _overlay.BackgroundPressed += CloseOverlayMenus;
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => CloseOverlayMenus()), true);
        _cutoutWatch = _dispatcher.CreateTimer();
        _cutoutWatch.Interval = TimeSpan.FromMilliseconds(120);
        _cutoutWatch.Tick += (_, _) =>
        {
            UpdateVideoBounds();
            if (Root.XamlRoot == null || VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Count == 0) _cutoutWatch.Stop();
        };
        Tabs.SizeChanged += (_, _) => AlignTitleBar();
        Tabs.TabItemsChanged += (_, _) => _dispatcher.TryEnqueue(AlignTitleBar);
        WireSplitter();
        WireKeyboard();
    }

    void WireKeyboard()
    {
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        RebuildShortcuts();
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnShortcutKey), true);
    }
    static bool CtrlDown => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    void FocusAddBox()
    {
        if (MultiviewActive) { ShowMultiviewAdd(); return; }
        if (_active == null) EmptyAddBox.Focus(FocusState.Keyboard);
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
        SidebarCol.Width = new GridLength(!chrome || MultiviewActive ? 0 : collapsed ? 56 : Math.Clamp(S.SidebarWidth, 200, 400));
        Sidebar.Visibility = chrome && !MultiviewActive ? Visibility.Visible : Visibility.Collapsed;
        Sidebar.Padding = collapsed ? new Thickness(4, 2, 4, 8) : new Thickness(8, 2, 6, 8);
        AddBox.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarBrowseText.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarBrowseButton.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        SidebarBrowseButton.Padding = collapsed ? new Thickness(10, 8, 10, 8) : new Thickness(10, 7, 10, 7);
        AddCompactButton.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        SidebarHeader.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarEmpty.Opacity = collapsed ? 0 : 1;
        AccountButton.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ChannelList.ItemTemplate = (DataTemplate)Root.Resources[collapsed ? "ChannelCompact" : "ChannelExpanded"];
        ChannelList.Margin = collapsed ? new Thickness(-2, 0, -2, 0) : new Thickness(-4, 0, -4, 0);

        bool chatOnly = S.ChatOnly && !fs;
        // Column widths don't depend on whether tabs are open yet (visibility handles that), otherwise
        // opening the first tab at startup leaves the chat column at zero width.
        bool chat = chatOnly || (fs ? S.FullscreenChat : !S.ChatHidden);
        MainCol.Width = chatOnly ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ChatCol.Width = !chat ? new GridLength(0) : chatOnly ? new GridLength(1, GridUnitType.Star) : new GridLength(S.ChatWidth);
        SplitterCol.Width = new GridLength(chat && !chatOnly ? 5 : 0);
        ShowVideoButton.Visibility = chatOnly ? Visibility.Visible : Visibility.Collapsed;
        UpdateEmptyState();
        UpdateModeUi();
        ApplyMultiviewLayout();
    }

    bool ChatShown => _active != null && (S.ChatOnly && !_fullscreen || (_fullscreen ? S.FullscreenChat : !S.ChatHidden));

    void UpdateEmptyState()
    {
        bool empty = _active == null;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        MainArea.Visibility = empty || S.ChatOnly || MultiviewActive ? Visibility.Collapsed : Visibility.Visible;
        ChatPanel.Visibility = ChatShown && !MultiviewActive ? Visibility.Visible : Visibility.Collapsed;
        Splitter.Visibility = ChatShown && !MultiviewActive && !(S.ChatOnly && !_fullscreen) ? Visibility.Visible : Visibility.Collapsed;
        MultiviewRoot.Visibility = MultiviewActive ? Visibility.Visible : Visibility.Collapsed;
        UpdateVideoBounds();
    }

    static void Launch(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    void UpdateAccountUi()
    {
        AccountText.Text = _irc.IsAuthenticated && _chatLogin != null ? _chatLogin : "Log in";
    }

    void ShowSettings()
    {
        if (_closing) return;
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        var window = _settingsWindow = new SettingsWindow(this, _hwnd);
        window.Closed += (_, _) => _settingsWindow = null;
        window.Activate();
    }

    internal Task OnLoggedInAsync() => ApplyAccountAsync();

    internal async void OnLoggedOut()
    {
        TwitchAuth.Logout();
        await ApplyAccountAsync();
    }

    internal void OnChatStyleChanged()
    {
        Chat.RebuildFormats();
        foreach (var pane in _multiviewChats.Values) pane.View.RebuildFormats();
        foreach (var window in _chatHistoryWindows) window.View.RebuildFormats();
    }

    internal void OnChatPreferencesChanged()
    {
        QueueEmoteSuggestions();
        Chat.RefreshMessages(false);
        foreach (var pane in _multiviewChats.Values) pane.View.RefreshMessages(false);
        foreach (var window in _chatHistoryWindows) window.View.RefreshMessages(false);
        _mentions.Drain();
        _unreadMentions = Math.Min(_unreadMentions, _mentions.Messages.Count(m => !ChatFilters.IsIgnored(m)));
        UpdateMentionsBadge();
        IgnoreWordsButton.Opacity = S.IgnoredWords.Count > 0 ? 1 : 0.7;
    }

    internal void OnAdSkippingChanged()
    {
        foreach (var tab in _tabs.Where(t => t.Vod == null && t.Playback.Started).ToList())
            if (tab.Playback.SelectedVariant is { } variant) LoadLive(tab, variant);
    }
}
