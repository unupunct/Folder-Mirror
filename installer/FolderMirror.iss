; Folder Mirror installer – build with scripts\build-release.ps1 (Inno Setup 6)
#define AppName "Folder Mirror"
#define AppVersion "1.0.0"
#define AppPublisher "Folder Mirror"
#define AppExe "FolderMirror.exe"
#define AppUrl "https://github.com/unupunct/Folder-Mirror"

[Setup]
AppId={{7B1E5C52-3F0A-4B8E-9D8C-5A4F1E6D0F21}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup
VersionInfoCopyright=Copyright (C) 2026 {#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\release
OutputBaseFilename=FolderMirror_Setup
SetupIconFile=..\assets\FolderMirror.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Start Folder Mirror when I sign in to Windows"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "..\release\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\release\README.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "FolderMirror"; ValueData: """{app}\{#AppExe}"" --minimized"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; remove all Folder Mirror scheduled tasks created by the app
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""\FolderMirror"" /F"; Flags: runhidden; RunOnceId: "DelTaskFolder"

[Code]
procedure RemoveScheduledTasks;
var
  ResultCode: Integer;
  Tmp, Ps: String;
begin
  { schtasks cannot delete a folder with tasks in it, so enumerate and delete each task }
  Ps := '-NoProfile -ExecutionPolicy Bypass -Command "Get-ScheduledTask -TaskPath ''\FolderMirror\'' -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false"';
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Ps, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
    RemoveScheduledTasks;
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\FolderMirror');
    if DirExists(DataDir) then
    begin
      if MsgBox('Do you also want to remove your Folder Mirror data (profiles, synchronization history and logs)?' + #13#10#13#10 +
                'Choose No to keep it for a future installation. Your synchronized files are never touched.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
