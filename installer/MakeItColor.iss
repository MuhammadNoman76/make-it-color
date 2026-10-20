; MakeItColor installer (Inno Setup 6). Built by build.ps1, which publishes each edition to ..\publish\<edition>.
;   /DEdition=x64     Windows 10 (1903+) / 11, 64-bit Intel/AMD
;   /DEdition=arm64   Windows 10 (1903+) / 11 on ARM
; The colour model (456 MB, or 912 MB for the CPU) is downloaded by the app on first use, not shipped here.

#define AppName "MakeItColor"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef Edition
  #define Edition "x64"
#endif
#define AppExe "MakeItColor.exe"
#define PublishDir "..\publish\" + Edition

[Setup]
; Same AppId for both editions: another edition simply replaces the current one. The updater looks up this key.
AppId={{2359B1AF-53A7-4F1C-8168-65B4042B82F4}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Muhammad Noman
AppPublisherURL=https://www.nomanshafiq.com/makeitcolor/
AppSupportURL=https://github.com/MuhammadNoman76/make-it-color
AppComments=Colours black-and-white photos and videos on your PC (NPU, then GPU, then CPU)
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=MakeItColor-Setup-{#AppVersion}-{#Edition}
SetupIconFile=..\src\MakeItColor\Assets\MakeItColor.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMANumBlockThreads=4
LZMAUseSeparateProcess=yes
; Windows ML (the AI runtime) needs Windows 10 version 1903 or newer.
MinVersion=10.0.18362
#if Edition == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#else
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#endif
; "Install for all users" by default; "only for me" needs no admin rights.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "openwith"; Description: "Add {#AppName} to ""Open with"" for photos and videos (never as the default app)"; GroupDescription: "Windows integration:"

[InstallDelete]
; Updating: remove the previous version's program files first, so nothing obsolete is left behind.
; Only in the app's own folder. Settings (%APPDATA%\MakeItColor) and the uninstaller (unins000.*) are never touched.
Type: filesandordirs; Name: "{app}\ffmpeg"; Check: IsOwnFolder
Type: files; Name: "{app}\*.dll"; Check: IsOwnFolder
Type: files; Name: "{app}\*.json"; Check: IsOwnFolder
Type: files; Name: "{app}\*.pdb"; Check: IsOwnFolder
Type: files; Name: "{app}\createdump.exe"; Check: IsOwnFolder
Type: files; Name: "{app}\{#AppExe}"; Check: IsOwnFolder

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; ---- Application registration: shows up in "Open with" for these types (not the default app)
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"",0"; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: openwith
#define OpenWith(Ext) "Root: HKA; Subkey: ""Software\Classes\" + Ext + "\OpenWithList\" + AppExe + """; ValueType: none; Flags: uninsdeletekey; Tasks: openwith"
#emit OpenWith(".jpg")
#emit OpenWith(".jpeg")
#emit OpenWith(".png")
#emit OpenWith(".tif")
#emit OpenWith(".tiff")
#emit OpenWith(".bmp")
#emit OpenWith(".webp")
#emit OpenWith(".heic")
#emit OpenWith(".mp4")
#emit OpenWith(".mov")
#emit OpenWith(".mkv")
#emit OpenWith(".avi")
#emit OpenWith(".wmv")
#emit OpenWith(".webm")
#emit OpenWith(".mpg")

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; The app's updater runs setup with /VERYSILENT ... /RELAUNCH=1: start it again afterwards
; (as the signed-in user, not elevated, even when setup asked for administrator rights).
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: RelaunchRequested

[UninstallDelete]
; The downloaded colour models and the NPU compile cache (hundreds of MB; downloaded again if reinstalled).
; Settings in %APPDATA%\MakeItColor stay.
Type: filesandordirs; Name: "{localappdata}\MakeItColor"

[Code]
// /RELAUNCH=1 on the command line (passed by the app's updater).
function RelaunchRequested(): Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

// True when the target folder is MakeItColor's own (it holds MakeItColor.exe), so cleaning it is safe.
function IsOwnFolder(): Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\{#AppExe}'));
end;
