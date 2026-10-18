# Downloads libmpv-2.dll into lib\mpv. It's ~115 MB, too big to keep in git.
# Builds come from shinchiro/mpv-winbuild-cmake (the ones linked from mpv.io). Old releases get pruned,
# so if the pinned one is gone we fall back to the latest.
$ErrorActionPreference = 'Stop'
$pinned = '20261018'
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'lib\mpv'
if (Test-Path (Join-Path $dest 'libmpv-2.dll')) { Write-Host 'libmpv already there.'; return }

$api = 'https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases'
try { $release = Invoke-RestMethod "$api/tags/$pinned" }
catch { Write-Host "Release $pinned is gone, using latest"; $release = Invoke-RestMethod "$api/latest" }

$asset = $release.assets | Where-Object { $_.name -like 'mpv-dev-x86_64-2*.7z' } | Select-Object -First 1
if (-not $asset) { throw 'No mpv-dev x86_64 build in that release' }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("mpv-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
try {
    Write-Host "Downloading $($asset.name)"
    Invoke-WebRequest $asset.browser_download_url -OutFile (Join-Path $tmp 'mpv.7z')
    New-Item -ItemType Directory -Force $dest | Out-Null
    # Windows' tar (libarchive) can read 7z
    tar -xf (Join-Path $tmp 'mpv.7z') -C $dest libmpv-2.dll include/mpv/client.h
    if ($LASTEXITCODE -ne 0) { throw 'Extract failed' }
    Write-Host 'Done.'
}
finally { Remove-Item -Recurse -Force $tmp }
