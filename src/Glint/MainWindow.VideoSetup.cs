using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Glint;

public sealed partial class MainWindow
{
    Task<bool>? _videoSetup;
    bool _videoSetupSkipped;
    readonly CancellationTokenSource _videoSetupCancellation = new();

    async Task<bool> EnsureVideoRuntimeAsync(ChannelTab tab)
    {
        if (MpvRuntime.IsAvailable) return !_closing && _tabs.Contains(tab);
        if (!_videoSetupSkipped)
        {
            _videoSetup ??= ShowVideoSetupAsync();
            try { await _videoSetup; }
            finally { _videoSetup = null; }
        }
        if (_closing || !_tabs.Contains(tab)) return false;
        if (MpvRuntime.IsAvailable) return true;
        tab.Playback.ErrorMessage = "Video playback needs a one-time download. Try again to set it up.";
        OnPlayerState(tab, Player.PlayerState.Error);
        return false;
    }

    async Task<bool> ShowVideoSetupAsync()
    {
        var text = new TextBlock { Text = "Download the video component from mpv's official GitHub release. " +
            "This is needed once, requires an internet connection and does not need administrator access.", TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
        var content = new StackPanel();
        content.Children.Add(text); content.Children.Add(progress);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = ElementTheme.Dark,
            Title = "Set up video playback", Content = content, PrimaryButtonText = "Download", CloseButtonText = "Not now" };
        bool installed = false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_videoSetupCancellation.Token);
        dialog.CloseButtonClick += (_, _) => cancellation.Cancel();
        dialog.Closing += (_, _) => { if (!installed) cancellation.Cancel(); };
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            e.Cancel = true;
            dialog.IsPrimaryButtonEnabled = false;
            progress.Visibility = Visibility.Visible;
            text.Text = "Downloading video playback. This may take a moment.";
            try
            {
                await MpvRuntime.InstallAsync(new Progress<double>(value => progress.Value = value), cancellation.Token);
                installed = true;
                e.Cancel = false;
            }
            catch (OperationCanceledException) { e.Cancel = false; }
            catch (Exception ex)
            {
                Log.Error("Video setup", ex);
                text.Text = "Couldn't download video playback. Check your connection and select Download to retry. " + ex.Message;
            }
            finally { dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        SetVideoPopup(dialog, true);
        try { await dialog.ShowAsync(); }
        finally { SetVideoPopup(dialog, false); }
        _videoSetupSkipped = !installed;
        return installed;
    }
}
