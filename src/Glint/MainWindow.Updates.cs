using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Glint;

// Version in the sidebar footer, update checks, and the update dialog with release notes.
public sealed partial class MainWindow
{
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _updateTimer;
    bool _updateDialogOpen;

    void WireUpdates()
    {
        VersionButton.Click += (_, _) => ShowUpdateDialog();
        Updater.Changed += () => _dispatcher.TryEnqueue(UpdateVersionUi);
        UpdateVersionUi();
        // First check shortly after launch, then every few hours.
        _updateTimer = Timer(TimeSpan.FromSeconds(15), () =>
        {
            _updateTimer!.Interval = TimeSpan.FromHours(6);
            if (S.CheckForUpdates) _ = Updater.CheckAsync();
        });
        _updateTimer.Start();
    }

    // Close() from code skips AppWindow.Closing, so shut down by hand and make sure the process ends.
    void Quit()
    {
        if (!_closing) Shutdown();
        Close();
        Application.Current.Exit();
    }

    void UpdateVersionUi()
    {
        var update = Updater.Available;
        bool collapsed = S.SidebarCollapsed;
        var accent = (Brush)Application.Current.Resources["GlintAccentBrush"];
        var subtle = (Brush)Application.Current.Resources["GlintSubtleTextBrush"];
        // Expanded, the version sits under your name and the button only shows up for an update.
        AccountSubText.Text = update != null ? "Update available" : $"Glint v{Updater.Current.ToString(3)}";
        AccountSubText.Foreground = update != null ? accent : subtle;
        VersionText.Text = $"v{Updater.Current.ToString(3)}";
        VersionText.FontSize = 10.5;
        VersionText.Visibility = collapsed && update == null ? Visibility.Visible : Visibility.Collapsed;
        UpdateIcon.Visibility = update != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateIcon.FontSize = 15;
        VersionButton.Visibility = collapsed || update != null ? Visibility.Visible : Visibility.Collapsed;
        VersionButton.Padding = collapsed ? new Thickness(4, 2, 4, 2) : new Thickness(6, 4, 6, 4);
        ToolTipService.SetToolTip(VersionButton, update != null ? $"Glint {update.Version.ToString(3)} is available" : "Check for updates");
    }

    internal async void ShowUpdateDialog()
    {
        if (_updateDialogOpen || Root.XamlRoot == null) return;
        _updateDialogOpen = true;
        try
        {
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = (Brush)Application.Current.Resources["GlintSubtleTextBrush"] };
            var progress = new ProgressBar { Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };
            var notes = new RichTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13.5, IsTextSelectionEnabled = true };
            var notesScroll = new ScrollViewer { Content = notes, MaxHeight = 360, Padding = new Thickness(0, 0, 14, 0), Visibility = Visibility.Collapsed };
            var content = new StackPanel { Spacing = 12, Width = 460, Children = { status, notesScroll, progress } };
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                RequestedTheme = ElementTheme.Dark,
                Title = "Checking for updates",
                Content = content,
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Primary,
            };
            // The dialog covers the whole window, so park the video the way Browse does.
            dialog.Opened += (_, _) => SetVideoPopup(dialog, true);
            dialog.Closed += (_, _) => SetVideoPopup(dialog, false);

            void Show(UpdateInfo? update)
            {
                if (update == null)
                {
                    dialog.Title = "Glint is up to date";
                    status.Text = $"You have the latest version, {Updater.Current.ToString(3)}.";
                    dialog.PrimaryButtonText = "";
                    dialog.SecondaryButtonText = "All releases";
                    return;
                }
                dialog.Title = $"Glint {update.Version.ToString(3)} is available";
                status.Text = $"You have {Updater.Current.ToString(3)}."
                    + (Updater.CanInstall ? "" : " This copy runs from a build folder, so it can't update itself. Download it from the release page.");
                notes.Blocks.Clear();
                foreach (var release in update.Notes) AddReleaseNotes(notes, release);
                notesScroll.Visibility = Visibility.Visible;
                dialog.PrimaryButtonText = Updater.CanInstall ? "Update and restart" : "";
                dialog.SecondaryButtonText = "Release page";
                dialog.CloseButtonText = "Later";
            }

            if (Updater.Available is { } known) Show(known);
            else status.Text = "Looking for a newer version…";

            CancellationTokenSource? download = null;
            dialog.SecondaryButtonClick += (_, _) => Launch(Updater.Available?.PageUrl ?? Updater.ReleasesPage);
            dialog.PrimaryButtonClick += async (_, e) =>
            {
                if (Updater.Available is not { } update) return;
                e.Cancel = true; // stay open while downloading
                dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = false;
                dialog.CloseButtonText = "Cancel";
                progress.Visibility = Visibility.Visible;
                status.Text = "Downloading…";
                download = new CancellationTokenSource();
                try
                {
                    var error = await Updater.InstallAsync(update, new Progress<double>(p => progress.Value = p), download.Token);
                    if (error == null)
                    {
                        status.Text = "Restarting Glint…";
                        Quit(); // the helper waits for us to exit, installs, then opens the new version
                        return;
                    }
                    status.Text = error;
                }
                catch (OperationCanceledException) { status.Text = "Update cancelled."; }
                catch (Exception ex)
                {
                    Log.Error("Update", ex);
                    status.Text = "The update couldn't be downloaded. Check your connection and try again.";
                }
                progress.Visibility = Visibility.Collapsed;
                dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = true;
                dialog.CloseButtonText = "Later";
            };
            dialog.CloseButtonClick += (_, _) => download?.Cancel();

            var check = Updater.CheckAsync();
            var shown = dialog.ShowAsync();
            Show(await check);
            await shown;
        }
        finally { _updateDialogOpen = false; }
    }

    // Release notes are Markdown; headings, bullets, bold and links cover what GitHub notes use.
    static void AddReleaseNotes(RichTextBlock target, ReleaseNotes release)
    {
        var heading = new Paragraph { Margin = new Thickness(0, target.Blocks.Count > 0 ? 14 : 0, 0, 6) };
        heading.Inlines.Add(new Run { Text = release.Title, FontWeight = FontWeights.SemiBold, FontSize = 15 });
        if (release.Published != default)
            heading.Inlines.Add(new Run { Text = "   " + release.Published.LocalDateTime.ToString("d MMM yyyy"), FontSize = 12.5,
                Foreground = (Brush)Application.Current.Resources["GlintSubtleTextBrush"] });
        target.Blocks.Add(heading);

        foreach (var raw in release.Body.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
            if (line.StartsWith('#'))
            {
                paragraph.Margin = new Thickness(0, 6, 0, 4);
                paragraph.Inlines.Add(new Run { Text = line.TrimStart('#').Trim(), FontWeight = FontWeights.SemiBold });
            }
            else
            {
                if (line.StartsWith("- ") || line.StartsWith("* "))
                {
                    paragraph.Margin = new Thickness(12, 0, 0, 4);
                    paragraph.TextIndent = -12;
                    paragraph.Inlines.Add(new Run { Text = "•  " });
                    line = line[2..];
                }
                AddInline(paragraph, line);
            }
            target.Blocks.Add(paragraph);
        }
    }

    static void AddInline(Paragraph paragraph, string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1").Replace("`", "");
        var parts = text.Split("**");
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0)
                paragraph.Inlines.Add(new Run { Text = parts[i], FontWeight = i % 2 == 1 ? FontWeights.SemiBold : FontWeights.Normal });
    }
}
