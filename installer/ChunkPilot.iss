#define MyAppName "ChunkPilot"
#define MyAppVersion "1.3.0"
#ifndef MyReleaseTag
  #define MyReleaseTag "v1.3.0-alpha.1"
#endif
#define MyAppPublisher "ChunkPilot"
#define MyAppExeName "ChunkPilot.exe"

[Setup]
AppId={{C609C59D-FD5A-4A18-91C8-2D04F7177A69}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\ChunkPilot
DefaultGroupName=ChunkPilot
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=output
OutputBaseFilename=ChunkPilot-Setup-{#MyReleaseTag}
SetupIconFile=..\assets\ChunkPilot.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupArchitecture=x64
AppMutex=Local\ChunkPilot.App,Local\ChunkPilot.Agent
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Guided local Minecraft server launcher and manager
VersionInfoCopyright=Copyright (c) 2026 ChunkPilot
MinVersion=10.0.17763

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\artifacts\self-contained-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "prerequisites\MicrosoftEdgeWebview2Setup.exe"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\ChunkPilot\ChunkPilot"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\ChunkPilot.ico"; Comment: "Open ChunkPilot"; AppUserModelID: "ChunkPilot.Desktop"
Name: "{autoprograms}\ChunkPilot\ChunkPilot WebUI Preview"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--webui-preview"; IconFilename: "{app}\Assets\ChunkPilot.ico"; Comment: "Open the preview WebUI (not the default interface)"; AppUserModelID: "ChunkPilot.Desktop.WebUiPreview"
Name: "{autodesktop}\ChunkPilot"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\ChunkPilot.ico"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "ChunkPilot"; Flags: deletevalue uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch ChunkPilot"; Flags: nowait postinstall skipifsilent

[Code]
const
  WebView2ClientId = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

var
  RemoveSettingsCheck: TNewCheckBox;
  RemoveCacheCheck: TNewCheckBox;
  RemoveLogsCheck: TNewCheckBox;
  RemoveManagedCheck: TNewCheckBox;
  RemoveBackupsCheck: TNewCheckBox;
  UninstallWarning: TNewStaticText;

function IsUsableWebViewVersion(const Version: String): Boolean;
begin
  Result := (Version <> '') and (Version <> '0.0.0.0');
end;

function IsWebView2RuntimeInstalled(): Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKLM64,
      'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + WebView2ClientId,
      'pv', Version) and IsUsableWebViewVersion(Version)) or
    (RegQueryStringValue(HKCU,
      'Software\Microsoft\EdgeUpdate\Clients\' + WebView2ClientId,
      'pv', Version) and IsUsableWebViewVersion(Version));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Bootstrapper: String;
begin
  Result := '';
  if IsWebView2RuntimeInstalled() then
    exit;

  WizardForm.StatusLabel.Caption :=
    'Installing the Microsoft Edge WebView2 Runtime required by the WebUI preview...';
  ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
  Bootstrapper := ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe');
  if not Exec(Bootstrapper, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := 'Windows could not start the official Microsoft Edge WebView2 Runtime installer.';
    exit;
  end;
  if (ResultCode <> 0) or (not IsWebView2RuntimeInstalled()) then
    Result := 'The Microsoft Edge WebView2 Runtime could not be installed. ' +
      'Check the installer log and network access, then retry. Error code: ' + IntToStr(ResultCode) + '.';
end;

procedure InitializeUninstallProgressForm;
var
  TopPosition: Integer;
begin
  { Silent automation cannot make cleanup choices, so preserve every data category and avoid
    constructing interactive controls on the hidden uninstall form. }
  if UninstallSilent then
    exit;

  UninstallProgressForm.Height := UninstallProgressForm.Height + ScaleY(210);
  UninstallProgressForm.InnerNotebook.Height := UninstallProgressForm.InnerNotebook.Height + ScaleY(210);
  TopPosition := UninstallProgressForm.StatusLabel.Top + ScaleY(42);

  UninstallWarning := TNewStaticText.Create(UninstallProgressForm);
  UninstallWarning.Parent := UninstallProgressForm.InstallingPage;
  UninstallWarning.Left := 0;
  UninstallWarning.Top := TopPosition;
  UninstallWarning.Width := UninstallProgressForm.InstallingPage.ClientWidth;
  UninstallWarning.Height := ScaleY(38);
  UninstallWarning.AutoSize := False;
  UninstallWarning.WordWrap := True;
  UninstallWarning.Caption :=
    'Optional data cleanup. Managed servers and backups are preserved by default. Imported external servers are never deleted.';

  RemoveSettingsCheck := TNewCheckBox.Create(UninstallProgressForm);
  RemoveSettingsCheck.Parent := UninstallProgressForm.InstallingPage;
  RemoveSettingsCheck.Left := 0;
  RemoveSettingsCheck.Top := TopPosition + ScaleY(44);
  RemoveSettingsCheck.Width := UninstallProgressForm.InstallingPage.ClientWidth;
  RemoveSettingsCheck.Caption := 'Remove settings and activity history: ' + ExpandConstant('{localappdata}\ChunkPilot\chunkpilot.db');
  RemoveSettingsCheck.Checked := False;

  RemoveCacheCheck := TNewCheckBox.Create(UninstallProgressForm);
  RemoveCacheCheck.Parent := UninstallProgressForm.InstallingPage;
  RemoveCacheCheck.Left := 0;
  RemoveCacheCheck.Top := TopPosition + ScaleY(68);
  RemoveCacheCheck.Width := UninstallProgressForm.InstallingPage.ClientWidth;
  RemoveCacheCheck.Caption := 'Remove cached downloads and temporary staging: ' + ExpandConstant('{localappdata}\ChunkPilot\Cache, Staging, Shares');
  RemoveCacheCheck.Checked := False;

  RemoveLogsCheck := TNewCheckBox.Create(UninstallProgressForm);
  RemoveLogsCheck.Parent := UninstallProgressForm.InstallingPage;
  RemoveLogsCheck.Left := 0;
  RemoveLogsCheck.Top := TopPosition + ScaleY(92);
  RemoveLogsCheck.Width := UninstallProgressForm.InstallingPage.ClientWidth;
  RemoveLogsCheck.Caption := 'Remove ChunkPilot logs: ' + ExpandConstant('{localappdata}\ChunkPilot\Logs');
  RemoveLogsCheck.Checked := False;

  RemoveManagedCheck := TNewCheckBox.Create(UninstallProgressForm);
  RemoveManagedCheck.Parent := UninstallProgressForm.InstallingPage;
  RemoveManagedCheck.Left := 0;
  RemoveManagedCheck.Top := TopPosition + ScaleY(116);
  RemoveManagedCheck.Width := UninstallProgressForm.InstallingPage.ClientWidth;
  RemoveManagedCheck.Caption := 'DANGER - remove default managed server instances: ' + ExpandConstant('{userprofile}\ChunkPilot\Servers');
  RemoveManagedCheck.Checked := False;

  RemoveBackupsCheck := TNewCheckBox.Create(UninstallProgressForm);
  RemoveBackupsCheck.Parent := UninstallProgressForm.InstallingPage;
  RemoveBackupsCheck.Left := 0;
  RemoveBackupsCheck.Top := TopPosition + ScaleY(140);
  RemoveBackupsCheck.Width := UninstallProgressForm.InstallingPage.ClientWidth;
  RemoveBackupsCheck.Caption := 'DANGER - remove backup archives: ' + ExpandConstant('{localappdata}\ChunkPilot\Backups');
  RemoveBackupsCheck.Checked := False;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDataRoot: String;
begin
  if CurUninstallStep <> usUninstall then
    exit;

  AppDataRoot := ExpandConstant('{localappdata}\ChunkPilot');
  if Assigned(RemoveSettingsCheck) and RemoveSettingsCheck.Checked then
  begin
    DeleteFile(AppDataRoot + '\chunkpilot.db');
    DeleteFile(AppDataRoot + '\chunkpilot.db-shm');
    DeleteFile(AppDataRoot + '\chunkpilot.db-wal');
    DeleteFile(AppDataRoot + '\secrets.dat');
  end;
  if Assigned(RemoveCacheCheck) and RemoveCacheCheck.Checked then
  begin
    DelTree(AppDataRoot + '\Cache', True, True, True);
    DelTree(AppDataRoot + '\Staging', True, True, True);
    DelTree(AppDataRoot + '\Shares', True, True, True);
  end;
  if Assigned(RemoveLogsCheck) and RemoveLogsCheck.Checked then
  begin
    DelTree(AppDataRoot + '\Logs', True, True, True);
    DelTree(AppDataRoot + '\DiagnosticBundles', True, True, True);
  end;
  if Assigned(RemoveManagedCheck) and RemoveManagedCheck.Checked then
    DelTree(ExpandConstant('{userprofile}\ChunkPilot\Servers'), True, True, True);
  if Assigned(RemoveBackupsCheck) and RemoveBackupsCheck.Checked then
    DelTree(AppDataRoot + '\Backups', True, True, True);
end;
