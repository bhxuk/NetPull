; NetPull installer (Inno Setup 6)
; Built automatically by GitHub Actions. To build by hand:
;   iscc /DMyAppVersion=1.0.0 /DPublishDir=..\publish installer\NetPull.iss

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

#define MyAppName "NetPull"
#define MyAppExe "NetPull.exe"
#define MyAppPublisher "bhxuk"
#define MyAppURL "https://github.com/bhxuk/NetPull"

[Setup]
AppId={{7B3E2A91-5C4D-4F0E-9A2B-6D18E0C4F3A7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; NetPull reads Windows' network events, which needs admin rights. Installing sets up a task so it never asks again.
PrivilegesRequired=admin
OutputDir=..\dist
OutputBaseFilename=NetPull-Setup-{#MyAppVersion}
SetupIconFile=..\src\NetPull\Assets\netpull.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
CloseApplications=no
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\{#MyAppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autoprograms}\{#MyAppName}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Open NetPull now"; Flags: nowait postinstall skipifsilent runascurrentuser
Filename: "{app}\{#MyAppExe}"; Parameters: "--background"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#MyAppExe}"; Flags: runhidden; RunOnceId: "StopNetPull"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""NetPull"" /F"; Flags: runhidden; RunOnceId: "RemoveTask"
Filename: "{sys}\logman.exe"; Parameters: "stop ""NetPull Network Trace"" -ets"; Flags: runhidden; RunOnceId: "StopTrace"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  { Close a running copy so its files can be replaced. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#MyAppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(600);
  Result := '';
end;
