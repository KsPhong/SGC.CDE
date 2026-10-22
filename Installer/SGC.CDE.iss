; ══════════════════════════════════════════════════════════════════════════
;  SGC CDE Studio — Inno Setup Script
;  Bộ cài đặt Desktop SGC CDE Studio (ACC · BIM 360 Workspace)
; ══════════════════════════════════════════════════════════════════════════

#define AppName        "SGC CDE Studio"
#define AppVersion     "1.1.0"
#define AppPublisher   "SGC"
#define AppExeName     "BIMUP.CDE.exe"
#define AppId          "{{E5497B8F-8C31-48C7-B91F-72A0D1591874}"
#define PublishDir     "Publish"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/KsPhong/SGC.CDE
DefaultDirName={localappdata}\Programs\BIMUP\CDE
DisableDirPage=yes
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=Output
OutputBaseFilename=SGC.CDE.Studio-Setup-v{#AppVersion}
PrivilegesRequired=lowest
CloseApplications=yes
RestartApplications=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
