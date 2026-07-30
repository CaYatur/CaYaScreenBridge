; CaYaScreenBridge installer
;
; Deliberately minimal. The application registers its own logon scheduled task on first run, so the
; installer does not need to create one; doing it in both places is how a startup entry ends up
; pointing at a path that no longer exists after an update.
;
; Build:  iscc build\installer\CaYaScreenBridge.iss /DSourceDir=..\..\artifacts\publish /DAppVersion=1.0.0

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\..\artifacts\publish"
#endif

#define AppName "CaYaScreenBridge"
#define AppPublisher "CaYaDev"
#define AppUrl "https://cayadev.com"
#define AppExe "CaYaScreenBridge.exe"

[Setup]
AppId={{B6F2A5C1-7E3D-4B9A-9C21-3D5E8F0A4C77}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
OutputDir=Output
OutputBaseFilename=CaYaScreenBridge-{#AppVersion}-setup
SetupIconFile=..\..\src\CaYaScreenBridge.Windows\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
turkish.StartupTask=Windows ile birlikte baslat (yonetici gorevi)
turkish.LaunchApp={#AppName} uygulamasini simdi baslat
turkish.DesktopIcon=Masaustu kisayolu olustur
english.StartupTask=Start with Windows (elevated task)
english.LaunchApp=Launch {#AppName} now
english.DesktopIcon=Create a desktop shortcut

[Tasks]
Name: "startup"; Description: "{cm:StartupTask}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove the logon task the application created for itself. Failure is ignored: the task may never
; have been created, and a missing task must not block the uninstall.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /F /TN ""CaYaScreenBridge"""; Flags: runhidden; RunOnceId: "RemoveStartupTask"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ConfigDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // The display calibration is the user's work, so it is kept unless they ask for it to go.
    ConfigDir := ExpandConstant('{localappdata}\CaYaScreenBridge');
    if DirExists(ConfigDir) then
    begin
      if MsgBox('Ekran kalibrasyonu ve ayarlar da silinsin mi?' + #13#10 +
                'Remove the display calibration and settings as well?',
                mbConfirmation, MB_YESNO) = IDYES then
      begin
        DelTree(ConfigDir, True, True, True);
      end;
    end;
  end;
end;
