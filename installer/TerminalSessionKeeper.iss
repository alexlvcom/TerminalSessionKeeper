#ifndef AppVersion
#define AppVersion "1.3.0"
#endif

[Setup]
AppId={{8B6AE52F-34C1-4190-8C9B-67F75D271CE3}
AppName=Terminal Session Keeper
AppVersion={#AppVersion}
AppPublisher=alexlvcom
AppPublisherURL=https://github.com/alexlvcom/TerminalSessionKeeper
AppSupportURL=https://github.com/alexlvcom/TerminalSessionKeeper/issues
DefaultDirName={autopf}\Terminal Session Keeper
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=TerminalSessionKeeper-Setup
SetupIconFile=..\src\TerminalSessionKeeper\Resources\app.ico
UninstallDisplayIcon={app}\TerminalSessionKeeper.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
WizardImageFile=images\wizard-164x314.bmp,images\wizard-192x386.bmp,images\wizard-246x459.bmp,images\wizard-273x556.bmp,images\wizard-328x604.bmp,images\wizard-355x700.bmp,images\wizard-410x797.bmp
WizardSmallImageFile=images\wizard-small-55x55.bmp,images\wizard-small-64x68.bmp,images\wizard-small-83x80.bmp,images\wizard-small-92x97.bmp,images\wizard-small-110x106.bmp,images\wizard-small-119x123.bmp,images\wizard-small-138x140.bmp
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked
Name: startup; Description: "Start with Windows"; Flags: unchecked

[Files]
Source: "..\publish\installer\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{autoprograms}\TerminalSessionKeeper.lnk"
Type: files; Name: "{autodesktop}\TerminalSessionKeeper.lnk"
Type: files; Name: "{autostartup}\TerminalSessionKeeper.lnk"
Type: files; Name: "{userprograms}\TerminalSessionKeeper.lnk"
Type: files; Name: "{userdesktop}\TerminalSessionKeeper.lnk"
Type: files; Name: "{userstartup}\TerminalSessionKeeper.lnk"
Type: files; Name: "{commonprograms}\TerminalSessionKeeper.lnk"
Type: files; Name: "{commondesktop}\TerminalSessionKeeper.lnk"
Type: files; Name: "{commonstartup}\TerminalSessionKeeper.lnk"

[Icons]
Name: "{autoprograms}\Terminal Session Keeper"; Filename: "{app}\TerminalSessionKeeper.exe"
Name: "{autodesktop}\Terminal Session Keeper"; Filename: "{app}\TerminalSessionKeeper.exe"; Tasks: desktopicon
Name: "{autostartup}\Terminal Session Keeper"; Filename: "{app}\TerminalSessionKeeper.exe"; Tasks: startup

[Run]
Filename: "{app}\TerminalSessionKeeper.exe"; Flags: nowait shellexec; Check: RestartAfterUpgrade
Filename: "{app}\TerminalSessionKeeper.exe"; Description: "Start Terminal Session Keeper"; Flags: nowait postinstall skipifsilent shellexec; Check: OfferFirstLaunch

[Code]
var
  WasRunning: Boolean;

function RestartAfterUpgrade: Boolean;
begin
  Result := WasRunning;
end;

function OfferFirstLaunch: Boolean;
begin
  Result := not WasRunning;
end;

function StopApplication: Boolean;
var
  AppWindow: HWND;
  Attempt: Integer;
  ErrorCode: Integer;
begin
  Result := (not CheckForMutexes('TerminalSessionKeeper.Singleton')) and (not CheckForMutexes('Local\TerminalSessionKeeper.SingleInstance'));
  if Result then
    Exit;

  Log('Requesting application shutdown.');
  AppWindow := FindWindowByWindowName('TerminalSessionKeeperListener');
  if AppWindow <> 0 then
    PostMessage(AppWindow, $0010, 0, 0); { WM_CLOSE: clean exit }

  for Attempt := 1 to 50 do
  begin
    if (not CheckForMutexes('TerminalSessionKeeper.Singleton')) and (not CheckForMutexes('Local\TerminalSessionKeeper.SingleInstance')) then
    begin
      Result := True;
      Exit;
    end;
    Sleep(100);
  end;

  if CheckForMutexes('TerminalSessionKeeper.Singleton') or CheckForMutexes('Local\TerminalSessionKeeper.SingleInstance') then
  begin
    Exec('taskkill.exe', '/F /IM TerminalSessionKeeper.exe', '', SW_HIDE, ewWaitUntilTerminated, ErrorCode);
    Sleep(200);
  end;

  Result := (not CheckForMutexes('TerminalSessionKeeper.Singleton')) and (not CheckForMutexes('Local\TerminalSessionKeeper.SingleInstance'));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  WasRunning := CheckForMutexes('TerminalSessionKeeper.Singleton') or CheckForMutexes('Local\TerminalSessionKeeper.SingleInstance');
  if WasRunning and not StopApplication then
    Result := 'Terminal Session Keeper is running. Please close it before running setup.';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usAppMutexCheck then
  begin
    if not StopApplication then
      RaiseException('Terminal Session Keeper is running. Please close it before uninstalling.');
  end;
end;
