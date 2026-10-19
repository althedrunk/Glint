#ifndef AppVersion
  #error AppVersion must be supplied by package-release.ps1
#endif

[Setup]
AppId={{790034A5-DA6D-47B1-976A-AC43EF52B99C}
AppName=Glint
AppVersion={#AppVersion}
AppPublisher=althedrunk
AppPublisherURL=https://github.com/althedrunk/Glint
AppSupportURL=https://github.com/althedrunk/Glint/issues
DefaultDirName={localappdata}\Programs\Glint
DefaultGroupName=Glint
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#ReleaseDir}
OutputBaseFilename=Glint-{#AppVersion}-win-x64-Setup
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern dynamic
SetupIconFile=..\src\Glint\Assets\Glint.ico
UninstallDisplayIcon={app}\Glint.exe
DisableProgramGroupPage=auto
CloseApplications=yes
RestartApplications=no
LicenseFile=..\LICENSE

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PackageDir}\Glint.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\Docs\*"; DestDir: "{app}\Docs"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Glint"; Filename: "{app}\Glint.exe"
Name: "{autodesktop}\Glint"; Filename: "{app}\Glint.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Glint.exe"; Description: "Open Glint"; Flags: nowait postinstall skipifsilent

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\Data\portable.flag')) then
    Result := 'This folder contains a portable copy of Glint. Choose a different installation folder to keep its settings separate.';
end;
