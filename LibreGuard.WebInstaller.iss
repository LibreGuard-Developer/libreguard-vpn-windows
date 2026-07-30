; LibreGuard VPN website installer bootstrapper.
; Build this after running scripts\publish-vm-bundle.ps1 so the publish\release-bundle
; folder exists and contains the full app/service/installer payload.

#define MyAppName "LibreGuard VPN"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "LibreGuard d.o.o"
#define BundleRoot "publish\release-bundle"
#define BundleTempDir "{tmp}\LibreGuard VPN Bundle"
#define InnerInstallerPath BundleTempDir + "\installer\LibreGuard.Installer.exe"
#define ThirdPartyNoticesPath BundleRoot + "\licenses\THIRD-PARTY-NOTICES.txt"
#define BuildStamp GetDateTimeString('yyyymmdd-hhnnss', '', '')

[Setup]
AppId={{9D8FD8C2-2A9D-4B2D-9C4A-6F8B5F9E3F38}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\LibreGuard VPN Bootstrapper
DefaultGroupName={#MyAppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
Uninstallable=no
SignTool=certum
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern
SetupIconFile=LibreGuard VPN Desktop\Assets\Images\LibreGuard_logo_cropped_V3.ico
WizardSmallImageFile=LibreGuard VPN Desktop\Assets\Images\LibreGuard_logo_cropped_V3.png
WizardSmallImageBackColor=none
LicenseFile=LICENSE
InfoBeforeFile={#ThirdPartyNoticesPath}
OutputDir=output\installer-builds
OutputBaseFilename=LibreGuard-Setup-{#MyAppVersion}-{#BuildStamp}
Compression=lzma2/ultra64
SolidCompression=yes
ChangesAssociations=no
DisableStartupPrompt=yes
UsePreviousAppDir=no
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} bootstrapper
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startmenuicon"; Description: "Create a Start Menu shortcut"; GroupDescription: "Additional icons:"
Name: "desktopicon"; Description: "Create a Desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
Source: "{#BundleRoot}\*"; DestDir: "{#BundleTempDir}"; Flags: recursesubdirs createallsubdirs ignoreversion; Excludes: "installers\openvpn\OpenVPN-Community-amd64.msi"
Source: "LibreGuard VPN Desktop\Assets\Images\LibreGuard_logo_cropped_V3.ico"; DestDir: "{#BundleTempDir}\app"; Flags: ignoreversion
Source: "LibreGuard VPN Desktop\Assets\Images\LibreGuard_logo_finished_wizard.bmp"; Flags: dontcopy
Source: "installers\openvpn\OpenVPN-Community-amd64.msi"; DestDir: "{#BundleTempDir}\installers\openvpn"; Flags: ignoreversion; AfterInstall: RunInnerInstallerAfterPayloadCopy

[Icons]
Name: "{autodesktop}\{#MyAppName}"; Filename: "{code:GetInstalledAppExe}"; WorkingDir: "{code:GetInstalledAppDir}"; IconFilename: "{code:GetInstalledAppIcon}"; Tasks: desktopicon

[Run]
Filename: "{code:GetInstalledAppExe}"; Description: "Launch LibreGuard VPN Desktop"; Flags: postinstall nowait skipifsilent

[Code]
var
  NoticesPage: TWizardPage;
  ExistingInstallPage: TWizardPage;
  BlockedInstallPage: TWizardPage;
  AcceptNoticesCheckBox: TNewCheckBox;
  RepairReinstallCheckBox: TNewCheckBox;
  RemoveDataCheckBox: TNewCheckBox;
  TermsLink: TNewStaticText;
  PrivacyLink: TNewStaticText;
  FinishedLogoExtracted: Boolean;

function GetInstalledAppDir(Param: string): string;
begin
  Result := ExpandConstant('{autopf}\LibreGuard VPN\app');
end;

function GetInstalledAppExe(Param: string): string;
begin
  Result := ExpandConstant('{autopf}\LibreGuard VPN\app\LibreGuard VPN Desktop.exe');
end;

function GetInstalledAppIcon(Param: string): string;
begin
  Result := ExpandConstant('{autopf}\LibreGuard VPN\app\LibreGuard_logo_cropped_V3.ico');
end;

function GetInstalledAppProcessName: string;
begin
  Result := ExtractFileName(GetInstalledAppExe(''));
end;

function IsExistingInstallation: Boolean;
begin
  Result := FileExists(GetInstalledAppExe(''));
end;

function IsInstalledAppRunning: Boolean;
var
  ResultCode: Integer;
  PowerShellArgs: string;
  TargetPath: string;
  ProcessName: string;
begin
  Result := False;
  TargetPath := GetInstalledAppExe('');
  ProcessName := GetInstalledAppProcessName;

  StringChangeEx(TargetPath, '''', '''''', True);
  StringChangeEx(ProcessName, '''', '''''', True);

  PowerShellArgs :=
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' +
    '$target = ''' + TargetPath + '''; ' +
    '$name = ''' + ProcessName + '''; ' +
    '$match = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue ' +
    '| Where-Object { $_.Name -eq $name -and $_.ExecutablePath -and ([string]::Equals($_.ExecutablePath, $target, [System.StringComparison]::OrdinalIgnoreCase)) } ' +
    '| Select-Object -First 1; ' +
    'if ($match) { exit 0 } else { exit 1 }"';

  if Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    PowerShellArgs,
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    Result := ResultCode = 0;
  end;
end;

procedure EnsureFinishedLogoExtracted;
begin
  if FinishedLogoExtracted then
    Exit;

  ExtractTemporaryFile('LibreGuard_logo_finished_wizard.bmp');
  FinishedLogoExtracted := True;
end;

procedure OpenUrl(const Url: string);
var
  ResultCode: Integer;
begin
  ShellExec('', Url, '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
end;

procedure TermsLinkClick(Sender: TObject);
begin
  OpenUrl('https://libreguard.net/Terms');
end;

procedure PrivacyLinkClick(Sender: TObject);
begin
  OpenUrl('https://libreguard.net/Privacy');
end;

procedure InitializeWizard;
var
  NotesLabel: TNewStaticText;
  ExistingInstallLabel: TNewStaticText;
  BlockedInstallLabel: TNewStaticText;
begin
  NoticesPage := CreateCustomPage(
    wpLicense,
    'LibreGuard VPN Notices',
    'Please review and accept the service terms before continuing');

  NotesLabel := TNewStaticText.Create(NoticesPage);
  NotesLabel.Parent := NoticesPage.Surface;
  NotesLabel.Left := 0;
  NotesLabel.Top := 0;
  NotesLabel.Width := NoticesPage.SurfaceWidth;
  NotesLabel.Height := ScaleY(48);
  NotesLabel.AutoSize := False;
  NotesLabel.WordWrap := True;
  NotesLabel.Caption :=
    'The desktop app is licensed under GPLv2 or later. Separate service terms and privacy rules apply to LibreGuard VPN usage.';

  TermsLink := TNewStaticText.Create(NoticesPage);
  TermsLink.Parent := NoticesPage.Surface;
  TermsLink.Left := 0;
  TermsLink.Top := ScaleY(56);
  TermsLink.Caption := 'Terms of Service: https://libreguard.net/Terms';
  TermsLink.Font.Style := [fsUnderline];
  TermsLink.Font.Color := clBlue;
  TermsLink.Cursor := crHandPoint;
  TermsLink.OnClick := @TermsLinkClick;

  PrivacyLink := TNewStaticText.Create(NoticesPage);
  PrivacyLink.Parent := NoticesPage.Surface;
  PrivacyLink.Left := 0;
  PrivacyLink.Top := ScaleY(78);
  PrivacyLink.Caption := 'Privacy Policy: https://libreguard.net/Privacy';
  PrivacyLink.Font.Style := [fsUnderline];
  PrivacyLink.Font.Color := clBlue;
  PrivacyLink.Cursor := crHandPoint;
  PrivacyLink.OnClick := @PrivacyLinkClick;

  AcceptNoticesCheckBox := TNewCheckBox.Create(NoticesPage);
  AcceptNoticesCheckBox.Parent := NoticesPage.Surface;
  AcceptNoticesCheckBox.Left := 0;
  AcceptNoticesCheckBox.Top := ScaleY(110);
  AcceptNoticesCheckBox.Width := NoticesPage.SurfaceWidth;
  AcceptNoticesCheckBox.Caption := 'I agree to the Terms of Service and Privacy Policy.';

  if IsExistingInstallation then
  begin
    ExistingInstallPage := CreateCustomPage(
      NoticesPage.ID,
      'Existing installation detected',
      'Choose how to continue');

    ExistingInstallLabel := TNewStaticText.Create(ExistingInstallPage);
    ExistingInstallLabel.Parent := ExistingInstallPage.Surface;
    ExistingInstallLabel.Left := 0;
    ExistingInstallLabel.Top := 0;
    ExistingInstallLabel.Width := ExistingInstallPage.SurfaceWidth;
    ExistingInstallLabel.Height := ScaleY(48);
    ExistingInstallLabel.AutoSize := False;
    ExistingInstallLabel.WordWrap := True;
    ExistingInstallLabel.Caption :=
      'A LibreGuard VPN installation was found at:' + #13#10 +
      GetInstalledAppExe('') + #13#10#13#10 +
      'Select repair/reinstall to continue with the existing installation.';

    RepairReinstallCheckBox := TNewCheckBox.Create(ExistingInstallPage);
    RepairReinstallCheckBox.Parent := ExistingInstallPage.Surface;
    RepairReinstallCheckBox.Left := 0;
    RepairReinstallCheckBox.Top := ScaleY(72);
    RepairReinstallCheckBox.Width := ExistingInstallPage.SurfaceWidth;
    RepairReinstallCheckBox.Caption := 'Repair / reinstall the existing installation';
    RepairReinstallCheckBox.Checked := True;

    RemoveDataCheckBox := TNewCheckBox.Create(ExistingInstallPage);
    RemoveDataCheckBox.Parent := ExistingInstallPage.Surface;
    RemoveDataCheckBox.Left := 0;
    RemoveDataCheckBox.Top := ScaleY(100);
    RemoveDataCheckBox.Width := ExistingInstallPage.SurfaceWidth;
    RemoveDataCheckBox.Caption := 'Remove app preferences and account data before reinstalling';
    RemoveDataCheckBox.Checked := False;

    BlockedInstallPage := CreateCustomPage(
      ExistingInstallPage.ID,
      'Installation aborted',
      'LibreGuard VPN must be closed before setup can continue');

    BlockedInstallLabel := TNewStaticText.Create(BlockedInstallPage);
    BlockedInstallLabel.Parent := BlockedInstallPage.Surface;
    BlockedInstallLabel.Left := 0;
    BlockedInstallLabel.Top := 0;
    BlockedInstallLabel.Width := BlockedInstallPage.SurfaceWidth;
    BlockedInstallLabel.Height := ScaleY(120);
    BlockedInstallLabel.AutoSize := False;
    BlockedInstallLabel.WordWrap := True;
    BlockedInstallLabel.Caption :=
      'LibreGuard VPN is currently running.' + #13#10#13#10 +
      'Please close LibreGuard VPN manually before running repair/reinstall. If a VPN tunnel is active, close it from inside the app so LibreGuard can disconnect safely.' + #13#10#13#10 +
      'Setup will now exit. After the app is closed, run the installer again to continue.';
  end;

end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (BlockedInstallPage <> nil) and (CurPageID = BlockedInstallPage.ID) then
  begin
    WizardForm.NextButton.Enabled := False;
    WizardForm.BackButton.Enabled := False;
    WizardForm.CancelButton.Caption := 'Exit';
  end;

  if CurPageID = wpFinished then
  begin
    EnsureFinishedLogoExtracted;
    WizardForm.WizardBitmapImage.Bitmap.LoadFromFile(
      ExpandConstant('{tmp}\LibreGuard_logo_finished_wizard.bmp'));
    WizardForm.WizardBitmapImage2.Bitmap.LoadFromFile(
      ExpandConstant('{tmp}\LibreGuard_logo_finished_wizard.bmp'));
    WizardForm.WizardBitmapImage.Visible := True;
    WizardForm.WizardBitmapImage2.Visible := True;
    WizardForm.WizardSmallBitmapImage.Visible := False;
    WizardForm.WizardBitmapImage.Repaint;
    WizardForm.WizardBitmapImage2.Repaint;
    WizardForm.Repaint;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;

  if (BlockedInstallPage <> nil) and (PageID = BlockedInstallPage.ID) then
    Result := not IsInstalledAppRunning;
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if (BlockedInstallPage <> nil) and (CurPageID = BlockedInstallPage.ID) then
    Confirm := False;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = NoticesPage.ID then
  begin
    if not AcceptNoticesCheckBox.Checked then
    begin
      MsgBox(
        'You must accept the Terms of Service and Privacy Policy before continuing.',
        mbError, MB_OK);
      Result := False;
    end;
  end;

  if (ExistingInstallPage <> nil) and (CurPageID = ExistingInstallPage.ID) then
  begin
    if not RepairReinstallCheckBox.Checked then
    begin
      MsgBox(
        'Select repair/reinstall to continue.',
        mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function ShouldRemoveUserDataBeforeReinstall: Boolean;
begin
  Result := (ExistingInstallPage <> nil) and RemoveDataCheckBox.Checked;
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
begin
  Result := '';

  if IsExistingInstallation and IsInstalledAppRunning then
    Result :=
      'LibreGuard VPN is currently running.'#13#10#13#10 +
      'Please close LibreGuard VPN manually, then run the installer again.';
end;

function RunInnerInstaller: Boolean;
var
  ResultCode: Integer;
  Arguments: string;
begin
  if not FileExists(ExpandConstant('{#InnerInstallerPath}')) then
  begin
    MsgBox(
      'LibreGuard installer payload was not found.'#13#10#13#10 +
      'Expected: ' + ExpandConstant('{#InnerInstallerPath}') + #13#10#13#10 +
      'Make sure you ran scripts\publish-vm-bundle.ps1 first.',
      mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if not FileExists(ExpandConstant('{#BundleTempDir}\installers\openvpn\OpenVPN-Community-amd64.msi')) then
  begin
    MsgBox(
      'The OpenVPN MSI payload is missing from the installer package.'#13#10#13#10 +
      'Expected: ' + ExpandConstant('{#BundleTempDir}\installers\openvpn\OpenVPN-Community-amd64.msi') + #13#10#13#10 +
      'Make sure the MSI exists at installers\openvpn\OpenVPN-Community-amd64.msi before compiling the Inno script.',
      mbError, MB_OK);
    Result := False;
    Exit;
  end;

  Arguments := 'install --quiet --install-root "' + ExpandConstant('{#BundleTempDir}') + '"';
  if not WizardIsTaskSelected('startmenuicon') then
    Arguments := Arguments + ' --no-shortcuts';
  if ShouldRemoveUserDataBeforeReinstall then
    Arguments := Arguments + ' --clear-user-data';

  if not Exec(
    ExpandConstant('{#InnerInstallerPath}'),
    Arguments,
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    MsgBox('Failed to launch the LibreGuard installer.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if ResultCode <> 0 then
  begin
    MsgBox(
      'LibreGuard installation failed with exit code ' + IntToStr(ResultCode) + '.'#13#10#13#10 +
      'Check the logs under %ProgramData%\LibreGuard VPN\Logs\installer.log for details.',
      mbError, MB_OK);
    Result := False;
    Exit;
  end;

  Result := True;
end;

procedure RunInnerInstallerAfterPayloadCopy;
begin
  WizardForm.StatusLabel.Caption := 'Installing LibreGuard VPN components...';
  WizardForm.StatusLabel.Repaint;
  WizardForm.Repaint;

  if not RunInnerInstaller then
    Abort;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    EnsureFinishedLogoExtracted;
end;
