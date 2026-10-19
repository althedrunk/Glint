param([string]$Version, [string]$InnoCompiler = $env:INNO_SETUP_COMPILER)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\Glint\Glint.csproj'
if (-not $Version) { $Version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw 'Invalid release version' }
if (-not $InnoCompiler) {
    $InnoCompiler = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler)) {
    throw 'Inno Setup 7 is required. Pass -InnoCompiler with the full path to ISCC.exe.'
}

$releases = Join-Path $repo 'dist\releases'
$zipName = "Glint-$Version-win-x64-Portable.zip"
$setupName = "Glint-$Version-win-x64-Setup.exe"
$sumsName = "Glint-$Version-SHA256SUMS.txt"
foreach ($name in @($zipName, $setupName, $sumsName)) {
    if (Test-Path -LiteralPath (Join-Path $releases $name)) { throw "Release file already exists: $name" }
}
$stage = Join-Path $releases ('build-' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $stage 'publish'
$package = Join-Path $stage 'Glint'
$docs = Join-Path $package 'Docs'
New-Item -ItemType Directory -Path $docs -Force | Out-Null

dotnet publish $project -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -p:ContinuousIntegrationBuild=true -p:IncludeMpv=false -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true "-p:Version=$Version" -o $publish -nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
$looseFiles = Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object {
    $_.FullName -ne (Join-Path $publish 'Glint.exe') -and $_.Extension -ne '.pdb'
}
if ($looseFiles) { throw 'Single-file publish left supporting files outside Glint.exe' }

Copy-Item -LiteralPath (Join-Path $publish 'Glint.exe') -Destination $package

Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $docs
Copy-Item -LiteralPath (Join-Path $repo 'THIRD-PARTY-NOTICES.md') -Destination $docs
Copy-Item -LiteralPath (Join-Path $repo 'docs\release-readme.txt') -Destination (Join-Path $docs 'START-HERE.txt')
$innoNotices = Join-Path $docs 'licenses\inno-setup'
New-Item -ItemType Directory -Path $innoNotices -Force | Out-Null
Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $InnoCompiler) 'license.txt') -Destination $innoNotices

$assets = Get-Content (Join-Path $repo 'src\Glint\obj\project.assets.json') -Raw | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
$manifest = @()
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    $relative = $library.Value.path.Replace('/', '\')
    $packagePath = $packageRoots | ForEach-Object { Join-Path $_ $relative } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $packagePath) { throw "Missing restored package: $($library.Name)" }
    $notices = Join-Path $docs ('licenses\' + $relative)
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

$framework = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1
$runtimeConfig = Get-Content (Join-Path $repo "src\Glint\bin\Release\$framework\win-x64\Glint.runtimeconfig.json") -Raw | ConvertFrom-Json
$runtimeVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
$runtimePackage = $packageRoots | ForEach-Object { Join-Path $_ "microsoft.netcore.app.runtime.win-x64\$runtimeVersion" } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $runtimePackage) { throw 'Cannot locate the bundled .NET runtime notices' }
$runtimeNotices = Join-Path $docs 'licenses\dotnet-runtime'
New-Item -ItemType Directory -Path $runtimeNotices -Force | Out-Null
Get-ChildItem -LiteralPath $runtimePackage -File | Where-Object { $_.Name -match '(?i)license|notice' } | Copy-Item -Destination $runtimeNotices
$manifest += [ordered]@{ Package = "Microsoft.NETCore.App.Runtime.win-x64/$runtimeVersion" }
$manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $docs 'licenses\packages.json') -Encoding UTF8
$revision = git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not identify the release source revision' }
$dirty = [bool](git -C $repo status --porcelain --untracked-files=normal)
[ordered]@{ Version = $Version; SourceRevision = $revision; UncommittedChanges = $dirty; Architecture = 'win-x64'; DotnetRuntime = $runtimeVersion } |
    ConvertTo-Json | Set-Content (Join-Path $docs 'release.json') -Encoding UTF8

$unexpected = Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object {
    $_.Name -match '^(settings(?:\..*)?\.json|glint\.log|libmpv-2\.dll)$' -or $_.Extension -in @('.pdb', '.user')
}
if ($unexpected) { throw 'The package contains a local setting, log, player library or debug file' }

& $InnoCompiler "/DAppVersion=$Version" "/DPackageDir=$package" "/DReleaseDir=$stage" (Join-Path $PSScriptRoot 'Glint.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }

New-Item -ItemType Directory -Path (Join-Path $package 'Data') | Out-Null
'Keep this file to store settings and downloads beside Glint.exe.' |
    Set-Content (Join-Path $package 'Data\portable.flag') -Encoding ASCII
$zip = Join-Path $stage $zipName
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($package, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$sums = foreach ($name in @($zipName, $setupName)) {
    $checksum = (Get-FileHash -LiteralPath (Join-Path $stage $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$checksum  $name"
}
$sums | Set-Content (Join-Path $stage $sumsName) -Encoding ASCII
foreach ($name in @($zipName, $setupName, $sumsName)) {
    Move-Item -LiteralPath (Join-Path $stage $name) -Destination (Join-Path $releases $name)
    Write-Host "Release file: $(Join-Path $releases $name)"
}
