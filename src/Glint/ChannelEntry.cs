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
    public CornerRadius AvatarRadius => new(AvatarSize / 2);
    public Thickness RowPadding => new(2, Settings.Current.InterfaceDensity == "Compact" ? 2 : 6, 2,
        Settings.Current.InterfaceDensity == "Compact" ? 2 : 6);
    public void RefreshAppearance() { Raise(nameof(AvatarSize)); Raise(nameof(AvatarRadius)); Raise(nameof(RowPadding)); }
    public string? Id { get; private set; }

    string? _displayName;
    public string DisplayName => _displayName ?? Login;

    string? _avatarUrl;
    ImageSource? _avatar;
    public ImageSource? Avatar => _avatar ??= _avatarUrl != null
        ? new BitmapImage(new Uri(_avatarUrl)) { DecodePixelWidth = 64, DecodePixelType = DecodePixelType.Logical }
        : null;

    public StreamInfo? Stream { get; private set; }
    public bool IsLive => Stream != null;
    public bool Known { get; private set; }

    bool _isFavorite, _isFollowed;
    public bool IsFavorite { get => _isFavorite; set { _isFavorite = value; Raise(); } }
    public bool IsFollowed { get => _isFollowed; set { _isFollowed = value; Raise(); } }

    public string Subtitle => Stream is { } s ? (s.Game ?? "Live") : "Offline";
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
