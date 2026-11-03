using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace Glint;

// One row in the multiview channel picker: a followed channel, or a Twitch search result.
public sealed class PickItem
{
    public PickItem(ChannelEntry e)
    {
        Login = e.Login;
        DisplayName = e.DisplayName;
        Avatar = e.Avatar;
        Subtitle = e.Stream is { } s ? s.Game ?? s.Title : "Offline";
        ViewersText = e.ViewersText;
        LiveVisibility = e.LiveVisibility;
        ItemOpacity = e.IsLive ? 1 : 0.6;
    }

    public PickItem(BrowseStream s)
    {
        Login = s.Login;
        DisplayName = s.DisplayName;
        if (s.Avatar != null) Avatar = new BitmapImage(new Uri(s.Avatar)) { DecodePixelWidth = 64 };
        Subtitle = s.Live ? s.Game ?? s.Title ?? "Live" : "Offline";
        ViewersText = s.Live ? ChannelEntry.FormatCount(s.Viewers) : "";
        LiveVisibility = s.Live ? Visibility.Visible : Visibility.Collapsed;
        ItemOpacity = s.Live ? 1 : 0.6;
    }

    public string Login { get; }
    public string DisplayName { get; }
    public ImageSource? Avatar { get; }
    public string Subtitle { get; }
    public string ViewersText { get; }
    public Visibility LiveVisibility { get; }
    public double ItemOpacity { get; }
    public override string ToString() => DisplayName;
}

// Multiview: picking channels to add, and making it obvious which chat you're typing in.
public sealed partial class MainWindow
{
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _pickSearch;
    List<PickItem> _pickResults = new();
    string _pickQuery = "";

    void WireMultiviewPicker()
    {
        _pickSearch = _dispatcher.CreateTimer();
        _pickSearch.Interval = TimeSpan.FromMilliseconds(350);
        _pickSearch.IsRepeating = false;
        _pickSearch.Tick += (_, _) => _ = SearchPickerAsync(MultiviewAddBox.Text.Trim());
        MultiviewAddFlyout.Opening += (_, _) => { MultiviewAddBox.Text = ""; FillPicker(); };
        MultiviewAddBox.TextChanged += (_, _) =>
        {
            FillPicker();
            _pickSearch.Stop();
            if (MultiviewAddBox.Text.Trim().Length >= 2) _pickSearch.Start();
        };
        MultiviewAddBox.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            // Enter takes the top match, or the name exactly as typed if nothing matched yet.
            var login = (MultiviewPickList.ItemsSource as List<PickItem>)?.FirstOrDefault()?.Login ?? NormalizeLogin(MultiviewAddBox.Text);
            if (login != null) AddPicked(login);
        };
        MultiviewPickList.ItemClick += (_, e) => { if (e.ClickedItem is PickItem item) AddPicked(item.Login); };
    }

    void FillPicker()
    {
        var query = MultiviewAddBox.Text.Trim();
        var inMultiview = _multiviewSlots.Select(s => s.Tab?.Login).OfType<string>().ToHashSet();
        bool Matches(string text) => query.Length == 0 || text.Contains(query, StringComparison.OrdinalIgnoreCase);
        var items = _entries.Values
            .Where(e => (e.IsFollowed || e.IsFavorite) && e.IsLive && !inMultiview.Contains(e.Login) && (Matches(e.Login) || Matches(e.DisplayName)))
            .OrderByDescending(e => e.Stream!.Viewers)
            .Select(e => new PickItem(e))
            .ToList();
        bool searched = query.Length >= 2 && _pickQuery == query;
        if (searched)
            items.AddRange(_pickResults.Where(r => !inMultiview.Contains(r.Login) && items.All(i => i.Login != r.Login)));
        MultiviewPickList.ItemsSource = items;
        MultiviewPickHeader.Text = query.Length == 0 ? "LIVE CHANNELS YOU FOLLOW" : "CHANNELS";
        MultiviewPickList.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MultiviewPickEmpty.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        MultiviewPickEmpty.Text = query.Length == 0 ? "None of the channels you follow are live. Search above to add anyone."
            : searched || query.Length < 2 ? "No channels found. Press Enter to add it anyway."
            : "Searching Twitch…";
    }

    async Task SearchPickerAsync(string query)
    {
        if (query.Length < 2) return;
        List<BrowseStream> channels;
        try { channels = (await TwitchBrowse.SearchAsync(query)).Channels; }
        catch (Exception ex) { Log.Error("Multiview search", ex); channels = new(); }
        if (MultiviewAddBox.Text.Trim() != query) return; // typed on since
        _pickQuery = query;
        _pickResults = channels.Select(c => new PickItem(c)).ToList();
        FillPicker();
    }

    void AddPicked(string login)
    {
        MultiviewAddFlyout.Hide();
        OpenChannel(login, true);
    }

    void WireMultiviewRecipient()
    {
        MultiviewRecipientButton.Click += (_, _) =>
        {
            var menu = new MenuFlyout();
            foreach (var tab in _multiviewSlots.Select(s => s.Tab).OfType<ChannelTab>().Distinct())
            {
                var item = new RadioMenuFlyoutItem { Text = tab.Entry.DisplayName, GroupName = "Recipient", IsChecked = tab == _multiviewRecipient };
                var target = tab;
                item.Click += (_, _) => { SetChatRecipient(target); ChatInput.Focus(FocusState.Programmatic); };
                menu.Items.Add(item);
            }
            TrackVideoPopup(menu);
            menu.ShowAt(MultiviewRecipientButton);
        };
    }

    // The message box sits under the chat you're writing in, so it's always clear where a message goes.
    // When that stream has no chat pane showing, it sits in the bar below beside a button naming the channel.
    void PlaceComposer()
    {
        Panel destination = ChatInputHost;
        if (MultiviewActive)
        {
            destination = MultiviewComposerInput;
            if (_multiviewRecipient is { } tab && _multiviewChats.TryGetValue(tab, out var pane)
                && MultiviewChatDock.Children.Contains(pane.Root) && pane.View.Channel != null)
                destination = pane.ComposerHost;
        }
        if (!destination.Children.Contains(Composer))
        {
            // Take it out of wherever it is first (a chat pane may already be off screen).
            foreach (var host in _multiviewChats.Values.Select(p => (Panel)p.ComposerHost).Append(ChatInputHost).Append(MultiviewComposerInput))
                host.Children.Remove(Composer);
            (Composer.Parent as Panel)?.Children.Remove(Composer);
            destination.Children.Add(Composer);
        }
        MultiviewComposerBar.Visibility = MultiviewActive && destination == MultiviewComposerInput && !SoloFullscreen ? Visibility.Visible : Visibility.Collapsed;

        var accent = (Brush)Application.Current.Resources["GlintAccentBrush"];
        var none = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var pane in _multiviewChats.Values) pane.Root.BorderBrush = pane.Tab == _multiviewRecipient ? accent : none;
        MultiviewRecipientName.Text = _multiviewRecipient?.Entry.DisplayName ?? "Choose a chat";
        MultiviewRecipientAvatar.ImageSource = _multiviewRecipient?.Entry.Avatar;
    }
}
