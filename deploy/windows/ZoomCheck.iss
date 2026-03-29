#define MyAppName "ZoomCheck"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#define MyAppPublisher "ianlyoo"
#define MyAppExeName "ZoomCheck.App.exe"
#define MyAppAssocDir "..\..\dist\windows\package"

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

[Files]
Source: "{#MyAppAssocDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ZoomCheck"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\ZoomCheck"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch ZoomCheck"; Flags: nowait postinstall skipifsilent
