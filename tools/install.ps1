# Builds Glint (Release, ReadyToRun) and installs it to %LOCALAPPDATA%\Programs\Glint,
# with Desktop + Start Menu shortcuts. Re-run any time to update.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Glint\Glint.csproj'
$publish = Join-Path $root 'dist'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Glint'

Write-Host 'Publishing...'
dotnet publish $project -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o $publish -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

Get-Process Glint -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$installDir*" } | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host "Installing to $installDir"
New-Item -ItemType Directory -Force $installDir | Out-Null
# Debug symbols and the WinAppSDK ML runtime (unused by Glint) are dead weight.
robocopy $publish $installDir /MIR /XF *.pdb onnxruntime.dll DirectML.dll /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copy failed ($LASTEXITCODE)" }
Get-ChildItem $installDir -Include *.pdb, onnxruntime.dll, DirectML.dll -Recurse -File | Remove-Item -Force

$exe = Join-Path $installDir 'Glint.exe'
$shell = New-Object -ComObject WScript.Shell
$targets = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Glint.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'Glint.lnk')
)
foreach ($lnk in $targets) {
    # Recreate rather than overwrite, otherwise Explorer keeps showing a cached old icon
    Remove-Item $lnk -ErrorAction SilentlyContinue
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $installDir
    $s.IconLocation = "$exe,0"
    $s.Description = 'Glint - fast Twitch client with 7TV'
    $s.Save()
    Write-Host "Shortcut: $lnk"
}
# Ask Explorer to refresh its icon cache so an updated icon shows up straight away
& "$env:SystemRoot\System32\ie4uinit.exe" -show
Write-Host 'Done.'
