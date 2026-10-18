namespace Glint.Player;

sealed class TabPlayback : IDisposable
{
    public VideoHost? Host { get; set; }
    public MpvPlayer? Player { get; set; }
    public AdSkipper? Ads { get; set; }
    public PlayerState State { get; set; } = PlayerState.Idle;
    public List<StreamVariant> Variants { get; set; } = new();
    public StreamVariant? SelectedVariant { get; set; }
    public double Speed { get; set; } = 1;
    public double Volume { get; set; }
    public bool Muted { get; set; }
    public AdSkipStatus AdStatus { get; set; }
    public bool Started { get; set; }
    public bool Resolving { get; set; }
    public string? ErrorMessage { get; set; }
    public int Version { get; set; }

    public void Stop()
    {
        Version++;
        Started = false;
        Resolving = false;
        AdStatus = AdSkipStatus.None;
        State = PlayerState.Idle;
        Player?.SetMuted(true);
        Player?.Stop();
        Ads?.Stop();
        Host?.Hide();
    }

    public void Dispose()
    {
        Version++;
        Player?.SetMuted(true);
        Player?.Dispose();
        Player = null;
        Host?.Dispose();
        Host = null;
        Ads?.Dispose();
        Ads = null;
    }
}
