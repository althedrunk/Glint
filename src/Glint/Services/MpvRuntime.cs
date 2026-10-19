using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Glint;

public static class MpvRuntime
{
    public const string ReleaseUrl = "https://github.com/mpv-player/mpv/releases/tag/git-release";
    const string ReleaseApi = "https://api.github.com/repos/mpv-player/mpv/releases/tags/git-release";
    const long MaxArchiveBytes = 250 * 1024 * 1024;
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static string LibraryPath => File.Exists(Path.Combine(Paths.ApplicationDirectory, "libmpv-2.dll"))
        ? Path.Combine(Paths.ApplicationDirectory, "libmpv-2.dll")
        : Path.Combine(Paths.LocalData, "player", "libmpv-2.dll");
    public static bool IsAvailable => File.Exists(LibraryPath);

    public static Task InstallAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        InstallAsync(Client, new Uri(ReleaseApi), Path.Combine(Paths.LocalData, "player"), progress, cancellationToken);

    internal sealed record Asset(string Name, string Url, long Size, string Sha256);

    internal static Asset ReadAsset(JsonElement release)
    {
        foreach (var item in release.GetProperty("assets").EnumerateArray())
        {
            string name = item.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith("libmpv-", StringComparison.Ordinal) ||
                !name.EndsWith("-x86_64-w64-mingw32-lgpl.zip", StringComparison.Ordinal)) continue;
            string url = item.GetProperty("browser_download_url").GetString() ?? "";
            string digest = item.GetProperty("digest").GetString() ?? "";
            long size = item.GetProperty("size").GetInt64();
            if (!url.StartsWith("https://github.com/mpv-player/mpv/releases/download/", StringComparison.Ordinal) ||
                !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 ||
                !digest[7..].All(char.IsAsciiHexDigit) || size <= 0 || size > MaxArchiveBytes)
                throw new InvalidDataException("The publisher did not provide a valid download checksum.");
            return new(name, url, size, digest[7..]);
        }
        throw new InvalidDataException("The publisher's Windows x64 LGPL video component is unavailable.");
    }

    internal static async Task InstallAsync(HttpClient client, Uri metadataUrl, string directory,
        IProgress<double> progress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, metadataUrl);
        request.Headers.UserAgent.ParseAdd("Glint/0.1");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var release = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var asset = ReadAsset(release.RootElement);
        string parent = Path.GetDirectoryName(Path.GetFullPath(directory))!;
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, "player-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            string archivePath = Path.Combine(staging, "upstream.zip");
            using (var download = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                download.EnsureSuccessStatusCode();
                await using var input = await download.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = File.Create(archivePath);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    received += count;
                    if (received > asset.Size) throw new InvalidDataException("The download is larger than the publisher specified.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    progress.Report((double)received / asset.Size * 100);
                }
                if (received != asset.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The video component's checksum did not match. Please retry the download.");
            }
            string library = Path.Combine(staging, "libmpv-2.dll");
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                foreach (string name in new[] { "libmpv-2.dll", "LICENSE.LGPL" })
                {
                    var entry = archive.GetEntry(name) ?? throw new InvalidDataException("The video component package is incomplete.");
                    if (entry.Length <= 0 || entry.Length > 400L * 1024 * 1024)
                        throw new InvalidDataException("The video component package contains an invalid file.");
                    await using var input = entry.Open();
                    await using var output = File.Create(Path.Combine(staging, name));
                    await input.CopyToAsync(output, cancellationToken);
                }
            }
            CheckArchitecture(library);
            cancellationToken.ThrowIfCancellationRequested();
            string libraryHash;
            await using (var stream = File.OpenRead(library))
                libraryHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            await File.WriteAllTextAsync(Path.Combine(staging, "download.json"), JsonSerializer.Serialize(new
            {
                Publisher = ReleaseUrl, asset.Name, asset.Url, asset.Sha256,
                LibrarySha256 = libraryHash
            }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            Directory.CreateDirectory(directory);
            File.Copy(Path.Combine(staging, "LICENSE.LGPL"), Path.Combine(directory, "LICENSE.LGPL"), true);
            File.Copy(Path.Combine(staging, "download.json"), Path.Combine(directory, "download.json"), true);
            try { File.Move(library, Path.Combine(directory, "libmpv-2.dll"), false); }
            catch (IOException) when (File.Exists(Path.Combine(directory, "libmpv-2.dll"))) { }
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(staging)) File.Delete(file);
            Directory.Delete(staging);
        }
    }

    internal static void CheckArchitecture(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.BaseStream.Length < 64 || reader.ReadUInt16() != 0x5A4D)
            throw new InvalidDataException("The downloaded video component is not a Windows library.");
        reader.BaseStream.Position = 0x3C;
        int offset = reader.ReadInt32();
        if (offset < 64 || offset > reader.BaseStream.Length - 6)
            throw new InvalidDataException("The downloaded video component has an invalid header.");
        reader.BaseStream.Position = offset;
        if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x8664)
            throw new InvalidDataException("The downloaded video component does not support Windows x64.");
    }
}
