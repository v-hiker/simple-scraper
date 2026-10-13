#if VER < EncodeVer(6, 5, 0)
  #error Inno Setup 6.5.0 or newer is required.
#endif

#ifndef AppVersion
  #error AppVersion must be supplied by build-installer.ps1.
#endif
#ifndef PublishDirectory
  #error PublishDirectory must be supplied by build-installer.ps1.
#endif
#ifndef ReleaseDirectory
  #error ReleaseDirectory must be supplied by build-installer.ps1.
#endif
#ifndef RepositoryUrl
  #define RepositoryUrl "https://github.com/v-hiker/simple-scraper"
#endif

[Setup]
AppId=SimpleScraper
AppName=简单刮削器
AppVersion={#AppVersion}
AppVerName=简单刮削器 {#AppVersion}
AppPublisher=v-hiker
AppPublisherURL={#RepositoryUrl}
AppSupportURL={#RepositoryUrl}/issues
AppUpdatesURL={#RepositoryUrl}/releases
DefaultDirName={localappdata}\Programs\SimpleScraper
DisableDirPage=yes
UsePreviousAppDir=yes
DefaultGroupName=简单刮削器
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
UninstallDisplayName=简单刮削器
UninstallDisplayIcon={app}\SimpleScraper.exe
OutputDir={#ReleaseDirectory}
OutputBaseFilename=SimpleScraper-{#AppVersion}-win-x64-setup
SetupIconFile={#PublishDirectory}\Assets\SimpleScraper.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName=SimpleScraper
VersionInfoDescription=简单刮削器安装程序

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\简单刮削器"; Filename: "{app}\SimpleScraper.exe"; WorkingDir: "{app}"; AppUserModelID: "SimpleScraper.App"; Comment: "管理影视媒体库与元数据"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\SimpleScraper.exe"; ValueType: string; ValueName: ""; ValueData: "{app}\SimpleScraper.exe"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\SimpleScraper.exe"; ValueType: string; ValueName: "Path"; ValueData: "{app}"

[Run]
Filename: "{app}\SimpleScraper.exe"; WorkingDir: "{app}"; Description: "启动简单刮削器"; Flags: nowait postinstall skipifsilent; Check: ShouldLaunch

[Code]
function ShouldLaunch: Boolean;
var
  Index: Integer;
begin
  Result := True;
  for Index := 1 to ParamCount do
  begin
    if CompareText(ParamStr(Index), '/NOLAUNCH') = 0 then
    begin
      Result := False;
      Exit;
    end;
  end;
end;
