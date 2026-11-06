; 12 island installer. Build it with scripts/Publish-Release.ps1, which passes AppVersion and SourceDir.
; The other defines exist so scripts/Test-AppUpdate.ps1 can install an isolated copy under its own identity.
;
; Install:   install mode (current user / all users) -> folder -> Install -> finished page with
;            "create desktop shortcut" and "run 12 island". The Start menu shortcut is always created.
; Update:    the installed version is uninstalled first (keeping data), then the new one is installed.
; Uninstall: one dialog with an unchecked "delete user data" box -> uninstall -> "uninstalled" message.

#ifndef AppVersion
  #error Define AppVersion, for example /DAppVersion=0.2.0
#endif
#ifndef SourceDir
  #error Define SourceDir as the folder produced by dotnet publish
#endif
; The Windows file version takes numbers only, so a prerelease label ("0.3.0-beta.1") is left out of it.
#if Pos("-", AppVersion) > 0
  #define FileVersion Copy(AppVersion, 1, Pos("-", AppVersion) - 1)
#else
  #define FileVersion AppVersion
#endif
; Must match GitHubUpdates.InstallerAppId. Never change it, or updates install a second copy.
#ifndef AppId
  #define AppId "9D9AD6ED-CF62-4527-A587-6DBE94A74B9E"
#endif
#ifndef AppName
  #define AppName "12 island"
#endif
#ifndef MainExe
  #define MainExe "12Island.exe"
#endif
; Settings, secrets and caches. Installing and updating never touch this folder; uninstalling deletes it on request.
#ifndef DataDir
  #define DataDir "{localappdata}\12Island"
#endif
; The app holds Local\12Island.Instance.<user name> while it runs (App.OnStartup).
#ifndef MutexPrefix
  #define MutexPrefix "12Island.Instance."
#endif
; Value the app writes under HKCU\...\Run for launch at startup (StartupRegistration).
#ifndef RunValue
  #define RunValue "12Island"
#endif
#ifndef AppUserModelId
  #define AppUserModelId "12Island.Desktop"
#endif
#ifndef Compression
  #define Compression "lzma2/max"
#endif
#define UninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\{" + AppId + "}_is1"

[Setup]
AppId={{{#AppId}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=12
AppPublisherURL=
AppSupportURL=
AppUpdatesURL=
AppCopyright=
VersionInfoVersion={#FileVersion}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#MainExe}
; Installs for the current user by default, so in-app updates run without an administrator prompt.
; The first dialog lets the user install for all users instead; later updates keep the chosen mode and folder.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
DefaultDirName={autopf}\{#AppName}
; Missing folders are created without asking. A reinstall starts at the previous folder and may choose another one,
; because the installed version is removed first (PrepareToInstall).
DisableDirPage=no
DisableReadyPage=yes
DisableProgramGroupPage=yes
; Match the Windows API target in TwelveIsland.csproj (Windows 10 version 2004).
MinVersion=10.0.19041
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern dynamic
SetupIconFile=..\src\12Island\Assets\12Island.ico
RestartApplications=no
; Writes "Setup Log <date> #nnn.txt" to %TEMP% for diagnosing failed installs and updates.
SetupLogging=yes
Compression={#Compression}
SolidCompression=yes
OutputBaseFilename=12Island-{#AppVersion}-Setup
; The language follows the Windows display language without asking: Chinese, Simplified or Traditional, shows
; Simplified Chinese and anything else English. A reinstall does not reuse the language of the earlier installer.
ShowLanguageDialog=no
UsePreviousLanguage=no

; English comes first, so a display language without a match falls back to it. Setup never matches a Traditional
; Chinese display language to a Simplified Chinese entry (it compares the code pages, 950 and 936), so "cht" carries
; Traditional Chinese's language ID and shows the Simplified Chinese texts.
[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "cht"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[LangOptions]
cht.LanguageID=$0404

; Entries without a prefix are the Chinese texts for chs and cht; the en. entries after them replace them in English.
[CustomMessages]
ExitFirstTitle=请先退出 {#AppName}
ExitFirstText={#AppName} 正在运行。请右键点击任务栏通知区域中的 {#AppName} 图标，选择“退出”，然后点击“重试”。
DesktopShortcut=创建桌面快捷方式
OldVersionNotRemoved=无法卸载已安装的旧版本 {#AppName}（代码 %1）。请在 Windows 设置的“应用”中卸载后，重新运行安装程序。
UninstallTitle=卸载 {#AppName}
UninstallQuestion=确定要卸载 {#AppName} 吗？
UninstallButton=卸载
DeleteUserData=删除用户数据
Uninstalled={#AppName} 已卸载。
en.ExitFirstTitle=Quit {#AppName} first
en.ExitFirstText={#AppName} is running. Right-click the {#AppName} icon in the notification area of the taskbar, choose "Exit", then click "Retry".
en.DesktopShortcut=Create a desktop shortcut
en.OldVersionNotRemoved=The installed version of {#AppName} could not be uninstalled (code %1). Uninstall it from Apps in Windows Settings, then run Setup again.
en.UninstallTitle=Uninstall {#AppName}
en.UninstallQuestion=Do you want to uninstall {#AppName}?
en.UninstallButton=Uninstall
en.DeleteUserData=Delete user data
en.Uninstalled={#AppName} was uninstalled.

; The folder's rights are settled before any file is copied into it (SecureAppDir).
[Dirs]
Name: "{app}"; AfterInstall: SecureAppDir

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Both shortcuts are created during installation so the uninstaller tracks and deletes them. The Start menu shortcut
; (All apps, not pinned) is always kept, so an update also restores it; the finished page removes the desktop
; shortcut when the user unchecks it (FinishShortcuts).
[Icons]
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#MainExe}"; AppUserModelID: "{#AppUserModelId}"
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#MainExe}"; AppUserModelID: "{#AppUserModelId}"

[Run]
; An in-app update runs Setup silently with /RELAUNCH after TwelveIsland has exited.
Filename: "{app}\{#MainExe}"; Flags: nowait runasoriginaluser; Check: RelaunchRequested

[Code]
var
  DesktopShortcut: Boolean;
  DesktopCheck, LaunchCheck: TNewCheckBox;
  DeleteUserData: Boolean;

function HasParam(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function RelaunchRequested: Boolean;
begin
  Result := HasParam('/RELAUNCH');
end;

function AppRunning: Boolean;
begin
  Result := CheckForMutexes('Local\{#MutexPrefix}' + GetUserNameString);
end;

// Silent runs come from an in-app update, which starts Setup and then exits, so they wait for that exit.
// Interactive runs ask the user to quit TwelveIsland from the tray menu.
function EnsureAppClosed(Silent: Boolean): Boolean;
var
  Waited: Integer;
begin
  Result := True;
  Waited := 0;
  while AppRunning do
  begin
    if Silent then
    begin
      if Waited >= 30000 then
      begin
        Result := False;
        Exit;
      end;
      Sleep(250);
      Waited := Waited + 250;
    end
    else if TaskDialogMsgBox(CustomMessage('ExitFirstTitle'), CustomMessage('ExitFirstText'),
      mbError, MB_RETRYCANCEL, [], 0) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

// ----- Setup -----

// A first install defaults to a desktop shortcut. When a version is already installed, PrepareToInstall replaces this
// with whether the desktop shortcut exists at that moment.
function InitializeSetup: Boolean;
begin
  Result := EnsureAppClosed(WizardSilent);
  DesktopShortcut := True;
end;

function AddCheck(const Caption: String; Checked: Boolean): TNewCheckBox;
begin
  Result := TNewCheckBox.Create(WizardForm);
  Result.Parent := WizardForm.FinishedPage;
  Result.Caption := Caption;
  Result.Checked := Checked;
end;

procedure InitializeWizard;
begin
  DesktopCheck := AddCheck(CustomMessage('DesktopShortcut'), DesktopShortcut);
  LaunchCheck := AddCheck(ExpandConstant('{cm:LaunchProgram,{#AppName}}'), True);
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Top: Integer;
begin
  // The ready page is skipped, so the folder page starts the installation.
  if CurPageID = wpSelectDir then
    WizardForm.NextButton.Caption := SetupMessage(msgButtonInstall)
  else if CurPageID = wpFinished then
  begin
    Top := WizardForm.FinishedLabel.Top + WizardForm.FinishedLabel.Height + ScaleY(16);
    DesktopCheck.SetBounds(WizardForm.FinishedLabel.Left, Top, WizardForm.FinishedLabel.Width, ScaleY(22));
    LaunchCheck.SetBounds(WizardForm.FinishedLabel.Left, Top + ScaleY(28), WizardForm.FinishedLabel.Width, ScaleY(22));
  end;
end;

// A typed or browsed folder always ends in \TwelveIsland, whether or not it was written with a trailing / or \.
function NormalizeAppDir(Dir: String): String;
begin
  Result := Trim(Dir);
  StringChangeEx(Result, '/', '\', True);
  while (Length(Result) > 3) and (Result[Length(Result)] = '\') do
    SetLength(Result, Length(Result) - 1);
  if CompareText(ExtractFileName(Result), '{#AppName}') <> 0 then
    Result := AddBackslash(Result) + '{#AppName}';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
    WizardForm.DirEdit.Text := NormalizeAppDir(WizardForm.DirEdit.Text);
end;

// ----- Folder rights -----

// Whether Dir is Root or a folder inside it.
function IsUnder(const Dir, Root: String): Boolean;
begin
  Result := (Root <> '') and (Pos(Lowercase(AddBackslash(Root)), Lowercase(AddBackslash(Dir))) = 1);
end;

// Program Files and the user's profile (the default %LOCALAPPDATA%\Programs) already keep other users from changing the
// program. A folder chosen elsewhere, such as D:\TwelveIsland, inherits the rights of the drive's root, which on a second
// drive let every signed-in user change files: anyone could replace the program that runs as this user at sign-in.
function NeedsOwnRights(const Dir: String): Boolean;
begin
  Result := not (IsUnder(Dir, ExpandConstant('{commonpf32}')) or IsUnder(Dir, ExpandConstant('{commonpf64}'))
    or IsUnder(Dir, GetEnv('USERPROFILE')) or IsUnder(Dir, ExpandConstant('{localappdata}')));
end;

// The SID of the user running Setup, such as S-1-5-21-..., or '' when it cannot be read.
function CurrentUserSid: String;
var
  Output: TExecOutput;
  Line: String;
  ResultCode, I: Integer;
begin
  Result := '';
  if not ExecAndCaptureOutput(ExpandConstant('{sys}\whoami.exe'), '/user /fo csv /nh', '', SW_HIDE, ewWaitUntilTerminated, ResultCode, Output)
    or (ResultCode <> 0) or (GetArrayLength(Output.StdOut) = 0) then
    Exit;
  // "domain\user","S-1-5-21-...": the SID is read from the end, so the characters of the user name do not matter.
  Line := Trim(Output.StdOut[0]);
  if (Length(Line) < 2) or (Line[Length(Line)] <> '"') then
    Exit;
  I := Length(Line) - 1;
  while (I > 0) and (Pos(Line[I], '0123456789-S') > 0) do
    I := I - 1;
  if (I = 0) or (Line[I] <> '"') then
    Exit;
  Line := Copy(Line, I + 1, Length(Line) - I - 1);
  if Copy(Line, 1, 4) = 'S-1-' then
    Result := Line;
end;

// Whether Setup can create and delete a file in Dir.
function CanWrite(const Dir: String): Boolean;
var
  Probe: String;
begin
  Probe := AddBackslash(Dir) + '.12island-write-test';
  Result := SaveStringToFile(Probe, '12Island', False) and DeleteFile(Probe);
end;

// Gives such a folder the rights of Program Files: it stops inheriting from its parent; SYSTEM and Administrators have full
// control and Users may read and run. Installed for the current user, that user also has full control, so in-app updates
// keep working without an administrator prompt. Subfolders with rights of their own keep them. If the rights cannot be set,
// or Setup cannot write to the folder with them, the folder inherits its parent's rights as before, so updates keep working.
procedure SecureAppDir;
var
  Dir, Grants, Sid: String;
  ResultCode: Integer;
begin
  Dir := ExpandConstant('{app}');
  if not NeedsOwnRights(Dir) then
  begin
    Log('The install folder keeps its inherited rights: ' + Dir);
    Exit;
  end;
  // Every account by SID, so the names need no translation and cannot be mistaken for another account.
  Grants := '"*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)RX"';
  if not IsAdminInstallMode then
  begin
    Sid := CurrentUserSid;
    if Sid = '' then
    begin
      Log('The install folder keeps its inherited rights, because the current user''s SID could not be read: ' + Dir);
      Exit;
    end;
    Grants := Grants + ' "*' + Sid + ':(OI)(CI)F"';
  end;
  if Exec(ExpandConstant('{sys}\icacls.exe'), '"' + Dir + '" /inheritance:r /grant:r ' + Grants + ' /Q', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
    and (ResultCode = 0) then
    Log('The install folder no longer inherits rights from its parent: ' + Dir)
  else
    Log('The install folder rights could not be set (code ' + IntToStr(ResultCode) + '): ' + Dir);
  if not CanWrite(Dir) then
  begin
    Exec(ExpandConstant('{sys}\icacls.exe'), '"' + Dir + '" /reset /Q', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Log('Setup could not write to the install folder, so it inherits its parent''s rights again (code ' + IntToStr(ResultCode) + '): ' + Dir);
  end;
end;

// Returns <path> from a command line such as "<path>\unins000.exe" /SILENT /UI.
// Pos disagrees with Copy on paths with Chinese characters, so the closing quote is found character by character.
function QuotedPath(const Command: String): String;
var
  I: Integer;
begin
  Result := Command;
  if (Length(Command) = 0) or (Command[1] <> '"') then
    Exit;
  Result := '';
  for I := 2 to Length(Command) do
  begin
    if Command[I] = '"' then
      Exit;
    Result := Result + Command[I];
  end;
end;

// Runs the uninstaller of the installed version before any file is copied, so no file of the old version remains.
// /UPGRADE keeps settings, data and the startup entry; the installer waits until that uninstall has finished.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Uninstaller: String;
  ResultCode, Waited: Integer;
begin
  Result := '';
  if not RegQueryStringValue(HKA, '{#UninstallKey}', 'UninstallString', Uninstaller) then
  begin
    Log('No installed version found.');
    Exit;
  end;
  // The uninstaller below deletes both shortcuts. The new installation creates them again; record whether the desktop
  // shortcut exists now so FinishShortcuts keeps it only then. A deleted desktop shortcut stays deleted, while the Start
  // menu shortcut is always restored.
  DesktopShortcut := FileExists(ExpandConstant('{autodesktop}\{#AppName}.lnk'));
  if not WizardSilent then
    DesktopCheck.Checked := DesktopShortcut;
  Uninstaller := QuotedPath(Uninstaller);
  if not FileExists(Uninstaller) then
  begin
    Log('Uninstaller of the installed version is missing: ' + Uninstaller);
    Exit;
  end;
  Log('Removing the installed version: ' + Uninstaller);
  if not Exec(Uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPGRADE', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
  begin
    Log('Uninstall failed with code ' + IntToStr(ResultCode));
    Result := FmtMessage(CustomMessage('OldVersionNotRemoved'), [IntToStr(ResultCode)]);
    Exit;
  end;
  // The old uninstaller deletes itself shortly after it exits. Waiting lets the new one reuse the name unins000.exe.
  Waited := 0;
  while FileExists(Uninstaller) and (Waited < 10000) do
  begin
    Sleep(200);
    Waited := Waited + 200;
  end;
end;

// Keeps the desktop shortcut when checked and starts TwelveIsland when requested. The Start menu shortcut always stays.
// Silent installs (in-app updates) keep the desktop shortcut only if it existed before the update.
procedure FinishShortcuts;
var
  ResultCode: Integer;
begin
  if not WizardSilent then
    DesktopShortcut := DesktopCheck.Checked;
  if not DesktopShortcut then
    DeleteFile(ExpandConstant('{autodesktop}\{#AppName}.lnk'));
  if not WizardSilent and LaunchCheck.Checked then
    ExecAsOriginalUser(ExpandConstant('{app}\{#MainExe}'), '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // Settings > Apps and Control Panel run UninstallString. /SILENT replaces the built-in confirmation and completion
  // messages with the dialog below; /UI marks that run as interactive.
  if CurStep = ssPostInstall then
    RegWriteStringValue(HKA, '{#UninstallKey}', 'UninstallString', '"' + ExpandConstant('{uninstallexe}') + '" /SILENT /UI')
  else if CurStep = ssDone then
    FinishShortcuts;
end;

// ----- Uninstall -----

// Laid out like the Windows confirmation box: a question icon and the question in the white area, and a grey
// footer holding the "delete user data" box on the left (as a task dialog's verification check box) and the
// buttons on the right.
// Sizes are in units of 100% display scaling. CreateCustomForm converts its client size to the current scaling
// itself (measured on 7.1.0: 250 became 695 px at a ScaleX factor of 1.667 when passed through ScaleX first), so
// the form size is passed unscaled while child controls use ScaleX/ScaleY. The form keeps this size instead of
// growing with WizardSizePercent.
function ConfirmUninstall: Boolean;
var
  Form: TSetupForm;
  Icon: TBitmapImage;
  Prompt: TNewStaticText;
  Footer: TPanel;
  DeleteCheck: TNewCheckBox;
  UninstallButton, CancelButton: TNewButton;
  ButtonWidth, ContentHeight: Integer;
begin
  Form := CreateCustomForm(310, 128, True, True);
  try
    Form.Caption := CustomMessage('UninstallTitle');
    // Task dialog colors. The style engine would otherwise paint both areas the same.
    Form.StyleElements := [seFont, seBorder];
    Footer := TPanel.Create(Form);
    Footer.Parent := Form;
    Footer.BevelOuter := bvNone;
    Footer.ParentBackground := False;
    Footer.StyleElements := [seFont, seBorder];
    if IsDarkInstallMode then
    begin
      Form.Color := $2B2B2B;
      Footer.Color := $202020;
    end
    else
    begin
      Form.Color := $FFFFFF;
      Footer.Color := $F0F0F0;
    end;
    Footer.SetBounds(0, Form.ClientHeight - ScaleY(50), Form.ClientWidth, ScaleY(50));
    ContentHeight := Footer.Top;

    Icon := TBitmapImage.Create(Form);
    Icon.Parent := Form;
    Icon.SetBounds(ScaleX(20), (ContentHeight - ScaleY(24)) div 2, ScaleX(24), ScaleY(24));
    InitializeBitmapImageFromStockIcon(Icon, 23 { SIID_HELP }, Form.Color, [16, 20, 24, 32, 40, 48, 64]);
    Prompt := TNewStaticText.Create(Form);
    Prompt.Parent := Form;
    Prompt.Caption := CustomMessage('UninstallQuestion');
    Prompt.Left := Icon.Left + Icon.Width + ScaleX(12);
    Prompt.Top := (ContentHeight - Prompt.Height) div 2;

    CancelButton := TNewButton.Create(Form);
    CancelButton.Parent := Footer;
    CancelButton.Caption := SetupMessage(msgButtonCancel);
    CancelButton.ModalResult := mrCancel;
    CancelButton.Cancel := True;
    UninstallButton := TNewButton.Create(Form);
    UninstallButton.Parent := Footer;
    UninstallButton.Caption := CustomMessage('UninstallButton');
    UninstallButton.ModalResult := mrOk;
    UninstallButton.Default := True;
    ButtonWidth := Form.CalculateButtonWidth([UninstallButton.Caption, CancelButton.Caption]);
    CancelButton.SetBounds(Footer.Width - ScaleX(12) - ButtonWidth, (Footer.Height - ScaleY(23)) div 2, ButtonWidth, ScaleY(23));
    UninstallButton.SetBounds(CancelButton.Left - ScaleX(8) - ButtonWidth, CancelButton.Top, ButtonWidth, ScaleY(23));
    DeleteCheck := TNewCheckBox.Create(Form);
    DeleteCheck.Parent := Footer;
    DeleteCheck.Caption := CustomMessage('DeleteUserData');
    DeleteCheck.Checked := False;
    DeleteCheck.SetBounds(ScaleX(20), (Footer.Height - ScaleY(19)) div 2, UninstallButton.Left - ScaleX(28), ScaleY(19));
    Form.ActiveControl := UninstallButton;
    Result := Form.ShowModal = mrOk;
    DeleteUserData := DeleteCheck.Checked;
  finally
    Form.Free;
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := EnsureAppClosed(UninstallSilent and not HasParam('/UI'));
  DeleteUserData := HasParam('/DELETEUSERDATA');
  if Result and HasParam('/UI') then
    Result := ConfirmUninstall;
end;

procedure RemoveStartupEntry;
var
  Command: String;
begin
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#RunValue}', Command) and
    (Pos(Lowercase(AddBackslash(ExpandConstant('{app}'))), Lowercase(Command)) > 0) then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#RunValue}');
end;

// Enhanced sensors set up a service in Program Files (TwelveIsland's SensorSetup); it goes with TwelveIsland, removed by TwelveIsland itself
// with one administrator prompt. PawnIO is the user's own install and stays.
procedure RemoveSensorService;
var
  Code: Integer;
begin
  if RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\TwelveIslandSensors') and FileExists(ExpandConstant('{app}\{#MainExe}')) then
    ShellExec('runas', ExpandConstant('{app}\{#MainExe}'), '--sensor-service remove', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usUninstall) and not HasParam('/UPGRADE') then
  begin
    RemoveStartupEntry;
    RemoveSensorService;
  end
  else if (CurUninstallStep = usPostUninstall) and DeleteUserData and not HasParam('/UPGRADE') then
    DelTree(ExpandConstant('{#DataDir}'), True, True, True)
  else if (CurUninstallStep = usDone) and HasParam('/UI') then
    MsgBox(CustomMessage('Uninstalled'), mbInformation, MB_OK);
end;
