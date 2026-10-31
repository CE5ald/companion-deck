; Companion Deck installer. Built by installer\Build-Installer.ps1, which publishes the program first.
; Everything the PC side needs is here: the program carries its own .NET, ADB is fetched from Google and
; checked, and the Android app travels inside so it can be put on the tablet from the same wizard.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<folder holding pub\, CompanionDeck.apk and the licence files>
#endif

[Setup]
AppId={{6B3F6E57-8D0C-4B6F-9C47-2D5E0C7A1F11}
AppName=Companion Deck
AppVersion={#AppVersion}
AppVerName=Companion Deck {#AppVersion}
AppPublisher=CE5ald
AppPublisherURL=https://github.com/CE5ald/companion-deck
AppSupportURL=https://github.com/CE5ald/companion-deck/issues
DefaultDirName={autopf}\Companion Deck
DefaultGroupName=Companion Deck
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequiredOverridesAllowed=commandline
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
LicenseFile={#SourceDir}\LICENSE
UninstallDisplayIcon={app}\CompanionDeck.Host.exe
OutputBaseFilename=CompanionDeck-Setup-{#AppVersion}
CloseApplications=no
RestartApplications=no

[Tasks]
Name: "autostart"; Description: "Start Companion Deck when I sign in (recommended). It runs with the rights it needs for temperatures, fans, brightness and frame rates."; GroupDescription: "Set up:"
Name: "adb"; Description: "Download ADB from Google, for the USB cable connection (recommended; needs internet)"; GroupDescription: "Set up:"
Name: "firewall"; Description: "Allow the Wi-Fi connection through Windows Firewall (private networks only)"; GroupDescription: "Set up:"
Name: "tablet"; Description: "Install the Android app on a tablet that is plugged in now (USB debugging must be on)"; GroupDescription: "Set up:"
Name: "pawnio"; Description: "Install the PawnIO driver so CPU temperature and fans can be read (optional; from winget)"; GroupDescription: "Optional:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\pub\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#SourceDir}\CompanionDeck.apk"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\scripts\Post-Install.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "{#SourceDir}\scripts\Start-Deck.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "{#SourceDir}\scripts\Stop-Deck.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "{#SourceDir}\scripts\Tablet-Setup.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion

[Icons]
Name: "{group}\Companion Deck"; Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\scripts\Start-Deck.ps1"""; IconFilename: "{app}\CompanionDeck.Host.exe"; Comment: "Start Companion Deck"
Name: "{group}\Stop Companion Deck"; Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\scripts\Stop-Deck.ps1"""; IconFilename: "{app}\CompanionDeck.Host.exe"
Name: "{group}\Install app on tablet"; Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\Tablet-Setup.ps1"""; IconFilename: "{app}\CompanionDeck.Host.exe"; Comment: "Download ADB if needed and install the Android app on a plugged-in tablet"
Name: "{group}\What can this PC do"; Filename: "cmd.exe"; Parameters: "/k ""{app}\CompanionDeck.Host.exe"" --selftest"; IconFilename: "{app}\CompanionDeck.Host.exe"; Comment: "Shows which features this PC supports"
Name: "{group}\Android app (APK)"; Filename: "{app}"; Comment: "The folder holding CompanionDeck.apk, to copy to a tablet by hand"
Name: "{group}\Uninstall Companion Deck"; Filename: "{uninstallexe}"

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\scripts\Start-Deck.ps1"""; Description: "Start Companion Deck now"; Flags: postinstall nowait skipifsilent runhidden

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\Post-Install.ps1"" -AppDir ""{app}"" -Remove"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveCompanionDeck"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\platform-tools"
Type: files; Name: "{app}\install-summary.txt"

[Code]
// An update installs over a running copy: stop it first, or its files are locked.
procedure StopRunningCopy;
var
  rc: Integer;
  cmd: String;
begin
  // Only the copy installed in this folder; another copy elsewhere on the PC is not ours to stop.
  cmd := '$a = ''' + ExpandConstant('{app}') + '''; ' +
         '$t = Get-ScheduledTask -TaskName ''CompanionDeck Host'' -EA 0; ' +
         'if ($t -and ($t.Actions | Where-Object { $_.Execute -like ($a + ''*'') })) { Stop-ScheduledTask -TaskName ''CompanionDeck Host'' }; ' +
         'Get-Process CompanionDeck.Host -EA 0 | Where-Object { $_.Path -like ($a + ''*'') } | Stop-Process -Force';
  Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command "' + cmd + '"', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(800);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningCopy;
  Result := '';
end;

// After the files are in place: run the steps the person ticked, then say plainly what did not work.
procedure CurStepChanged(CurStep: TSetupStep);
var
  rc: Integer;
  params, summary: String;
  text: AnsiString;
begin
  if CurStep <> ssPostInstall then exit;

  params := '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + ExpandConstant('{app}\scripts\Post-Install.ps1') + '" -AppDir "' + ExpandConstant('{app}') + '"';
  if WizardIsTaskSelected('adb') then params := params + ' -Adb';
  if WizardIsTaskSelected('autostart') then params := params + ' -Autostart';
  if WizardIsTaskSelected('firewall') then params := params + ' -Firewall';
  if WizardIsTaskSelected('pawnio') then params := params + ' -PawnIO';
  if WizardIsTaskSelected('tablet') then params := params + ' -Tablet';

  WizardForm.StatusLabel.Caption := 'Setting up (downloading ADB and installing on the tablet can take a minute)...';
  Exec('powershell.exe', params, '', SW_HIDE, ewWaitUntilTerminated, rc);

  summary := ExpandConstant('{app}\install-summary.txt');
  if (not WizardSilent) and FileExists(summary) then
  begin
    if LoadStringFromFile(summary, text) then
      if Pos('PROBLEM:', String(text)) > 0 then
        MsgBox('Companion Deck is installed, but some steps need your attention:' + #13#10#13#10 + String(text), mbInformation, MB_OK);
  end;
end;
