; Hindsight installer (Inno Setup 6).
; Build: iscc /DAppVersion=0.1.0 /DSourceDir=..\artifacts\publish\win-x64 /DOutputDir=..\artifacts\release installer\Hindsight.iss
; The release workflow (.github/workflows/release.yml) runs this on GitHub Actions.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif

#define AppName "Hindsight"
#define AppExe "Hindsight.exe"
#define AppUrl "https://hindsight-clips.vercel.app"
#define RepoUrl "https://github.com/efebalci569-dot/hindsight"

[Setup]
AppId={{B6F3C2A4-5D7E-4F81-9C2B-7A3E1D4F6B90}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Hindsight contributors
AppPublisherURL={#AppUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Hindsight setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user install by default (no administrator rights); "install for all users" is offered.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1903+ (Windows Graphics Capture)
MinVersion=10.0.18362
OutputDir={#OutputDir}
OutputBaseFilename=HindsightSetup-{#AppVersion}
SetupIconFile=..\src\Hindsight.App\Resources\hindsight.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
CloseApplications=force
RestartApplications=no

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
turkish.DesktopIcon=Masaüstü kısayolu oluştur
english.DesktopIcon=Create a desktop shortcut
turkish.Autostart=Windows açılınca Hindsight'ı tepside başlat (önerilen)
english.Autostart=Start Hindsight in the tray with Windows (recommended)
turkish.LaunchApp=Hindsight'ı başlat
english.LaunchApp=Launch Hindsight

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked
Name: "autostart"; Description: "{cm:Autostart}"

[Files]
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SourceDir}\ffmpeg\*"; DestDir: "{app}\ffmpeg"; Flags: ignoreversion recursesubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Same per-user Run entry the app's own "Start with Windows" switch manages.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Hindsight"; ValueData: """{app}\{#AppExe}"" --minimized"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "StopHindsight"

[UninstallDelete]
; The replay buffer is a temporary cache; clips in Videos\Hindsight are never touched.
Type: filesandordirs; Name: "{localappdata}\Hindsight\buffer"
Type: filesandordirs; Name: "{localappdata}\Hindsight\thumbs"
