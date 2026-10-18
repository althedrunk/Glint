using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace Glint;

public sealed class StreamCard
{
    public StreamCard(BrowseStream s)
    {
        Stream = s;
        Title = s.Live ? s.Title ?? "" : "Offline";
        DisplayName = s.DisplayName;
        Game = s.Game ?? "";
        ViewersText = $"{ChannelEntry.FormatCount(s.Viewers)} viewers";
        LiveVisibility = s.Live ? Visibility.Visible : Visibility.Collapsed;
        CardOpacity = s.Live ? 1 : 0.6;
        // Twitch caches previews for a few minutes; a changing query string keeps them fresh.
        if (s.Thumbnail != null) Thumbnail = new BitmapImage(new Uri($"{s.Thumbnail}?t={DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute / 2}")) { DecodePixelWidth = 292 };
        else if (s.Avatar != null) Thumbnail = new BitmapImage(new Uri(s.Avatar)) { DecodePixelWidth = 164 };
        if (s.Avatar != null) Avatar = new BitmapImage(new Uri(s.Avatar)) { DecodePixelWidth = 64 };
    }

    public BrowseStream Stream { get; }
    public string Title { get; }
    public string DisplayName { get; }
    public string Game { get; }
    public string ViewersText { get; }
    public Visibility LiveVisibility { get; }
    public double CardOpacity { get; }
    public ImageSource? Thumbnail { get; }
    public ImageSource? Avatar { get; }
    public override string ToString() => $"{DisplayName}: {Title}";
}

public sealed class CategoryCard
{
    public CategoryCard(BrowseCategory c)
    {
        Category = c;
        DisplayName = c.DisplayName;
        ViewersText = c.Viewers > 0 ? $"{ChannelEntry.FormatCount(c.Viewers)} viewers" : "";
        if (c.BoxArt != null) BoxArt = new BitmapImage(new Uri(c.BoxArt)) { DecodePixelWidth = 150 };
    }

    public BrowseCategory Category { get; }
    public string DisplayName { get; }
    public string ViewersText { get; }
    public ImageSource? BoxArt { get; }
    public override string ToString() => DisplayName;
}

// The Browse page: top live channels, categories, one category's streams, and search.
public sealed partial class MainWindow
{
    enum BrowseMode { Live, Categories, Category, Search }

    BrowseMode _browseMode = BrowseMode.Live;
    bool _browseOpen, _browseLoaded;
    int _browseVersion;
    (DateTime At, List<BrowseStream> Items)? _topStreams;
    (DateTime At, List<BrowseCategory> Items)? _topCategories;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _searchDebounce;

    void WireBrowse()
    {
        SidebarBrowseButton.Click += (_, _) => { if (_browseOpen) CloseBrowse(); else OpenBrowse(); };
        // Picking a tab (even the one already selected) takes you back to it.
        Tabs.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
        {
            for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
                if (d is TabViewItem) { CloseBrowse(); return; }
        }), true);
        EmptyBrowseButton.Click += (_, _) => OpenBrowse();
        BrowseClose.Click += (_, _) => CloseBrowse();
        BrowseBack.Click += (_, _) => _ = ShowCategoriesAsync();
        BrowseLiveMode.Click += (_, _) => _ = ShowLiveAsync();
        BrowseCategoriesMode.Click += (_, _) => _ = ShowCategoriesAsync();

        _searchDebounce = _dispatcher.CreateTimer();
        _searchDebounce.Interval = TimeSpan.FromMilliseconds(400);
        _searchDebounce.IsRepeating = false;
        _searchDebounce.Tick += (_, _) => _ = SearchAsync(BrowseSearch.Text.Trim());
        BrowseSearch.TextChanged += (_, _) =>
        {
            _searchDebounce.Stop();
            if (BrowseSearch.Text.Trim().Length >= 2) _searchDebounce.Start();
            else if (_browseMode == BrowseMode.Search) _ = ShowLiveAsync();
        };
        BrowseSearch.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter && BrowseSearch.Text.Trim().Length > 0) { _searchDebounce.Stop(); _ = SearchAsync(BrowseSearch.Text.Trim()); e.Handled = true; }
            else if (e.Key == VirtualKey.Escape) { if (BrowseSearch.Text.Length > 0) BrowseSearch.Text = ""; else CloseBrowse(); e.Handled = true; }
        };

        BrowseGrid.ItemClick += (_, e) => OnBrowseItem(e.ClickedItem);
        BrowseGrid.SizeChanged += (_, _) => SizeBrowseCards();
        BrowseCategoryStrip.ItemClick += (_, e) => OnBrowseItem(e.ClickedItem);
    }

    void OnBrowseItem(object item)
    {
        switch (item)
        {
            case StreamCard s:
                CloseBrowse();
                OpenChannelInCurrentTab(s.Stream.Login);
                break;
            case CategoryCard c:
                _ = ShowCategoryAsync(c.Category);
                break;
        }
    }

    void OpenBrowse()
    {
        _browseOpen = true;
        BrowsePage.Visibility = Visibility.Visible;
        SidebarBrowseButton.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        SetVideoPopup(BrowsePage, true); // the page covers the video, so park the native video windows
        if (!_browseLoaded) { _browseLoaded = true; _ = ShowLiveAsync(); }
        else if (_browseMode == BrowseMode.Live && IsStale(_topStreams?.At)) _ = ShowLiveAsync();
        BrowseSearch.Focus(FocusState.Programmatic);
    }

    void CloseBrowse()
    {
        if (!_browseOpen) return;
        _browseOpen = false;
        BrowsePage.Visibility = Visibility.Collapsed;
        SidebarBrowseButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        SetVideoPopup(BrowsePage, false);
    }

    static bool IsStale(DateTime? at) => at == null || DateTime.UtcNow - at > TimeSpan.FromMinutes(2);

    async Task ShowLiveAsync()
    {
        SetBrowseMode(BrowseMode.Live, "Browse");
        int version = BeginBrowseLoad("StreamCardTemplate");
        if (IsStale(_topStreams?.At)) _topStreams = (DateTime.UtcNow, await TwitchBrowse.TopStreamsAsync());
        EndBrowseLoad(version, _topStreams!.Value.Items.Select(s => new StreamCard(s)), "Couldn't load live channels.");
    }

    async Task ShowCategoriesAsync()
    {
        SetBrowseMode(BrowseMode.Categories, "Browse");
        int version = BeginBrowseLoad("CategoryCardTemplate");
        if (IsStale(_topCategories?.At)) _topCategories = (DateTime.UtcNow, await TwitchBrowse.TopCategoriesAsync());
        EndBrowseLoad(version, _topCategories!.Value.Items.Select(c => new CategoryCard(c)), "Couldn't load categories.");
    }

    async Task ShowCategoryAsync(BrowseCategory category)
    {
        SetBrowseMode(BrowseMode.Category, category.DisplayName);
        int version = BeginBrowseLoad("StreamCardTemplate");
        var streams = await TwitchBrowse.CategoryStreamsAsync(category.Name);
        EndBrowseLoad(version, streams.Select(s => new StreamCard(s)), "Nobody's live in this category right now.");
    }

    async Task SearchAsync(string query)
    {
        if (query.Length == 0) return;
        SetBrowseMode(BrowseMode.Search, $"Results for “{query}”");
        int version = BeginBrowseLoad("StreamCardTemplate");
        var (channels, categories) = await TwitchBrowse.SearchAsync(query);
        if (version != _browseVersion) return;
        bool anyCategories = categories.Count > 0;
        BrowseCategoryStrip.ItemsSource = categories.Select(c => new CategoryCard(c)).ToList();
        BrowseCategoryStrip.Visibility = BrowseCategoriesHeader.Visibility = anyCategories ? Visibility.Visible : Visibility.Collapsed;
        BrowseChannelsHeader.Visibility = anyCategories && channels.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EndBrowseLoad(version, channels.Select(s => new StreamCard(s)), anyCategories ? "" : "No results.");
    }

    void SetBrowseMode(BrowseMode mode, string title)
    {
        _browseMode = mode;
        BrowseTitle.Text = title;
        BrowseBack.Visibility = mode == BrowseMode.Category ? Visibility.Visible : Visibility.Collapsed;
        BrowseLiveMode.IsChecked = mode == BrowseMode.Live;
        BrowseCategoriesMode.IsChecked = mode is BrowseMode.Categories or BrowseMode.Category;
        if (mode != BrowseMode.Search)
            BrowseCategoryStrip.Visibility = BrowseCategoriesHeader.Visibility = BrowseChannelsHeader.Visibility = Visibility.Collapsed;
    }

    // Stream cards stretch so each row fills the width; categories stay at their natural size.
    void SizeBrowseCards()
    {
        if (BrowseGrid.ItemsPanelRoot is not ItemsWrapGrid panel) return;
        if (!ReferenceEquals(BrowseGrid.ItemTemplate, Root.Resources["StreamCardTemplate"])) { panel.ItemWidth = double.NaN; return; }
        const double gap = 12, min = 270;
        double width = BrowseGrid.ActualWidth - 16; // leave room for the scrollbar
        int columns = Math.Max(1, (int)((width + gap) / (min + gap)));
        panel.ItemWidth = Math.Floor((width + gap) / columns);
    }

    int BeginBrowseLoad(string template)
    {
        BrowseGrid.ItemTemplate = (DataTemplate)Root.Resources[template];
        BrowseGrid.ItemsSource = null;
        BrowseEmpty.Visibility = Visibility.Collapsed;
        BrowseSpinner.IsActive = true;
        return ++_browseVersion;
    }

    void EndBrowseLoad(int version, IEnumerable<object> items, string emptyText)
    {
        if (version != _browseVersion) return; // a newer load started meanwhile
        var list = items.ToList();
        BrowseSpinner.IsActive = false;
        BrowseGrid.ItemsSource = list;
        BrowseGrid.UpdateLayout();
        SizeBrowseCards();
        BrowseEmpty.Text = emptyText;
        BrowseEmpty.Visibility = list.Count == 0 && emptyText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
