; Inno Setup script for Multi-SSH
; Builds a per-user installer (no admin required) with Start Menu / optional
; Desktop shortcuts and an uninstaller. Compile with:
;   "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\Multi-SSH.iss
; The published self-contained exe must exist first (see build-installer.ps1).

#define AppName "Multi-SSH"
#define AppVersion "1.0.3"
#define AppPublisher "joc00p"
#define AppExe "Multi-SSH.exe"
#define AppUrl "https://github.com/joc00p/Multi-SSH"
; Path to the published single-file exe, relative to this script.
#define PublishDir "..\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
AppId={{9A5E4C2F-7B3D-4E6A-9C1F-2D8B6A0E5F71}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user install => no UAC/admin prompt.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=Output
OutputBaseFilename=Multi-SSH-Setup-{#AppVersion}
SetupIconFile=..\Assets\multissh.ico
UninstallDisplayIcon={app}\{#AppExe}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
{ Uninstall registry key for this AppId (Inno appends _is1). }
const
  UninstKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{9A5E4C2F-7B3D-4E6A-9C1F-2D8B6A0E5F71}_is1';

{ Returns the previous version's uninstaller exe (unquoted), or '' if not installed.
  Checks per-user (HKCU) first, then per-machine (HKLM, both registry views). }
function PreviousUninstaller(): String;
var
  s: String;
begin
  Result := '';
  if RegQueryStringValue(HKCU, UninstKey, 'UninstallString', s) then
    Result := RemoveQuotes(s)
  else if RegQueryStringValue(HKLM64, UninstKey, 'UninstallString', s) then
    Result := RemoveQuotes(s)
  else if RegQueryStringValue(HKLM32, UninstKey, 'UninstallString', s) then
    Result := RemoveQuotes(s);
end;

{ Before installing new files, silently uninstall any previous version. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  Uninstaller: String;
  ResultCode: Integer;
  Tries: Integer;
begin
  if CurStep = ssInstall then
  begin
    Uninstaller := PreviousUninstaller();
    if (Uninstaller <> '') and FileExists(Uninstaller) then
    begin
      Exec(Uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '',
        SW_HIDE, ewWaitUntilTerminated, ResultCode);
      { The Inno uninstaller relaunches from a temp copy and returns early, so
        wait for the real uninstaller exe to disappear before we overwrite files. }
      Tries := 0;
      while FileExists(Uninstaller) and (Tries < 100) do
      begin
        Sleep(100);
        Tries := Tries + 1;
      end;
    end;
  end;
end;
