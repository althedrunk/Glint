using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Glint;

public sealed partial class SettingsWindow
{
    readonly List<(string Section, string Search, FrameworkElement Element)> _rows = new();
    readonly StackPanel _settingsContent = new() { Spacing = 8 };
    readonly ListView _categories = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true };
    readonly TextBox _search = new() { PlaceholderText = "Search settings" };
    readonly TextBlock _sectionTitle = new() { FontSize = 22, FontWeight = FontWeights.SemiBold };
    readonly TextBlock _feedback = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    readonly Button _reset = new() { Content = "Reset this section" };
    ScrollViewer _settingsScroll = null!;
    bool _building, _confirmingReset;

    void BuildShell()
    {
        var page = new Grid { RequestedTheme = ElementTheme.Dark, Padding = new Thickness(24), RowSpacing = 16,
            Background = (Brush)Application.Current.Resources["GlintBackgroundBrush"] };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition());
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Spacing = 4 };
        heading.Children.Add(new TextBlock { Text = "Settings", FontSize = 26, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(Note("Changes save automatically. Adjust a setting and see it in Glint as you go."));
        page.Children.Add(heading);
        AutomationProperties.SetName(_search, "Search settings");
        Grid.SetRow(_search, 1); page.Children.Add(_search);
        var body = new Grid { ColumnSpacing = 24 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(155) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        _categories.ItemsSource = new[] { "Account", "Appearance", "Chat", "Filtering", "Player", "Multiview", "Notifications", "Shortcuts", "Profiles", "General", "About" };
        AutomationProperties.SetName(_categories, "Settings categories");
        body.Children.Add(_categories);
        var content = new Grid { RowSpacing = 14 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition());
        content.Children.Add(_sectionTitle);
        _settingsScroll = new ScrollViewer { Content = _settingsContent, Padding = new Thickness(0, 0, 12, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetName(_settingsScroll, "Settings options");
        Grid.SetRow(_settingsScroll, 1); content.Children.Add(_settingsScroll);
        Grid.SetColumn(content, 1); body.Children.Add(content);
        Grid.SetRow(body, 2); page.Children.Add(body);
        var footer = new Grid { ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _feedback.VerticalAlignment = VerticalAlignment.Center;
        _feedback.Foreground = Subtle;
        _feedback.Text = "Saved automatically";
        footer.Children.Add(_feedback);
        Grid.SetColumn(_reset, 1); footer.Children.Add(_reset);
        var close = new Button { Content = "Close", MinWidth = 90, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        AutomationProperties.SetName(close, "Close settings");
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 2); footer.Children.Add(close);
        Grid.SetRow(footer, 3); page.Children.Add(footer);
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) => { if (!_confirmingReset) { Close(); e.Handled = true; } };
        page.KeyboardAccelerators.Add(escape);
        Content = page;
        page.Loaded += (_, _) => Appearance.RefreshTheme(page);
        BuildRows();
        _categories.SelectionChanged += (_, _) => { _search.Text = ""; RenderRows(); _settingsScroll.ChangeView(null, 0, null); };
        _categories.ItemClick += (_, e) => { _categories.SelectedItem = e.ClickedItem; _search.Text = ""; RenderRows(); _settingsScroll.ChangeView(null, 0, null); };
        _search.TextChanged += (_, _) => { if (!_building) { RenderRows(); _settingsScroll.ChangeView(null, 0, null); } };
        _reset.Click += async (_, _) =>
        {
            if (_categories.SelectedItem is not string section || !PreferenceCatalog.Sections.ContainsKey(section)) return;
            await RestoreDefaultsAsync(section);
        };
        _categories.SelectedItem = "Account";
    }

    async Task RestoreDefaultsAsync(string? section = null)
    {
        if (_confirmingReset || _closed || Content is not FrameworkElement { XamlRoot: { } xamlRoot }) return;
        _confirmingReset = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                RequestedTheme = ElementTheme.Dark,
                Title = section == null ? "Restore all defaults?" : $"Reset {section.ToLowerInvariant()}?",
                Content = section == null
                    ? "Restore appearance, chat, filters, player defaults, multiview, notifications and shortcuts to their original settings? Ignored words, ignored users and highlight lists will be cleared.\n\nYour Twitch login, open streams, current audio, favourites and saved profiles and layouts will be kept."
                    : $"Restore the settings in {section} to their defaults? Other categories, your account and saved profiles will be kept.",
                PrimaryButtonText = "Restore defaults",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _closed) return;
            bool ads = S.BlockAds;
            if (section == null) PreferenceCatalog.ResetAll(S);
            else PreferenceCatalog.Reset(section, S);
            _window.ApplyPreferences(ads != S.BlockAds);
            BuildRows(); RenderRows();
            _feedback.Text = section == null ? "All customisation settings restored to defaults" : $"{section} reset to defaults";
        }
        finally { _confirmingReset = false; }
    }

    void AddOption(string section, string keywords, FrameworkElement element) =>
        _rows.Add((section, section + " " + keywords + " " + SearchText(element), element));

    static string SearchText(UIElement element)
    {
        string name = AutomationProperties.GetName(element);
        return name + " " + (element switch
        {
            TextBlock text => text.Text,
            Panel panel => string.Join(" ", panel.Children.Select(SearchText)),
            Border { Child: { } child } => SearchText(child),
            ContentControl { Content: UIElement child } => SearchText(child),
            ContentControl { Content: string text } => text,
            _ => "",
        });
    }

    void RenderRows()
    {
        _settingsContent.Children.Clear();
        string section = _categories.SelectedItem as string ?? "Account";
        string query = _search.Text.Trim();
        _sectionTitle.Text = query.Length == 0 ? section : "Search results";
        _reset.Visibility = query.Length == 0 && PreferenceCatalog.Sections.ContainsKey(section) ? Visibility.Visible : Visibility.Collapsed;
        string? previous = null;
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var row in _rows.Where(r => query.Length == 0 ? r.Section == section : terms.All(term => r.Search.Contains(term, StringComparison.OrdinalIgnoreCase))))
        {
            if (query.Length > 0 && previous != row.Section) _settingsContent.Children.Add(Heading(row.Section, previous == null));
            _settingsContent.Children.Add(row.Element);
            previous = row.Section;
        }
        if (_settingsContent.Children.Count == 0) _settingsContent.Children.Add(Note("No settings match. Try another word."));
    }

    Border Choice(string title, string? description, IEnumerable<string> choices, string value, Action<string> set)
    {
        var box = new ComboBox { ItemsSource = choices.ToList(), SelectedItem = value, Width = 205, MaxDropDownHeight = 320 };
        AutomationProperties.SetName(box, title);
        box.SelectionChanged += (_, _) => { if (!_building && box.SelectedItem is string selected) { set(selected); Settings.Save(); } };
        return Row(title, description, box);
    }

    Border Number(string title, string? description, double min, double max, double step, double value, Action<double> set, string suffix = "")
    {
        var panel = new StackPanel { Width = 205 };
        var label = new TextBlock { Text = value.ToString("0.##") + suffix, HorizontalAlignment = HorizontalAlignment.Right, FontSize = 12, Foreground = Subtle };
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, Value = Math.Clamp(value, min, max) };
        AutomationProperties.SetName(slider, title);
        slider.ValueChanged += (_, e) =>
        {
            label.Text = e.NewValue.ToString("0.##") + suffix;
            if (_building) return;
            set(e.NewValue); Settings.Save();
        };
        panel.Children.Add(label); panel.Children.Add(slider);
        return Row(title, description, panel);
    }

    Border ListEditor(string title, string description, List<string> values, Action<List<string>> set, string placeholder)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(Note(description));
        var entries = new StackPanel { Spacing = 4 };
        var input = new TextBox { PlaceholderText = placeholder, MaxLength = 300 };
        AutomationProperties.SetName(input, title);
        var add = new Button { Content = "Add" };
        AutomationProperties.SetName(add, "Add " + title.ToLowerInvariant());
        var list = values.ToList();
        void Refresh()
        {
            entries.Children.Clear();
            foreach (string word in list.ToList())
            {
                var line = new Grid { ColumnSpacing = 8 };
                line.ColumnDefinitions.Add(new ColumnDefinition());
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.Children.Add(new TextBlock { Text = word, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
                var remove = new Button { Content = new SymbolIcon(Symbol.Cancel), Padding = new Thickness(6), MinHeight = 28, MinWidth = 28 };
                AutomationProperties.SetName(remove, $"Remove {word} from {title.ToLowerInvariant()}");
                remove.Click += (_, _) => { list.Remove(word); set(list.ToList()); Settings.Save(); Refresh(); };
                Grid.SetColumn(remove, 1); line.Children.Add(remove); entries.Children.Add(line);
            }
        }
        void Add()
        {
            var words = Chat.ChatFilters.ParseWords(input.Text);
            foreach (var word in words)
                if (!list.Contains(word, StringComparer.OrdinalIgnoreCase)) list.Add(word);
            set(list.ToList()); Settings.Save(); input.Text = ""; Refresh();
        }
        add.Click += (_, _) => Add();
        input.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { Add(); e.Handled = true; } };
        var editor = new Grid { ColumnSpacing = 8 };
        editor.ColumnDefinitions.Add(new ColumnDefinition()); editor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        editor.Children.Add(input); Grid.SetColumn(add, 1); editor.Children.Add(add);
        panel.Children.Add(editor);
        panel.Children.Add(new ScrollViewer { Content = entries, MaxHeight = 220, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Refresh();
        return Card(panel);
    }

    void BuildRows()
    {
        _building = true;
        _settingsContent.Children.Clear();
        _rows.Clear();
        BuildAccount();
        AddOption("Account", "Twitch login sign in clips watch time", _account);
        BuildAppearancePage(); BuildChatPage(); BuildFilteringPage(); BuildPlayerPage();
        BuildMultiviewPage(); BuildNotificationsPage(); BuildShortcutsPage();
        AddOption("Profiles", "save restore named preferences setup", ProfilesCard(false));
        var restore = new Button { Content = "Restore all defaults" };
        AutomationProperties.SetName(restore, "Restore all defaults");
        restore.Click += async (_, _) => await RestoreDefaultsAsync();
        AddOption("General", "restore reset all defaults original settings", Row("Restore defaults",
            "Reset all customisation settings. Keeps your login, open streams, current audio and saved setups. Asks for confirmation.", restore));
        AddOption("About", "version mpv emotes", Card(Note($"Glint {typeof(App).Assembly.GetName().Version?.ToString(3)}. Video by mpv, emotes from 7TV, BTTV and FFZ.")));
        _building = false;
    }

    void BuildAppearancePage()
    {
        void Add(string text, FrameworkElement row) => AddOption("Appearance", text, row);
        Add("accent colour color purple blue teal pink orange", Choice("Accent colour", null, Appearance.Accents, S.AccentColor, v => { S.AccentColor = v; _window.ApplyAppearance(); _window.OnChatStyleChanged(); }));
        Add("background dark grey black", Choice("Background", null, ["Dark", "Grey", "Black"], S.BackgroundStyle, v => { S.BackgroundStyle = v; _window.ApplyAppearance(); }));
        Add("sidebar spacing density compact comfortable", Choice("Sidebar spacing", null, ["Comfortable", "Compact"], S.InterfaceDensity, v => { S.InterfaceDensity = v; _window.ApplyAppearance(); }));
        Add("sidebar width", Number("Sidebar width", "Expanded channel list", 200, 400, 4, S.SidebarWidth, v => { S.SidebarWidth = v; _window.ApplyAppearance(); }, " px"));
        Add("sidebar avatar size", Number("Avatar size", "In the expanded channel list", 24, 48, 2, S.SidebarAvatarSize, v => { S.SidebarAvatarSize = v; _window.ApplyAppearance(); }, " px"));
    }

    void BuildChatPage()
    {
        void Add(string text, FrameworkElement row) => AddOption("Chat", text, row);
        Add("font typeface", FontRow());
        Add("text font size", Number("Text size", null, 11, 24, 0.5, S.ChatFontSize, v => { S.ChatFontSize = v; _window.OnChatStyleChanged(); }, " px"));
        Add("line spacing", Number("Line spacing", "Space between lines within a message", 1, 1.8, 0.05, S.ChatLineSpacing, v => { S.ChatLineSpacing = v; _window.OnChatStyleChanged(); }, "x"));
        Add("message spacing", Number("Message spacing", "Padding above and below each message", 0, 12, 1, S.ChatMessageSpacing, v => { S.ChatMessageSpacing = v; _window.OnChatStyleChanged(); }, " px"));
        Add("alternating rows background", ToggleRow("Alternating backgrounds", "A subtle background on every other message", S.AlternateChatRows, v => { S.AlternateChatRows = v; _window.OnChatStyleChanged(); }));
        Add("emote size scale", Number("Emote size", null, 0.75, 2, 0.05, S.EmoteScale, v => { S.EmoteScale = v; _window.OnChatStyleChanged(); }, "x"));
        Add("colon emote autocomplete suggestions", ToggleRow("Emote suggestions", "Type : and at least two characters to find emotes while writing", S.EmoteSuggestions, v => { S.EmoteSuggestions = v; _window.OnChatPreferencesChanged(); }));
        Add("animated emotes animation hover still", Choice("Animate emotes", null, ["Always", "On hover", "Still"], S.EmoteAnimation, v => { S.EmoteAnimation = v; _window.OnChatStyleChanged(); }));
        Add("timestamps time", ToggleRow("Timestamps", null, S.ShowTimestamps, v => { S.ShowTimestamps = v; _window.OnChatStyleChanged(); }));
        Add("timestamp format seconds 12 24 hour", Choice("Time format", null, ["HH:mm", "HH:mm:ss", "h:mm tt", "h:mm:ss tt"], S.TimestampFormat, v => { S.TimestampFormat = v; _window.OnChatStyleChanged(); }));
        Add("pause chat on hover scrolling", ToggleRow("Pause chat on hover", "Resumes when you move away, unless you scroll up", S.PauseChatOnHover, v => { S.PauseChatOnHover = v; _window.OnChatPreferencesChanged(); }));
        Add("Twitch badges subscriber moderator", ToggleRow("Twitch badges", null, S.ShowBadges, v => { S.ShowBadges = v; _window.OnChatStyleChanged(); }));
        Add("7TV name paints", ToggleRow("7TV name paints", "Gradient and textured usernames", S.ShowSevenTvPaints, v => { S.ShowSevenTvPaints = v; _window.OnChatStyleChanged(); }));
        Add("7TV badges", ToggleRow("7TV badges", null, S.ShowSevenTvBadges, v => { S.ShowSevenTvBadges = v; _window.OnChatStyleChanged(); }));
    }

    Border FontRow()
    {
        var fonts = ChatFonts.Value;
        var family = new ComboBox { ItemsSource = fonts, SelectedItem = S.ChatFontFamily, Width = 205, MaxDropDownHeight = 320, IsTextSearchEnabled = true };
        family.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <StackPanel Spacing="2" ToolTipService.ToolTip="{Binding}">
                    <TextBlock Text="{Binding}" FontFamily="{Binding}" FontSize="16" TextTrimming="CharacterEllipsis" />
                    <TextBlock Text="Hello chat! Aa Bb 0123" FontFamily="{Binding}" FontSize="13" Opacity="0.7" />
                </StackPanel>
            </DataTemplate>
            """);
        AutomationProperties.SetName(family, "Chat font");
        family.SelectionChanged += (_, _) => { if (!_building && family.SelectedItem is string name) { S.ChatFontFamily = name; Settings.Save(); _window.OnChatStyleChanged(); } };
        return Row("Font", "Installed text fonts", family);
    }

    void BuildFilteringPage()
    {
        void Add(string text, FrameworkElement row) => AddOption("Filtering", text, row);
        Add("ignored words phrases", ListEditor("Ignored words", "Hide whole words or phrases, ignoring case. Add one or separate several with commas.", S.IgnoredWords,
            v => { S.IgnoredWords = v; _window.OnChatPreferencesChanged(); }, "Word or phrase"));
        Add("ignored users usernames", ListEditor("Ignored users", "Hide messages from these usernames in chat, threads and history.", S.IgnoredUsers,
            v => { S.IgnoredUsers = v; _window.OnChatPreferencesChanged(); }, "Username"));
        Add("announcements notices", ToggleRow("Announcements", "Show moderator announcements", S.ShowAnnouncements, v => { S.ShowAnnouncements = v; _window.OnChatPreferencesChanged(); }));
        Add("subscription gift notices", ToggleRow("Subscription notices", "Show subscriptions, resubscriptions and gift notices", S.ShowSubscriptionNotices, v => { S.ShowSubscriptionNotices = v; _window.OnChatPreferencesChanged(); }));
    }

    void BuildPlayerPage()
    {
        void Add(string text, FrameworkElement row) => AddOption("Player", text, row);
        Add("default quality resolution", Choice("Default quality", "Used when opening or reloading a stream", ["Source", "1080p60", "1080p", "720p60", "720p", "480p", "360p", "160p", "Audio only"], S.PreferredQuality, v => S.PreferredQuality = v));
        Add("default new tab volume last", ToggleRow("Use last volume for new tabs", "Existing tabs keep their own audio settings", S.UseLastVolumeForNewTabs, v => S.UseLastVolumeForNewTabs = v));
        Add("default new tab volume", Number("New tab volume", "Used when the option above is off", 0, 100, 1, S.NewTabVolume, v => S.NewTabVolume = v, "%"));
        Add("seek distance skip seconds", Number("Seek distance", "Player skip buttons and seek shortcuts", 5, 60, 5, S.SeekSeconds, v => { S.SeekSeconds = v; _window.ApplyPlayerPreferences(); }, " s"));
        Add("long seek distance shift arrows", Number("Long seek distance", "Long seek shortcuts; Shift + arrows by default", 15, 300, 15, S.LongSeekSeconds, v => S.LongSeekSeconds = v, " s"));
        Add("controls hide delay overlay", Number("Hide controls after", "While playing and the pointer is away from controls", 1, 10, 0.5, S.ControlsHideSeconds, v => { S.ControlsHideSeconds = v; _window.ApplyPlayerPreferences(); }, " s"));
        Add("low latency live delay", ToggleRow("Low latency", "Applies to streams opened afterwards", S.LowLatency, v => S.LowLatency = v));
        Add("skip ads", ToggleRow("Skip ads", "Tries another stream during detected ad breaks, otherwise waits for the broadcast to return. Some commercial-break screens may still get through. Reloads open live streams.",
            S.BlockAds, v => { S.BlockAds = v; _window.OnAdSkippingChanged(); }));
    }

    void BuildMultiviewPage()
    {
        void Add(string text, FrameworkElement row) => AddOption("Multiview", text, row);
        Add("stream count two three four", Choice("Number of streams", null, ["2", "3", "4"], S.MultiviewCount.ToString(), v => { S.MultiviewCount = int.Parse(v); _window.ApplyMultiviewPreferences(); }));
        Add("layout grid main stream", Choice("Video layout", null, ["Grid", "Main stream"], S.MultiviewLayout, v => { S.MultiviewLayout = v; _window.ApplyMultiviewPreferences(); }));
        Add("chat each following pinned hidden", Choice("Chats", null, ["Each stream", "Following chat", "Pinned chats", "Hidden"], S.MultiviewChats, v => { S.MultiviewChats = v; _window.ApplyMultiviewPreferences(); }));
        Add("chat placement below right auto", Choice("Chat placement", null, ["Below", "Right", "Auto"], S.MultiviewDock, v => { S.MultiviewDock = v; _window.ApplyMultiviewPreferences(); }));
        Add("chat width", Number("Chat width", "When placed beside video", 260, 600, 10, S.MultiviewChatWidth, v => { S.MultiviewChatWidth = v; _window.ApplyMultiviewPreferences(); }, " px"));
        Add("chat height", Number("Chat height", "When placed below video", 140, 700, 10, S.MultiviewChatHeight, v => { S.MultiviewChatHeight = v; _window.ApplyMultiviewPreferences(); }, " px"));
        Add("saved layout preset restore", ProfilesCard(true));
    }

    void BuildNotificationsPage()
    {
        void Add(string text, FrameworkElement row) => AddOption("Notifications", text, row);
        Add("highlight words", ListEditor("Highlight words", "Mark matching messages and collect them in Mentions.", S.HighlightWords, v => S.HighlightWords = v, "Word or phrase"));
        Add("highlight users", ListEditor("Highlight users", "Collect messages from these people in Mentions.", S.HighlightUsers, v => S.HighlightUsers = v, "Username"));
        Add("flash taskbar mentions", ToggleRow("Flash the taskbar", "For mentions while Glint is in the background", S.FlashOnMention, v => S.FlashOnMention = v));
        Add("sound mentions", ToggleRow("Mention sound", null, S.SoundOnMention, v => S.SoundOnMention = v));
        Add("sound highlighted messages", ToggleRow("Highlight sound", "For highlighted words and users", S.SoundOnHighlight, v => S.SoundOnHighlight = v));
        Add("alerts focused watching", ToggleRow("Alert for the chat you're viewing", "Also play sounds for messages in the chat you're looking at", S.NotifyWhenFocused, v => S.NotifyWhenFocused = v));
        Add("mention tone sound preview", ToneRow("Mention tone", S.MentionTone, v => S.MentionTone = v));
        Add("highlight tone sound preview", ToneRow("Highlight tone", S.HighlightTone, v => S.HighlightTone = v));
        Add("notification sound volume", Number("Alert volume", null, 0, 100, 1, S.NotificationVolume, v => S.NotificationVolume = v, "%"));
    }

    Border ToneRow(string title, string tone, Action<string> set)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var box = new ComboBox { ItemsSource = NotificationSound.Tones, SelectedItem = tone, Width = 125 };
        AutomationProperties.SetName(box, title);
        box.SelectionChanged += (_, _) => { if (!_building && box.SelectedItem is string selected) { set(selected); Settings.Save(); } };
        var play = new Button { Content = new SymbolIcon(Symbol.Play), Padding = new Thickness(9) };
        AutomationProperties.SetName(play, "Preview " + title.ToLowerInvariant());
        play.Click += (_, _) => { if (box.SelectedItem is string selected) NotificationSound.Play(selected, preview: true); };
        panel.Children.Add(box); panel.Children.Add(play);
        return Row(title, null, panel);
    }

    Border ProfilesCard(bool multiview)
    {
        var profiles = multiview ? S.MultiviewPresets : S.Profiles;
        string title = multiview ? "Saved layouts" : "Preference profiles";
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(Note(multiview ? "Save the layout, chat placement and proportions. Open streams and their audio stay as they are." :
            "Save appearance, chat, filtering, player, multiview, alerts and shortcuts. Your account and open streams stay as they are."));
        var name = new TextBox { PlaceholderText = multiview ? "e.g. Two streams, chats below" : "e.g. Watching", MaxLength = 60 };
        AutomationProperties.SetName(name, title + " name");
        panel.Children.Add(name);
        var save = new Button { Content = "Save current", HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(save, "Save " + title.ToLowerInvariant());
        panel.Children.Add(save);
        var choices = new ComboBox { ItemsSource = profiles.ToList(), HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Choose a saved setup" };
        AutomationProperties.SetName(choices, title);
        panel.Children.Add(choices);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var apply = new Button { Content = "Apply", IsEnabled = false };
        var delete = new Button { Content = "Remove", IsEnabled = false };
        AutomationProperties.SetName(apply, "Apply " + title.ToLowerInvariant());
        AutomationProperties.SetName(delete, "Remove " + title.ToLowerInvariant());
        actions.Children.Add(apply); actions.Children.Add(delete); panel.Children.Add(actions);
        choices.SelectionChanged += (_, _) => { apply.IsEnabled = delete.IsEnabled = choices.SelectedItem is PreferenceProfile; };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { _feedback.Text = "Give this setup a name first"; return; }
            var profile = PreferenceCatalog.Capture(name.Text, S, multiview ? "Multiview" : null);
            profiles.RemoveAll(p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
            profiles.Add(profile);
            choices.ItemsSource = profiles.ToList(); choices.SelectedItem = profile;
            Settings.Save(); _feedback.Text = $"Saved {profile.Name}";
        };
        apply.Click += (_, _) =>
        {
            if (choices.SelectedItem is not PreferenceProfile profile) return;
            bool ads = S.BlockAds;
            PreferenceCatalog.Apply(profile, S, multiview ? "Multiview" : null);
            _window.ApplyPreferences(ads != S.BlockAds);
            BuildRows(); RenderRows();
            _feedback.Text = $"Applied {profile.Name}";
        };
        delete.Click += (_, _) =>
        {
            if (choices.SelectedItem is not PreferenceProfile profile) return;
            profiles.Remove(profile); choices.ItemsSource = profiles.ToList(); Settings.Save();
            _feedback.Text = $"Removed {profile.Name}";
        };
        return Card(panel);
    }

    void BuildShortcutsPage()
    {
        AddOption("Shortcuts", "keyboard remap keys", Note("Select a shortcut and press your keys. A custom binding replaces its defaults. Escape cancels; navigation and text-editing keys stay reserved."));
        foreach (var definition in ShortcutMap.Definitions)
        {
            var keys = new TextBox { Text = string.Join(" / ", ShortcutMap.Bindings(definition, S)), IsReadOnly = true, Width = 200, PlaceholderText = "Unassigned" };
            AutomationProperties.SetName(keys, definition.Label + " shortcut");
            ToolTipService.SetToolTip(keys, "Press your keys to replace. Right-click to restore defaults or disable.");
            var menu = new MenuFlyout();
            var restore = new MenuFlyoutItem { Text = "Restore default shortcut" };
            var disable = new MenuFlyoutItem { Text = "Disable shortcut" };
            restore.Click += (_, _) =>
            {
                string? conflict = definition.Defaults.Select(chord => ShortcutMap.Validate(definition.Id, chord, S)).FirstOrDefault(error => error != null);
                if (conflict != null) { _feedback.Text = conflict; return; }
                S.ShortcutOverrides.Remove(definition.Id); keys.Text = string.Join(" / ", definition.Defaults);
                _window.RebuildShortcuts(); Settings.Save(); _feedback.Text = "Default shortcut restored";
            };
            disable.Click += (_, _) =>
            {
                S.ShortcutOverrides[definition.Id] = ""; keys.Text = "";
                _window.RebuildShortcuts(); Settings.Save(); _feedback.Text = "Shortcut disabled";
            };
            menu.Items.Add(restore); menu.Items.Add(disable); keys.ContextFlyout = menu;
            keys.PreviewKeyDown += (_, e) =>
            {
                if (e.Key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows) return;
                if (e.Key == VirtualKey.Escape) { _categories.Focus(FocusState.Programmatic); e.Handled = true; return; }
                string chord = ShortcutMap.Format(e.Key, MainWindow.CurrentModifiers());
                string? error = ShortcutMap.Validate(definition.Id, chord, S);
                if (error != null) _feedback.Text = error;
                else { S.ShortcutOverrides[definition.Id] = chord; keys.Text = chord; _window.RebuildShortcuts(); Settings.Save(); _feedback.Text = "Shortcut saved"; }
                e.Handled = true;
            };
            AddOption("Shortcuts", definition.Label, Row(definition.Label, string.Join(" / ", definition.Defaults), keys));
        }
    }
}
