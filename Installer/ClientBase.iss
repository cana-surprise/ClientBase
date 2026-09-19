; Установщик «База клиентов». Собирается скриптом build-installer.ps1 (Inno Setup 6).
; Данные пользователя (база, фото, резервные копии) хранятся вне папки программы —
; в «Документах\База клиентов» — и при обновлении и удалении программы не затрагиваются.

#define AppName "База клиентов"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish-installer"
#endif

[Setup]
; Постоянный идентификатор приложения: по нему новая версия ставится «поверх» старой.
AppId={{7B3E5A12-94C8-4F6D-A1E0-2D58C3B9F471}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=BazaKlientov-Setup-{#AppVersion}
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\ClientBase.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no
; По умолчанию ставится для текущего пользователя (без запроса прав администратора);
; в мастере можно выбрать установку для всех пользователей.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Если программа открыта, установщик попросит её закрыть.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на &рабочем столе"; GroupDescription: "Дополнительно:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\ClientBase.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\ClientBase.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ClientBase.exe"; Description: "Запустить {#AppName}"; Flags: nowait postinstall skipifsilent
; Обновление из самой программы идёт в тихом режиме — после него программа запускается снова.
Filename: "{app}\ClientBase.exe"; Flags: nowait runasoriginaluser; Check: WizardSilent
