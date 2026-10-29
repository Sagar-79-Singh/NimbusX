; NimbusX installer (Inno Setup 6)
;
; Design rules this script follows:
;   * per-user install, no administrator rights, no service, no driver
;   * nothing is installed machine-wide and no PATH entry is added silently
;   * WinFsp is NOT bundled; rclone mounts need it and NimbusX offers the official installer instead
;   * rclone is bundled only when the payload contains verified binaries (scripts/fetch-deps.ps1)
;   * uninstall never touches %LOCALAPPDATA%\NimbusX (settings, cache, logs) unless the user ticks the box

#define AppName "NimbusX"
#define AppPublisher "NimbusX"
#define AppExeName "NimbusX.exe"
#define CliExeName "cpx.exe"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PayloadDir
  #define PayloadDir "..\artifacts\payload"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{7A5B1C2E-5E1D-4C48-9A1D-2B7C3B6E9F11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
OutputDir={#OutputDir}
OutputBaseFilename=NimbusX-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
MinVersion=10.0.17763
DisableDirPage=no
DisableReadyPage=no
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
LicenseFile=..\LICENSE
InfoBeforeFile=..\installer\BEFORE-YOU-INSTALL.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "startmenu"; Description: "Create a Start menu entry"; GroupDescription: "Shortcuts:"
Name: "clipath"; Description: "Add the cpx command line tool to my user PATH"; GroupDescription: "Command line:"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\cli\*"; DestDir: "{app}\cli"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
; Only present when scripts/fetch-deps.ps1 ran before packaging (verified binaries).
Source: "{#PayloadDir}\tools\rclone\*"; DestDir: "{localappdata}\NimbusX\tools\rclone"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#PayloadDir}\tools\terabox\*"; DestDir: "{localappdata}\NimbusX\tools\terabox"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#PayloadDir}\tools\winfsp\*.msi"; DestDir: "{localappdata}\NimbusX\tools\winfsp"; Flags: ignoreversion skipifsourcedoesntexist
; Licence texts for the bundled third party binaries (rclone MIT, rclone-extra MIT, WinFsp GPLv3 + FLOSS exception).
Source: "{#PayloadDir}\tools\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\BUILD_STATUS.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startmenu
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"; Tasks: startmenu

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent
Filename: "{localappdata}\NimbusX\tools\winfsp\winfsp-2.1.25156.msi"; Description: "Install WinFsp 2.1.25156 (needed for mounted drives; Windows will ask for permission)"; Flags: shellexec waituntilterminated postinstall skipifsilent unchecked; Check: WinFspInstallerPresent

[UninstallDelete]
; Only the install folder is removed. Settings, cache and logs stay unless the user asks below.

[Code]
const
  UninstallDataPrompt = 'NimbusX keeps its settings, cache and logs in your local app data folder so a reinstall picks up where you left off. Do you also want to delete that data now? Cloud files are never touched either way.';

function WinFspInstallerPresent: Boolean;
begin
  Result := FileExists(ExpandConstant('{localappdata}\NimbusX\tools\winfsp\winfsp-2.1.25156.msi'));
end;

function WinFspInstalled: Boolean;
var
  Version: String;
begin
  // WinFsp registers its version in the uninstall key; absent means not installed.
  Result := RegQueryStringValue(HKEY_LOCAL_MACHINE, 'SOFTWARE\WOW6432Node\WinFsp', 'Version', Version) or
            RegQueryStringValue(HKEY_LOCAL_MACHINE, 'SOFTWARE\WinFsp', 'Version', Version);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if not WinFspInstalled then
    begin
      // A visible note, never a silent install and never a changed Windows setting.
      MsgBox('WinFsp was not found on this PC.' + #13#10 + #13#10 +
             'NimbusX can list your cloud accounts without it, but mounting a drive needs WinFsp. ' +
             'You can install the official WinFsp build from the last step of this wizard, or from NimbusX: ' +
             'Diagnostics shows the exact installer and its SHA-256.',
             mbInformation, MB_OK);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\NimbusX');
    if DirExists(DataDir) then
    begin
      if MsgBox(UninstallDataPrompt, mbConfirmation, MB_YESNO) = IDYES then
      begin
        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := True;
end;
