; Inno Setup script for StealthDeck.
; Build with:  ISCC.exe /DAppVersion=1.0.0 /DSourceDir=..\publish installer\StealthDeck.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

#define AppName "StealthDeck"
#define AppExe "StealthDeck.exe"

[Setup]
AppId={{B6E1D3A4-7C52-4F0E-9A1B-3D8F5C2E7A90}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=StealthDeck-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
WizardStyle=modern
SetupIconFile=..\assets\StealthDeck.ico
; Per-user install by default (no admin prompt); the user can choose "all users" on the first page.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
; The app is a hidden tray program with no visible window to close, so detect it by its single-instance
; mutex and ask the user to exit it from the tray instead of failing on a locked file.
AppMutex=Local\StealthDeck.SingleInstance.{{8F3C2A51-6D4E-4B7A-9C1D-2E5F7A8B9C0D}

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "startup"; Description: "Start {#AppName} when I sign in to Windows"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; User data (notes, browser sessions) in %LocalAppData%\StealthDeck is removed only if the user agrees below.
Type: filesandordirs; Name: "{app}"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\StealthDeck');
    if DirExists(DataDir) then
    begin
      if MsgBox('Also delete your notes and saved browser sessions?' + #13#10 + DataDir,
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
