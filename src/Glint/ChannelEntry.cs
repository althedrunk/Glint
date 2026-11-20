using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Glint;

// A channel shown in the sidebar / tabs. Mutated on the UI thread only.
public sealed partial class ChannelEntry : INotifyPropertyChanged
{
    public ChannelEntry(string login) => Login = login.ToLowerInvariant();

    public string Login { get; }
    public double AvatarSize => Settings.Current.SidebarAvatarSize;
    public Thickness RowPadding => new(2, Settings.Current.InterfaceDensity == "Compact" ? 2 : 6, 2,
        Settings.Current.InterfaceDensity == "Compact" ? 2 : 6);
    public void RefreshAppearance() { Raise(nameof(AvatarSize)); Raise(nameof(RowPadding)); }
    public string? Id { get; private set; }

    string? _displayName;
    public string DisplayName => _displayName ?? Login;
    public override string ToString() => DisplayName; // what screen readers announce for list items

    string? _avatarUrl;
    ImageSource? _avatar;
    public string? AvatarUrl => _avatarUrl;
    public ImageSource? Avatar
    {
        get
        {
            if (_avatar == null && Uri.TryCreate(_avatarUrl, UriKind.Absolute, out var uri))
            {
                var bitmap = new BitmapImage { DecodePixelWidth = 64, DecodePixelType = DecodePixelType.Logical };
                _avatar = bitmap;
                _ = LoadAvatarAsync(bitmap, uri);
            }
            return _avatar;
        }
    }

    static async Task LoadAvatarAsync(BitmapImage bitmap, Uri uri)
    {
        try
        {
            var bytes = await ImageCache.LoadAvatarBytesAsync(uri.AbsoluteUri);
            using var stream = new MemoryStream(bytes).AsRandomAccessStream();
            await bitmap.SetSourceAsync(stream);
        }
        catch
        {
            bitmap.UriSource = uri;
        }
    }

    public StreamInfo? Stream { get; private set; }
    public bool IsLive => Stream != null;
    public Brush LiveRingBrush => (Brush)Application.Current.Resources[IsLive ? "GlintLiveBrush" : "GlintPanelBorderBrush"];
    public bool Known { get; private set; }

    bool _isFavorite, _isFollowed;
    public bool IsFavorite { get => _isFavorite; set { _isFavorite = value; Raise(); } }
    public bool IsFollowed { get => _isFollowed; set { _isFollowed = value; Raise(); } }

    public string Subtitle => Stream is { } s ? (s.Game ?? "Live") : "Offline";
    public Visibility AlertVisibility => Settings.Current.LiveAlertChannels.Contains(Login) ? Visibility.Visible : Visibility.Collapsed;
    public void RefreshAlert() => Raise(nameof(AlertVisibility));
    public string ViewersText => Stream is { } s ? FormatCount(s.Viewers) : "";
    public Visibility LiveVisibility => IsLive ? Visibility.Visible : Visibility.Collapsed;
    public double ItemOpacity => IsLive ? 1.0 : 0.5;
    public string Tooltip => Stream is { } s ? $"{DisplayName}: {s.Title}" : $"{DisplayName} (offline)";

    public void Update(UserInfo info)
    {
        Id = info.Id;
        Known = true;
        _displayName = info.DisplayName;
        if (_avatarUrl != info.AvatarUrl)
        {
            _avatarUrl = info.AvatarUrl;
            _avatar = null;
            Raise(nameof(Avatar));
        }
        Stream = info.Stream;
        Raise(nameof(DisplayName));
        Raise(nameof(Stream));
        Raise(nameof(IsLive));
        Raise(nameof(LiveRingBrush));
        Raise(nameof(Subtitle));
        Raise(nameof(ViewersText));
        Raise(nameof(LiveVisibility));
        Raise(nameof(ItemOpacity));
        Raise(nameof(Tooltip));
    }

    public static string FormatCount(int n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 10_000 => $"{n / 1000.0:0}K",
        >= 1_000 => $"{n / 1000.0:0.#}K",
        _ => n.ToString(),
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
