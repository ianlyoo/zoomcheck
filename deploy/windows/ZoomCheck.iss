; ZoomCheck Windows installer.
; Payload is the self-contained ZoomCheck.Backend publish output: a single
; executable that hosts the web dashboard and opens the browser on start.
#define MyAppName "ZoomCheck"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#define MyAppPublisher "ianlyoo"
#define MyAppExeName "ZoomCheck.Backend.exe"
#define MyAppPackageDir "..\..\dist\windows\package"

#if !FileExists(SourcePath + MyAppPackageDir + "\" + MyAppExeName)
  #error Package payload not found. Run deploy\windows\build-installer.ps1 first.
#endif

[Setup]
AppId={{E7DB342B-E7B4-446B-8D41-29B3847CBAAF}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\ZoomCheck
DefaultGroupName=ZoomCheck
DisableProgramGroupPage=yes
OutputDir=..\..\dist\installer
OutputBaseFilename=ZoomCheck-Setup-x64
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Secrets, databases and user data are excluded here as a second line of
; defence; build-installer.ps1 already fails the build if any are present.
Source: "{#MyAppPackageDir}\*"; DestDir: "{app}"; Excludes: "*.db,*.db-shm,*.db-wal,*.sqlite,*.sqlite3,*.env,.env,.env.*,*.pfx,*.p12,*.pem,*.key,*.keystore,*.jks,secrets.json,*.secrets.json,appsettings.Development.json,appsettings.Local.json,*.log,*.xlsx,*.xls,*.csv,data\*,logs\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ZoomCheck"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\ZoomCheck"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch ZoomCheck dashboard"; Flags: nowait postinstall skipifsilent
