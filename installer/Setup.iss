; Inno Setup 6 Script for Personal BA Transcriber
#define MyAppName "Personal BA Transcriber"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Mohammad Khan"
#define MyAppExeName "PersonalBATranscriber.App.exe"

[Setup]
AppId={{D9A24C65-F6B2-4E9A-A3BC-61D328C67B21}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\PersonalBATranscriber
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=PersonalBATranscriber-Setup-v1.0
Compression=lzma
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\PersonalBATranscriber\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
