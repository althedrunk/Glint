using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Glint;

public sealed record UpdateAsset(string Name, string Url, long Size, string? Sha256);

public sealed record ReleaseNotes(Version Version, string Title, DateTimeOffset Published, string Body);

public sealed record UpdateInfo(Version Version, string PageUrl, List<ReleaseNotes> Notes, UpdateAsset? Setup, UpdateAsset? Portable, UpdateAsset? Sums);

// Newer Glint releases from GitHub. Installed copies update with the release's Setup.exe,
// portable ones with its zip.
public static class Updater
{
    const string Repo = "althedrunk/Glint";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases";

    public static Version Current { get; } = Trim(typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0));
    public static UpdateInfo? Available { get; private set; }
    public static event Action? Changed;

    public static bool IsPortable => File.Exists(Path.Combine(Paths.ApplicationDirectory, "Data", "portable.flag"));

    static bool IsInstalled => SameDir(Paths.ApplicationDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Glint"));

    // A copy run from a build folder isn't ours to overwrite.
    public static bool CanInstall => IsPortable || IsInstalled;

    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases?per_page=20");
            req.Headers.TryAddWithoutValidation("User-Agent", "Glint");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var resp = await Net.Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) { Log.Error($"Update check: HTTP {(int)resp.StatusCode}"); return Available; }
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());

            var newer = new List<(Version Version, JsonElement Release)>();
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.Bool("draft") || release.Bool("prerelease")) continue;
                if (ParseTag(release.Str("tag_name")) is { } v && v > Current) newer.Add((v, release));
            }
            newer.Sort((a, b) => b.Version.CompareTo(a.Version));

            UpdateInfo? info = null;
            if (newer.Count > 0)
            {
                var latest = newer[0].Release;
                UpdateAsset? Asset(string suffix) => latest.Arr("assets")
                    .Where(a => a.Str("name")?.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) == true)
                    .Select(a => new UpdateAsset(a.Str("name")!, a.Str("browser_download_url") ?? "",
                        a.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                        a.Str("digest") is { } d && d.StartsWith("sha256:") ? d[7..].ToLowerInvariant() : null))
                    .FirstOrDefault(a => a.Url.Length > 0);
                info = new UpdateInfo(newer[0].Version, latest.Str("html_url") ?? ReleasesPage,
                    newer.Select(n => new ReleaseNotes(n.Version, n.Release.Str("name") ?? $"Glint {n.Version}",
                        DateTimeOffset.TryParse(n.Release.Str("published_at"), out var at) ? at : default,
                        n.Release.Str("body") ?? "")).ToList(),
                    Asset("-Setup.exe"), Asset("-Portable.zip"), Asset("-SHA256SUMS.txt"));
            }
            Available = info;
            Changed?.Invoke();
            return info;
        }
        catch (Exception ex)
        {
            Log.Error("Update check", ex);
            return Available;
        }
    }

    // Downloads and verifies the update, then starts a helper that swaps it in once Glint has closed.
    // Returns an error message, or null when the caller should close Glint.
    public static async Task<string?> InstallAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct)
    {
        var asset = IsPortable ? info.Portable : info.Setup;
        if (asset == null) return "This release has no download for your copy of Glint.";
        var dir = Path.Combine(Path.GetTempPath(), "Glint-update");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, asset.Name);

        using (var resp = await Net.Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? asset.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(file);
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress.Report((double)done / total);
            }
        }

        var expected = asset.Sha256 ?? await ChecksumFromSumsAsync(info.Sums, asset.Name, ct);
        if (expected == null) return "Couldn't find a checksum for this release, so it wasn't installed.";
        string actual;
        await using (var stream = File.OpenRead(file)) actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        if (actual != expected)
        {
            File.Delete(file);
            return "The download didn't match its checksum, so it wasn't installed. Try again later.";
        }

        var script = Path.Combine(dir, "update.ps1");
        await File.WriteAllTextAsync(script, HelperScript, ct);
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                     "-ProcessId", Environment.ProcessId.ToString(), "-Package", file, "-AppDir", Paths.ApplicationDirectory,
                     "-Mode", IsPortable ? "Portable" : "Setup" })
            start.ArgumentList.Add(arg);
        Process.Start(start);
        return null;
    }

    static async Task<string?> ChecksumFromSumsAsync(UpdateAsset? sums, string name, CancellationToken ct)
    {
        if (sums == null) return null;
        var text = await Net.Http.GetStringAsync(sums.Url, ct);
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Trim().TrimStart('*') == name) return parts[0].ToLowerInvariant();
        }
        return null;
    }

    // Waits for Glint to exit, installs, and opens the new version.
    const string HelperScript = """
        param([int]$ProcessId, [string]$Package, [string]$AppDir, [string]$Mode)
        $ErrorActionPreference = 'Stop'
        $log = Join-Path (Split-Path $Package) 'update.log'
        Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue
        if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) { Add-Content $log 'Glint did not close'; exit 1 }
        $exe = Join-Path $AppDir 'Glint.exe'
        try {
            if ($Mode -eq 'Setup') {
                $setup = Start-Process -FilePath $Package -Wait -PassThru -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$AppDir`""
                if ($setup.ExitCode -ne 0) { throw "Setup exited with $($setup.ExitCode)" }
            } else {
                $extract = Join-Path (Split-Path $Package) 'extract'
                Expand-Archive -LiteralPath $Package -DestinationPath $extract -Force
                Copy-Item -LiteralPath (Join-Path $extract 'Glint.exe') -Destination $exe -Force
                if (Test-Path (Join-Path $extract 'Docs')) { Copy-Item -Path (Join-Path $extract 'Docs') -Destination $AppDir -Recurse -Force }
            }
        } catch {
            Add-Content -Path $log -Value $_.ToString()
        }
        Start-Process -FilePath $exe
        """;

    static Version? ParseTag(string? tag)
    {
        if (tag == null) return null;
        var core = tag.TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(core, out var v) ? Trim(v) : null;
    }

    static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    static bool SameDir(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
