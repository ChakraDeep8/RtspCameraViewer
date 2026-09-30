; Installer for RTSP Camera Viewer.
;
; Built with Inno Setup (iscc). Use installer\build-installer.ps1, which publishes the app first
; and passes the version in — compiling this file on its own installs whatever happens to be
; sitting in the publish folder.
;
; Design notes, since the shape of this is deliberate:
;
; * PrivilegesRequired=lowest. The app is installed for the CURRENT USER, under
;   %LocalAppData%\Programs, which is what makes the install genuinely one click: no UAC prompt,
;   no administrator, nothing for the person at the counter to approve or to go and ask for. The
;   app writes its camera list to %AppData% and needs no privileged location.
;
; * The directory, start-menu and "ready to install" pages are all disabled. There is one
;   meaningful choice (the two shortcuts) and it is on the first page with sensible defaults, so
;   the whole flow is Next, Install, done.
;
; * The camera list lives in %AppData%\RtspCameraViewer and is deliberately NOT removed on
;   uninstall. Reinstalling or upgrading therefore keeps every camera, class, window layout and
;   picture adjustment. Someone who truly wants it gone can delete that folder.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

#define AppName "RTSP Camera Viewer"
#define AppExeName "RtspCameraViewer.exe"
#define AppPublisher "ChakraDeep8"
#define AppUrl "https://github.com/ChakraDeep8/RtspCameraViewer"

[Setup]
; Never change AppId: it is how Windows recognises an existing install as the same product and
; upgrades it in place instead of leaving two copies side by side.
AppId={{8F3C21D4-6B7E-4A19-9C42-5E0D8A7B1F63}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

DefaultDirName={autopf}\RtspCameraViewer
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

; Per-user install: no administrator, and therefore no UAC prompt.
PrivilegesRequired=lowest

; x64 only, matching the win-x64 publish.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; One-click flow: the only page with a decision on it is Tasks.
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableWelcomePage=yes
ShowLanguageDialog=no

; The payload is a self-contained .NET build plus LibVLC, so it is large and worth compressing
; hard. Solid compression matters here: several hundred small runtime DLLs share a lot.
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes

; Shut the app down if it is running, rather than failing on a locked file. This app is normally
; left running on a display, so an upgrade meeting a running copy is the NORMAL case, not an edge.
CloseApplications=yes
RestartApplications=no

WizardStyle=modern
OutputDir=..\dist
OutputBaseFilename=RtspCameraViewer-Setup-v{#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startupicon"; Description: "Start automatically when I sign in"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
; The whole publish tree. Recursesubdirs picks up the LibVLC plugins folder, which is several
; hundred files and is what the app actually decodes RTSP with — an install missing it starts and
; then fails on every camera.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startupicon

[Run]
; skipifsilent so an unattended install does not pop a window on someone's screen.
Filename: "{app}\{#AppExeName}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only what the app generates inside its own install folder. The camera list in %AppData% is
; left alone on purpose — see the note at the top.
Type: filesandordirs; Name: "{app}\logs"
