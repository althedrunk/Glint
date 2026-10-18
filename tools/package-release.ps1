param([string]$Version)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\Glint\Glint.csproj'
if (-not $Version) { $Version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw 'Invalid release version' }

$releases = Join-Path $repo 'dist\releases'
$stage = Join-Path $releases ('build-' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $stage 'publish'
$package = Join-Path $stage 'Glint'
New-Item -ItemType Directory -Path $package -Force | Out-Null

dotnet publish $project -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -p:ContinuousIntegrationBuild=true -p:IncludeMpv=false "-p:Version=$Version" -o $publish -nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object {
    $_.Extension -ne '.pdb' -and $_.Name -notin @('onnxruntime.dll', 'DirectML.dll', 'libmpv-2.dll')
} | ForEach-Object {
    $relative = $_.FullName.Substring($publish.Length).TrimStart('\')
    $destination = Join-Path $package $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination
}

Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $package
Copy-Item -LiteralPath (Join-Path $repo 'THIRD-PARTY-NOTICES.md') -Destination $package
Copy-Item -LiteralPath (Join-Path $repo 'docs\release-readme.txt') -Destination (Join-Path $package 'START-HERE.txt')

$assets = Get-Content (Join-Path $repo 'src\Glint\obj\project.assets.json') -Raw | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
$manifest = @()
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    $relative = $library.Value.path.Replace('/', '\')
    $packagePath = $packageRoots | ForEach-Object { Join-Path $_ $relative } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $packagePath) { throw "Missing restored package: $($library.Name)" }
    $notices = Join-Path $package ('licenses\' + $relative)
    New-Item -ItemType Directory -Path $notices -Force | Out-Null
    Get-ChildItem -LiteralPath $packagePath -Recurse -File | Where-Object {
        $_.Name -match '(?i)license|notice|copying' -or $_.FullName -match '[\\/]licenses[\\/]'
    } | ForEach-Object {
        $noticeRelative = $_.FullName.Substring($packagePath.Length).TrimStart('\')
        $destination = Join-Path $notices $noticeRelative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destination
    }
    Get-ChildItem -LiteralPath $packagePath -Filter '*.nuspec' -File | Copy-Item -Destination $notices
    $manifest += [ordered]@{ Package = $library.Name; Sha512 = $library.Value.sha512 }
}

$runtimeConfig = Get-Content (Join-Path $publish 'Glint.runtimeconfig.json') -Raw | ConvertFrom-Json
$runtimeVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
$runtimePackage = $packageRoots | ForEach-Object { Join-Path $_ "microsoft.netcore.app.runtime.win-x64\$runtimeVersion" } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $runtimePackage) { throw 'Cannot locate the bundled .NET runtime notices' }
$runtimeNotices = Join-Path $package 'licenses\dotnet-runtime'
New-Item -ItemType Directory -Path $runtimeNotices -Force | Out-Null
Get-ChildItem -LiteralPath $runtimePackage -File | Where-Object { $_.Name -match '(?i)license|notice' } | Copy-Item -Destination $runtimeNotices
$manifest += [ordered]@{ Package = "Microsoft.NETCore.App.Runtime.win-x64/$runtimeVersion" }
$manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $package 'licenses\packages.json') -Encoding UTF8
$revision = git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not identify the release source revision' }
[ordered]@{ Version = $Version; SourceRevision = $revision; Architecture = 'win-x64'; DotnetRuntime = $runtimeVersion } |
    ConvertTo-Json | Set-Content (Join-Path $package 'release.json') -Encoding UTF8

$unexpected = Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object {
    $_.Name -match '^(settings(?:\..*)?\.json|glint\.log|libmpv-2\.dll)$' -or $_.Extension -in @('.pdb', '.user')
}
if ($unexpected) { throw 'The package contains a local setting, log, player library or debug file' }

$zip = Join-Path $releases "Glint-$Version-win-x64.zip"
if (Test-Path -LiteralPath $zip) { throw "The release archive already exists: $zip" }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($package, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$checksum = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$checksum  $([IO.Path]::GetFileName($zip))" | Set-Content (Join-Path $releases "Glint-$Version-SHA256SUMS.txt") -Encoding ASCII
Write-Host "Release archive: $zip"
