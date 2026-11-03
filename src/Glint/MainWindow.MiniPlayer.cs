namespace Glint;

// Popping the current stream out into the always-on-top mini player, and bringing it back.
public sealed partial class MainWindow
{
    MiniPlayerWindow? _mini;
    ChannelTab? _miniTab;

    void TogglePopOut()
    {
        if (_miniTab != null) BringBackFromMini();
        else PopOut();
    }

    void PopOut()
    {
        if (_active is not { } tab || MultiviewActive || tab.Playback.Host is not { } host || tab.Playback.Player == null) return;
        if (_fullscreen) ToggleFullscreen();
        _miniTab = tab;
        tab.PoppedOut = true;
        CloseOverlayMenus();
        UpdateVideoBounds();
        var mini = _mini = new MiniPlayerWindow(tab.Entry.DisplayName, host, Asset("Glint.ico"));
        mini.AppWindow.Closing += (_, _) => { if (_mini == mini) BringBackFromMini(closeWindow: false); };
        mini.Activate();
        RenderPlayerState();
    }

    void BringBackFromMini(bool closeWindow = true)
    {
        if (_miniTab is not { } tab) return;
        var mini = _mini;
        _mini = null;
        _miniTab = null;
        tab.PoppedOut = false;
        tab.Playback.Host?.Reattach(_hwnd);
        if (closeWindow) mini?.Close();
        if (_closing) return;
        RenderPlayerState();
        UpdateVideoBounds();
        Activate();
    }
}
