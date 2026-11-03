using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Glint;

// Channel-point predictions and polls for the stream you're watching, as a card over the top of chat.
// You can predict and vote from it (needs the twitch.tv session).
public sealed partial class MainWindow
{
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _engageTimer, _engageClock;
    Engagement? _engage;
    ChannelTab? _engageTab;
    TextBlock? _engageTime;
    string? _engageCollapsedId, _engageError;
    bool _engageBusy, _engageFlyoutOpen;
    readonly Dictionary<string, string> _myPollVotes = new(); // Twitch doesn't tell us our own vote

    static readonly Color PredictionBlue = Color.FromArgb(255, 56, 122, 255), PredictionPink = Color.FromArgb(255, 245, 0, 155);

    void WireEngagement()
    {
        _engageTimer = Timer(TimeSpan.FromSeconds(6), () => _ = RefreshEngagementAsync());
        _engageTimer.Start();
        _engageClock = Timer(TimeSpan.FromSeconds(1), UpdateEngageClock);
        _engageClock.Start();
    }

    async Task RefreshEngagementAsync()
    {
        var tab = _active;
        if (tab == null || tab.Vod != null || !tab.Entry.IsLive || !TwitchSession.IsConnected || MultiviewActive || !ChatShown)
        {
            HideEngagement();
            return;
        }
        if (_engageBusy) return;
        _engageBusy = true;
        try
        {
            var engagement = await TwitchGql.GetEngagementAsync(tab.Login);
            if (_active != tab || _closing) return;
            _engage = engagement;
            _engageTab = tab;
            RenderEngagement();
        }
        catch (Exception ex) { Log.Error("Predictions", ex); }
        finally { _engageBusy = false; }
    }

    void HideEngagement()
    {
        _engage = null;
        _engageTab = null;
        EngageCard.Visibility = Visibility.Collapsed;
    }

    void RenderEngagement()
    {
        if (_engageFlyoutOpen) return; // don't pull the points box out from under the user
        var prediction = _engage?.Prediction is { Status: "ACTIVE" or "LOCKED" } p ? p : null;
        var poll = prediction == null && _engage?.Poll is { Status: "ACTIVE" or "COMPLETED" } q ? q : null;
        if (prediction == null && poll == null) { EngageCard.Visibility = Visibility.Collapsed; return; }

        var id = prediction?.Id ?? poll!.Id;
        bool collapsed = _engageCollapsedId == id;
        EngageContent.Children.Clear();
        EngageContent.Children.Add(EngageHeader(prediction != null ? "" : "", prediction != null ? "Prediction" : "Poll", id, collapsed));
        EngageContent.Children.Add(new TextBlock
        {
            Text = prediction?.Title ?? poll!.Title, FontWeight = FontWeights.SemiBold, FontSize = 14,
            TextWrapping = collapsed ? TextWrapping.NoWrap : TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!collapsed)
        {
            if (prediction != null) AddPrediction(prediction);
            else AddPoll(poll!);
            if (_engageError != null)
                EngageContent.Children.Add(new TextBlock { Text = _engageError, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 120, 120)) });
        }
        EngageCard.Visibility = Visibility.Visible;
        UpdateEngageClock();
    }

    Grid EngageHeader(string glyph, string label, string id, bool collapsed)
    {
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var subtle = (Brush)Application.Current.Resources["GlintSubtleTextBrush"];
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = (Brush)Application.Current.Resources["GlintAccentBrush"] });
        title.Children.Add(new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = subtle });
        _engageTime = new TextBlock { FontSize = 12.5, Foreground = subtle, VerticalAlignment = VerticalAlignment.Center };
        var toggle = new Button
        {
            Style = (Style)Root.Resources["IconButton"], Width = 28, Height = 24, FontSize = 11,
            Content = collapsed ? "" : "",
        };
        ToolTipService.SetToolTip(toggle, collapsed ? "Show" : "Hide");
        toggle.Click += (_, _) => { _engageCollapsedId = collapsed ? null : id; RenderEngagement(); };
        Grid.SetColumn(_engageTime, 2);
        Grid.SetColumn(toggle, 3);
        header.Children.Add(title);
        header.Children.Add(_engageTime);
        header.Children.Add(toggle);
        return header;
    }

    void AddPrediction(Prediction prediction)
    {
        long total = Math.Max(1, prediction.Outcomes.Sum(o => o.Points));
        bool open = prediction.Status == "ACTIVE" && prediction.MyOutcomeId == null;
        foreach (var outcome in prediction.Outcomes)
        {
            var color = outcome.Color == "PINK" ? PredictionPink : outcome.Color == "BLUE" ? PredictionBlue : Appearance.Accent;
            double share = (double)outcome.Points / total;
            string ratio = outcome.Points > 0 ? $"1:{(double)total / outcome.Points:0.00}" : "1:1";
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel { Spacing = 3 };
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = (outcome.Id == prediction.MyOutcomeId ? "✓ " : "") + outcome.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            var percent = new TextBlock { Text = $"{share:0%}", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(color) };
            Grid.SetColumn(percent, 1);
            line.Children.Add(name);
            line.Children.Add(percent);
            info.Children.Add(line);
            info.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = share, Foreground = new SolidColorBrush(color), Height = 4 });
            info.Children.Add(new TextBlock
            {
                Text = $"{ChannelEntry.FormatCount((int)Math.Min(int.MaxValue, outcome.Points))} points  ·  {outcome.Users} {(outcome.Users == 1 ? "person" : "people")}  ·  {ratio}",
                FontSize = 11.5, Foreground = (Brush)Application.Current.Resources["GlintSubtleTextBrush"],
            });
            row.Children.Add(info);
            if (open)
            {
                var predict = new Button { Content = "Predict", VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(12, 4, 12, 4),
                    IsEnabled = _engage?.Balance is > 0 };
                predict.Flyout = PredictFlyout(prediction, outcome);
                Grid.SetColumn(predict, 1);
                row.Children.Add(predict);
            }
            EngageContent.Children.Add(row);
        }

        string footer = prediction.MyOutcomeId is { } mine
            ? $"You predicted {prediction.Outcomes.FirstOrDefault(o => o.Id == mine)?.Title ?? "an outcome"} with {prediction.MyPoints:N0} points."
            : prediction.Status == "LOCKED" ? "Predictions are closed. Waiting for the result." : "";
        if (_engage?.Balance is { } balance) footer = (footer + $"  You have {balance:N0} points.").Trim();
        if (footer.Length > 0)
            EngageContent.Children.Add(new TextBlock { Text = footer, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["GlintSubtleTextBrush"] });
    }

    Flyout PredictFlyout(Prediction prediction, PredictionOutcome outcome)
    {
        long balance = _engage?.Balance ?? 0;
        var amount = new NumberBox
        {
            Header = $"Points on “{outcome.Title}”", Minimum = 1, Maximum = Math.Max(1, balance), Value = Math.Min(100, Math.Max(1, balance)),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, SmallChange = 10, LargeChange = 100,
        };
        var quick = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (label, value) in new[] { ("10", 10L), ("100", 100L), ("1K", 1000L), ("All", balance) })
        {
            var b = new Button { Content = label, Padding = new Thickness(10, 3, 10, 3), IsEnabled = balance >= Math.Min(value, 1) && value <= balance };
            b.Click += (_, _) => amount.Value = value;
            quick.Children.Add(b);
        }
        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var go = new Button { Content = "Predict", Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch };
        var panel = new StackPanel { Width = 240, Spacing = 10, Children = { amount, quick, go, status,
            new TextBlock { Text = $"You have {balance:N0} points", FontSize = 12, Foreground = (Brush)Application.Current.Resources["GlintSubtleTextBrush"] } } };
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.Left };
        flyout.Opened += (_, _) => _engageFlyoutOpen = true;
        flyout.Closed += (_, _) => { _engageFlyoutOpen = false; RenderEngagement(); };
        TrackVideoPopup(flyout);
        go.Click += async (_, _) =>
        {
            if (double.IsNaN(amount.Value) || amount.Value < 1) return;
            go.IsEnabled = false;
            var error = await TwitchGql.MakePredictionAsync(prediction.Id, outcome.Id, (long)amount.Value);
            go.IsEnabled = true;
            if (error != null)
            {
                status.Text = EngageErrorText(error);
                status.Visibility = Visibility.Visible;
                return;
            }
            flyout.Hide();
            _ = RefreshEngagementAsync();
        };
        return flyout;
    }

    void AddPoll(Poll poll)
    {
        int total = Math.Max(1, poll.Choices.Sum(c => c.Votes));
        _myPollVotes.TryGetValue(poll.Id, out var mine);
        bool open = poll.Status == "ACTIVE" && mine == null;
        var accent = (Brush)Application.Current.Resources["GlintAccentBrush"];
        foreach (var choice in poll.Choices)
        {
            double share = (double)choice.Votes / total;
            var content = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = (choice.Id == mine ? "✓ " : "") + choice.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            var percent = new TextBlock { Text = $"{share:0%}  ({choice.Votes:N0})", FontSize = 12.5, Foreground = (Brush)Application.Current.Resources["GlintSubtleTextBrush"] };
            Grid.SetColumn(percent, 1);
            line.Children.Add(name);
            line.Children.Add(percent);
            content.Children.Add(line);
            content.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = share, Foreground = accent, Height = 4 });
            if (!open)
            {
                EngageContent.Children.Add(content);
                continue;
            }
            var vote = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 6, 10, 8) };
            AutomationPropertiesName(vote, $"Vote for {choice.Title}");
            vote.Click += async (_, _) =>
            {
                if (S.WebSessionUserId is not { } user) return;
                vote.IsEnabled = false;
                var error = await TwitchGql.VoteInPollAsync(poll.Id, choice.Id, user);
                _engageError = error != null ? EngageErrorText(error) : null;
                if (error == null) _myPollVotes[poll.Id] = choice.Id;
                RenderEngagement();
                _ = RefreshEngagementAsync();
            };
            EngageContent.Children.Add(vote);
        }
        EngageContent.Children.Add(new TextBlock
        {
            Text = poll.Status == "COMPLETED" ? $"Poll ended. {total:N0} votes." : mine != null ? "Thanks for voting." : $"{total:N0} votes so far. Click a choice to vote.",
            FontSize = 12, Foreground = (Brush)Application.Current.Resources["GlintSubtleTextBrush"],
        });
    }

    static void AutomationPropertiesName(UIElement element, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);

    void UpdateEngageClock()
    {
        if (_engageTab != null && _engageTab != _active) { HideEngagement(); _ = RefreshEngagementAsync(); return; }
        if (_engageTime == null || EngageCard.Visibility != Visibility.Visible) return;
        var prediction = _engage?.Prediction;
        var poll = _engage?.Poll;
        _engageTime.Text = prediction is { Status: "ACTIVE" } ? Left(prediction.ClosesAt)
            : prediction is { Status: "LOCKED" } ? "Closed"
            : poll is { Status: "ACTIVE" } ? Left(poll.EndsAt)
            : poll is { Status: "COMPLETED" } ? "Ended" : "";
    }

    static string Left(DateTimeOffset until)
    {
        var left = until - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero) return "Closing";
        return left.TotalHours >= 1 ? $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00} left" : $"{left.Minutes}:{left.Seconds:00} left";
    }

    static string EngageErrorText(string code) => code switch
    {
        "NOT_ENOUGH_POINTS" or "INSUFFICIENT_POINTS" => "You don't have enough channel points for that.",
        "EVENT_NOT_ACTIVE" or "EVENT_LOCKED" or "PREDICTION_LOCKED" => "Predictions just closed.",
        "POLL_NOT_ACTIVE" or "POLL_ENDED" => "This poll has ended.",
        "NETWORK" => "Couldn't reach Twitch. Try again.",
        _ => $"Twitch didn't accept that ({code.ToLowerInvariant().Replace('_', ' ')}).",
    };
}
