using Windows.Media.Core;
using Windows.Media.Playback;

namespace Glint;

static class NotificationSound
{
    public static readonly string[] Tones = ["Bell", "Soft", "Double tap"];
    static MediaPlayer? _player;
    static MediaSource? _source;
    static long _lastPlayed;

    public static void Play(string tone, bool preview = false)
    {
        if (!preview && Environment.TickCount64 - _lastPlayed < 800) return;
        _lastPlayed = Environment.TickCount64;
        try
        {
            int index = Array.IndexOf(Tones, tone);
            if (index < 0) index = 0;
            string path = Path.Combine(Paths.LocalData, $"notification-{index}.wav");
            if (!File.Exists(path)) WriteTone(path, index);
            _player ??= new MediaPlayer();
            _player.CommandManager.IsEnabled = false;
            _player.Pause();
            _player.Source = null;
            _source?.Dispose();
            _source = MediaSource.CreateFromUri(new Uri(path));
            _player.Volume = Math.Clamp(Settings.Current.NotificationVolume / 100, 0, 1);
            _player.Source = _source;
            _player.Play();
        }
        catch (Exception ex) { Log.Error("Notification sound", ex); }
    }

    static void WriteTone(string path, int tone)
    {
        const int rate = 44100, samples = rate / 2;
        using var file = new BinaryWriter(File.Create(path));
        file.Write("RIFF"u8); file.Write(36 + samples * 2); file.Write("WAVEfmt "u8);
        file.Write(16); file.Write((short)1); file.Write((short)1); file.Write(rate); file.Write(rate * 2);
        file.Write((short)2); file.Write((short)16); file.Write("data"u8); file.Write(samples * 2);
        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / rate, frequency = tone == 1 ? 440 : 880;
            if (tone == 2 && t > 0.16) { t -= 0.16; frequency = 1100; }
            double envelope = Math.Min(1, t * 250) * Math.Exp(-t * (tone == 1 ? 10 : 16));
            file.Write((short)(Math.Sin(t * frequency * Math.Tau) * envelope * 12000));
        }
    }

    public static void Stop() { _player?.Dispose(); _player = null; _source?.Dispose(); _source = null; }
}
