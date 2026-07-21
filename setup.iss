; Instalador do Taskbar Widget for Spotify (per-user, sem UAC)
; Compilar: ISCC.exe setup.iss  (depois de dotnet publish)

#define MyAppName "Taskbar Widget for Spotify"
#define MyAppVersion "1.3.0"
#define MyAppPublisher "MechanicWB"
#define MyAppURL "https://github.com/mechanicwb2-hub/spotify-taskbar-widget"
#define MyAppExeName "SpotifyTaskbarWidget.exe"

[Setup]
AppId={{7C1E9F42-5B8A-4D33-9C61-2F4A8E7B5D10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={userpf}\SpotifyTaskbarWidget
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=installer
OutputBaseFilename=SpotifyTaskbarWidget-Setup
SetupIconFile=app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no
CloseApplications=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "pt"; MessagesFile: "compiler:Languages\Portuguese.isl"

[Tasks]
Name: "autostart"; Description: "{cm:AutoStartProgram,{#MyAppName}}"

[Files]
Source: "publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "SpotifyTaskbarWidget"; ValueData: """{app}\{#MyAppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/f /im {#MyAppExeName}"; Flags: runhidden; RunOnceId: "KillWidget"

[Code]
// A app precisa do .NET 8 Desktop Runtime; se faltar, descarrega e instala
// (link evergreen da Microsoft). Sem rede, a instalação continua na mesma.
function NeedsDotNet(): Boolean;
var
  RC: Integer;
begin
  Result := not (Exec('cmd.exe',
    '/c dotnet --list-runtimes | findstr /C:"Microsoft.WindowsDesktop.App 8." >nul',
    '', SW_HIDE, ewWaitUntilTerminated, RC) and (RC = 0));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  RC: Integer;
begin
  Result := '';
  if NeedsDotNet() then
  begin
    try
      DownloadTemporaryFile(
        'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe',
        'windowsdesktop-runtime.exe', '', nil);
      Exec(ExpandConstant('{tmp}\windowsdesktop-runtime.exe'),
        '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, RC);
    except
      // sem rede ou download falhou — a app avisa ao arrancar
    end;
  end;
end;
