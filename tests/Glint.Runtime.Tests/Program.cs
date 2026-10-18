using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Glint;

var root = Path.Combine(Path.GetTempPath(), "glint-runtime-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var progress = new Progress<double>();
byte[] Library(ushort machine = 0x8664)
{
    var bytes = new byte[128];
    BitConverter.GetBytes((ushort)0x5A4D).CopyTo(bytes, 0);
    BitConverter.GetBytes(64).CopyTo(bytes, 0x3C);
    BitConverter.GetBytes(0x00004550).CopyTo(bytes, 64);
    BitConverter.GetBytes(machine).CopyTo(bytes, 68);
    return bytes;
}
byte[] Archive(byte[] library, bool license = true)
{
    using var buffer = new MemoryStream();
    using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
    {
        using (var output = zip.CreateEntry("libmpv-2.dll").Open()) output.Write(library);
        if (license) { using var writer = new StreamWriter(zip.CreateEntry("LICENSE.LGPL").Open()); writer.Write("Test licence"); }
        using var ignored = new StreamWriter(zip.CreateEntry("../unexpected.txt").Open());
        ignored.Write("This entry must not be extracted");
    }
    return buffer.ToArray();
}
string Metadata(byte[] zip, string? digest = null, string? url = null, long? size = null) => JsonSerializer.Serialize(new
{
    assets = new[] { new { name = "libmpv-test-x86_64-w64-mingw32-lgpl.zip",
        browser_download_url = url ?? "https://github.com/mpv-player/mpv/releases/download/git-release/test.zip",
        digest = digest ?? "sha256:" + Convert.ToHexString(SHA256.HashData(zip)), size = size ?? zip.LongLength } }
});
async Task Run(string name, byte[] zip, string metadata, bool success, CancellationToken cancellation = default, IProgress<double>? report = null)
{
    string directory = Path.Combine(root, Guid.NewGuid().ToString("N"), "player");
    using var client = new HttpClient(new Handler(metadata, zip));
    bool installed;
    try { await MpvRuntime.InstallAsync(client, new Uri("https://test.invalid/release"), directory, report ?? progress, cancellation); installed = true; }
    catch (Exception ex) when (ex is InvalidDataException or OperationCanceledException) { installed = false; }
    if (installed != success) throw new Exception("FAIL: " + name);
    if (success)
    {
        if (!File.ReadAllBytes(Path.Combine(directory, "libmpv-2.dll")).SequenceEqual(Library()) ||
            !File.Exists(Path.Combine(directory, "LICENSE.LGPL")) || !File.Exists(Path.Combine(directory, "download.json")) ||
            File.Exists(Path.Combine(Path.GetDirectoryName(directory)!, "unexpected.txt"))) throw new Exception("FAIL: Installed files or metadata");
    }
    else if (File.Exists(Path.Combine(directory, "libmpv-2.dll"))) throw new Exception("FAIL: An invalid download became available");
    if (Directory.Exists(Path.GetDirectoryName(directory)) && Directory.EnumerateDirectories(Path.GetDirectoryName(directory)!, "player-download-*").Any())
        throw new Exception("FAIL: Temporary downloads were not cleaned up");
    Console.WriteLine("PASS: " + name);
}
try
{
    var zip = Archive(Library());
    await Run("Verified x64 library, licence and provenance install; unrelated ZIP paths are ignored", zip, Metadata(zip), true);
    await Run("Wrong checksums never install a library", zip, Metadata(zip, "sha256:" + new string('0', 64)), false);
    await Run("Missing SHA-256 digests are rejected", zip, Metadata(zip, ""), false);
    await Run("Downloads outside the publisher's repository are rejected", zip, Metadata(zip, url: "https://example.com/test.zip"), false);
    await Run("Oversized downloads are rejected", zip, Metadata(zip, size: 300L * 1024 * 1024), false);
    await Run("Truncated downloads are rejected", zip, Metadata(zip, size: zip.Length + 1), false);
    var x86 = Archive(Library(0x014C));
    await Run("Wrong architectures are rejected", x86, Metadata(x86), false);
    var bad = Archive(new byte[128]);
    await Run("Invalid PE headers are rejected", bad, Metadata(bad), false);
    var noLicense = Archive(Library(), false);
    await Run("Incomplete packages are rejected", noLicense, Metadata(noLicense), false);
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    await Run("Cancelled setup never publishes a partial library", zip, Metadata(zip), false, cancellation.Token);
    using var duringDownload = new CancellationTokenSource();
    await Run("Cancelling an in-progress download removes its partial files", zip, Metadata(zip), false,
        duringDownload.Token, new CancelProgress(duringDownload));
}
finally
{
    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.Delete(file);
    foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length)) Directory.Delete(dir);
    Directory.Delete(root);
}

sealed class Handler(string metadata, byte[] archive) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = request.RequestUri!.Host == "test.invalid" ? new StringContent(metadata) : new ByteArrayContent(archive)
        });
    }
}

sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<double>
{
    public void Report(double value) => cancellation.Cancel();
}

namespace Glint
{
    public static class Paths { public static string LocalData => Path.GetTempPath(); }
}
