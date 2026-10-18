using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Graphics.Canvas;
using SkiaSharp;
using Windows.Graphics.DirectX;

namespace Glint;

// A decoded (possibly animated) image living on the GPU as one bitmap per frame.
public sealed class AnimatedImage
{
    public required string Url { get; init; }
    public CanvasBitmap[]? Frames { get; internal set; }
    int[] _frameEnds = Array.Empty<int>();
    int _totalMs;
    public int PixelWidth { get; internal set; }
    public int PixelHeight { get; internal set; }
    public bool Failed { get; internal set; }
    public bool IsLoaded => Frames != null;
    public bool IsAnimated => Frames is { Length: > 1 };

    internal void SetFrames(CanvasBitmap[] frames, int[] delays)
    {
        var ends = new int[delays.Length];
        int t = 0;
        for (int i = 0; i < delays.Length; i++) { t += delays[i]; ends[i] = t; }
        _frameEnds = ends;
        _totalMs = Math.Max(1, t);
        Frames = frames;
    }

    public CanvasBitmap? FrameAt(long ms)
    {
        var frames = Frames;
        if (frames == null || frames.Length == 0) return null;
        if (frames.Length == 1) return frames[0];
        int t = (int)(ms % _totalMs);
        int i = Array.BinarySearch(_frameEnds, t);
        i = i < 0 ? ~i : i + 1;
        return frames[Math.Min(i, frames.Length - 1)];
    }
}

public static class ImageCache
{
    static readonly ConcurrentDictionary<string, AnimatedImage> Images = new();
    static readonly SemaphoreSlim DownloadGate = new(8, 8);
    static readonly SemaphoreSlim DecodeGate = new(Math.Max(2, Environment.ProcessorCount / 2));

    // Raised (on a worker thread) when any image finishes loading.
    public static event Action? ImageLoaded;

    // Raised when an emote's on-screen size was unknown until its image arrived (needs re-layout).
    public static event Action? SizeDiscovered;

    public static AnimatedImage Get(string url, EmoteInfo? owner = null)
    {
        if (Images.TryGetValue(url, out var existing)) return existing;
        var img = new AnimatedImage { Url = url };
        if (!Images.TryAdd(url, img)) return Images[url];
        _ = Task.Run(() => LoadAsync(img, owner));
        return img;
    }

    static string CachePath(string url)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)));
        return Path.Combine(Paths.ImageCache, hash[..2], hash);
    }

    static async Task LoadAsync(AnimatedImage img, EmoteInfo? owner)
    {
        try
        {
            var path = CachePath(img.Url);
            byte[]? bytes = null;
            if (File.Exists(path))
            {
                try { bytes = await File.ReadAllBytesAsync(path); } catch { }
            }
            if (bytes == null)
            {
                await DownloadGate.WaitAsync();
                try { bytes = await Net.Http.GetByteArrayAsync(img.Url); }
                finally { DownloadGate.Release(); }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                try { await File.WriteAllBytesAsync(path, bytes); } catch { }
            }

            await DecodeGate.WaitAsync();
            try { Decode(img, bytes); }
            finally { DecodeGate.Release(); }

            if (owner != null && (owner.Width <= 0 || owner.Height <= 0) && img.PixelHeight > 0)
            {
                // 2x assets → DIPs
                owner.Height = img.PixelHeight / 2f;
                owner.Width = img.PixelWidth / 2f;
                SizeDiscovered?.Invoke();
            }
        }
        catch (Exception ex)
        {
            img.Failed = true;
            Log.Error($"Image {img.Url}", ex);
        }
        ImageLoaded?.Invoke();
    }

    const int MaxFrames = 400;

    static void Decode(AnimatedImage img, byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("Unsupported image");
        int w = codec.Info.Width, h = codec.Info.Height;
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        var device = CanvasDevice.GetSharedDevice();
        int frameCount = Math.Min(Math.Max(codec.FrameCount, 1), MaxFrames);
        int stride = w * 4;

        var frames = new CanvasBitmap[frameCount];
        var delays = new int[frameCount];
        var pixels = new byte[frameCount][];
        var frameInfo = codec.FrameInfo;

        for (int i = 0; i < frameCount; i++)
        {
            var buffer = new byte[stride * h];
            int required = frameCount > 1 && i < frameInfo.Length ? frameInfo[i].RequiredFrame : -1;
            if (required >= 0 && required < i) Buffer.BlockCopy(pixels[required], 0, buffer, 0, buffer.Length);

            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var opts = frameCount > 1 ? new SKCodecOptions(i, required >= 0 && required < i ? required : -1) : new SKCodecOptions();
                var result = codec.GetPixels(info, handle.AddrOfPinnedObject(), stride, opts);
                if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput && i == 0)
                    throw new InvalidDataException($"Decode failed: {result}");
            }
            finally { handle.Free(); }

            pixels[i] = buffer;
            var duration = frameCount > 1 && i < frameInfo.Length ? frameInfo[i].Duration : 0;
            delays[i] = duration <= 10 ? 100 : duration;
            frames[i] = CanvasBitmap.CreateFromBytes(device, buffer, w, h,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        }

        img.PixelWidth = w;
        img.PixelHeight = h;
        img.SetFrames(frames, delays);
    }
}
