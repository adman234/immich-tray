; Inno Setup script for Immich Tray.
; Build:  iscc /DAppVersion=1.2.3 /DSourceDir=..\publish installer\ImmichTray.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{9C3B5E1A-6F0D-4C5B-A1E2-5D7E8F9A0B1C}
AppName=Immich Tray
AppVersion={#AppVersion}
AppVerName=Immich Tray {#AppVersion}
AppPublisher=adman234
AppPublisherURL=https://github.com/adman234/immich-tray
AppSupportURL=https://github.com/adman234/immich-tray/issues
AppUpdatesURL=https://github.com/adman234/immich-tray/releases
; Per-user install: no admin prompt, and in-app updates never need elevation.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Immich Tray
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=..\dist
OutputBaseFilename=ImmichTray-Setup-{#AppVersion}
SetupIconFile=..\src\ImmichTray\Assets\immich-tray.ico
UninstallDisplayIcon={app}\ImmichTray.exe
UninstallDisplayName=Immich Tray
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Immich Tray"; Filename: "{app}\ImmichTray.exe"

[Run]
; No skipifsilent: after a silent in-app update the app starts again by itself.
Filename: "{app}\ImmichTray.exe"; Description: "Start Immich Tray now"; Flags: nowait postinstall

[UninstallRun]
; Stops the app, removes the Immich folder from Explorer, and deletes its placeholders and downloads.
Filename: "{app}\ImmichTray.exe"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveImmichFolder"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\ImmichTray\updates"

[Code]
// Stop a running copy before files are replaced (in-app updates already exit first).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Exe: String;
begin
  Exe := ExpandConstant('{app}\ImmichTray.exe');
  if FileExists(Exe) then
    Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
