; XCloudSSD installer (Inno Setup 6)
; Build: ISCC /DAppVersion=1.0.0 /DSourceDir=<publish dir> /DWinFspMsi=<winfsp.msi> /DRepoDir=<repo root> XCloudSSD.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out\app"
#endif
#ifndef WinFspMsi
  #define WinFspMsi "..\deps\winfsp.msi"
#endif
#ifndef RepoDir
  #define RepoDir ".."
#endif

#define AppName "XCloudSSD"
#define ServiceName "XCloudSSDService"

[Setup]
AppId={{6E4B1C2A-7D35-4C1B-9A8E-3F0C2D5B7A91}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=XCloudSSD
AppPublisherURL=https://github.com/BxiiDN/XCloudSSD
DefaultDirName={autopf}\XCloudSSD
DefaultGroupName=XCloudSSD
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
OutputBaseFilename=XCloudSSD-Setup
SetupIconFile={#RepoDir}\src\XCloudSSD.Desktop\Assets\xcloudssd.ico
UninstallDisplayIcon={app}\XCloudSSD.exe
UninstallDisplayName=XCloudSSD
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
LicenseFile={#RepoDir}\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startapp"; Description: "Start the XCloudSSD window in the tray at sign-in"; GroupDescription: "Startup:"

[Files]
Source: "{#SourceDir}\XCloudSSD.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\XCloudSSDService.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoDir}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoDir}\docs\*.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "{#WinFspMsi}"; DestDir: "{tmp}"; DestName: "winfsp.msi"; Flags: deleteafterinstall

[Icons]
Name: "{group}\XCloudSSD"; Filename: "{app}\XCloudSSD.exe"
Name: "{group}\Uninstall XCloudSSD"; Filename: "{uninstallexe}"
Name: "{autodesktop}\XCloudSSD"; Filename: "{app}\XCloudSSD.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "XCloudSSD"; \
  ValueData: """{app}\XCloudSSD.exe"" --tray"; Flags: uninsdeletevalue; Tasks: startapp

[Run]
; 1) Filesystem driver (WinFsp) — installed only when missing.
Filename: "{sys}\msiexec.exe"; Parameters: "/i ""{tmp}\winfsp.msi"" /qn /norestart"; \
  StatusMsg: "Installing the WinFsp filesystem driver..."; Flags: runhidden waituntilterminated; Check: WinFspNeeded
; 2) Windows service: update an existing registration (upgrade) or create it (fresh install).
Filename: "{sys}\sc.exe"; Parameters: "config {#ServiceName} binPath= ""\""{app}\XCloudSSDService.exe\"""" start= auto DisplayName= ""XCloudSSD Service"""; \
  Flags: runhidden waituntilterminated; StatusMsg: "Registering XCloudSSDService..."
Filename: "{sys}\sc.exe"; Parameters: "create {#ServiceName} binPath= ""\""{app}\XCloudSSDService.exe\"""" start= auto DisplayName= ""XCloudSSD Service"""; \
  Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "description {#ServiceName} ""Mounts XCloudSSD cloud virtual disks and handles cloud transfers."""; Flags: runhidden waituntilterminated
; Service recovery: restart after a crash (5 s, 10 s, 30 s).
Filename: "{sys}\sc.exe"; Parameters: "failure {#ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start {#ServiceName}"; Flags: runhidden waituntilterminated; StatusMsg: "Starting XCloudSSDService..."
Filename: "{app}\XCloudSSD.exe"; Description: "{cm:LaunchProgram,XCloudSSD}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\cmd.exe"; Parameters: "/c ping -n 6 127.0.0.1 >nul"; Flags: runhidden waituntilterminated; RunOnceId: "WaitStop"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM XCloudSSD.exe /F"; Flags: runhidden waituntilterminated; RunOnceId: "KillApp"

[Code]
function WinFspNeeded(): Boolean;
var
  Dir: String;
begin
  Result := True;
  if RegQueryStringValue(HKLM32, 'SOFTWARE\WinFsp', 'InstallDir', Dir) then
    Result := not FileExists(AddBackslash(Dir) + 'bin\winfsp-x64.dll');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // Upgrade: stop the running service (it commits pending uploads first) and close the app.
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(4000);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM XCloudSSD.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
