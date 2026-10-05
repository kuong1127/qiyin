#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef BuildOutput
  #error BuildOutput is required
#endif

[Setup]
AppId={{56307F38-031B-4481-9B8F-2F1BAF924FD8}
AppName=栖音
AppVersion=0.2.20
AppVerName=栖音 Qiyin 0.2.20
AppPublisher=Qiyin
DefaultDirName={localappdata}\Programs\TaskbarMediaBar
DefaultGroupName=栖音
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#BuildOutput}
OutputBaseFilename=Qiyin-Setup-0.2.20-win-x64
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\TaskbarMediaBar.exe
SetupIconFile=..\Assets\Qiyin.ico
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
VersionInfoVersion=0.2.20.0
VersionInfoDescription=栖音 Qiyin 安装程序

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："
Name: "autostart"; Description: "登录 Windows 后自动启动"; GroupDescription: "启动选项："; Flags: unchecked

[Files]
Source: "{#PayloadDir}\TaskbarMediaBar.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\使用说明.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\DOTNET-LICENSE.txt"; DestDir: "{app}\许可"; Flags: ignoreversion
Source: "{#PayloadDir}\DOTNET-ThirdPartyNotices.txt"; DestDir: "{app}\许可"; Flags: ignoreversion
Source: "defaults.json"; DestDir: "{app}"; DestName: "默认配置.json"; Flags: ignoreversion
Source: "defaults.json"; DestDir: "{userappdata}\TaskbarMediaBar"; DestName: "settings.json"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\栖音"; Filename: "{app}\TaskbarMediaBar.exe"; WorkingDir: "{app}"
Name: "{group}\使用说明"; Filename: "{app}\使用说明.txt"
Name: "{autodesktop}\栖音"; Filename: "{app}\TaskbarMediaBar.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TaskbarMediaBar"; ValueData: """{app}\TaskbarMediaBar.exe"""; Tasks: autostart

[Run]
Filename: "{app}\TaskbarMediaBar.exe"; Description: "立即启动栖音"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand: String;
  InstalledExe: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    InstalledExe := ExpandConstant('{app}\TaskbarMediaBar.exe');
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'TaskbarMediaBar', StartupCommand) then
    begin
      if (CompareText(StartupCommand, '"' + InstalledExe + '"') = 0) or
         (CompareText(StartupCommand, InstalledExe) = 0) then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'TaskbarMediaBar');
    end;
  end;
end;
