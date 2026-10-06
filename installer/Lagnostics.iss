#define AppName "Lagnostics"
#define AppVersion GetEnv("APP_VERSION")
#define PublishDir GetEnv("PUBLISH_DIR")

[Setup]
AppId={{A34CE2B0-DC52-4FBB-90E0-D4E8294ED83A}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisherURL=https://github.com/FedericoRocca/diagnostico_lag
DefaultDirName={localappdata}\Programs\Lagnostics
DefaultGroupName=Lagnostics
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayName=Lagnostics
UninstallDisplayIcon={app}\DiagnosticoLag.exe
SetupIconFile=..\DiagnosticoLag\Assets\diagnostico-lag.ico
OutputDir=..\artifacts\installer
OutputBaseFilename=DiagnosticoLag-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Lagnostics"; Filename: "{app}\DiagnosticoLag.exe"
Name: "{autodesktop}\Lagnostics"; Filename: "{app}\DiagnosticoLag.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\DiagnosticoLag.exe"; Description: "{cm:LaunchProgram,Lagnostics}"; Flags: nowait postinstall skipifsilent
