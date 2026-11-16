#ifndef Payload
  #error Payload directory is required
#endif
#ifndef AppVersion
  #define AppVersion "0.2.0-rc.2"
#endif
[Setup]
AppId={{6CC1415A-4816-480B-9666-078FD7E67D93}
AppName=PixelLyrics
AppVersion={#AppVersion}
AppPublisher=Skyye2003
AppPublisherURL=https://github.com/Skyye2003/PixelLyrics
DefaultDirName={autopf}\PixelLyrics
DefaultGroupName=PixelLyrics
OutputDir=..\release\setup
OutputBaseFilename=PixelLyrics-{#AppVersion}-Setup-win-x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
SetupIconFile=..\PixelLyrics.Probe\Assets\pixellyrics.ico
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\PixelLyrics.exe
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Types]
Name: full; Description: "PixelLyrics + NetEase sync components"
Name: qq; Description: "PixelLyrics only (QQ Music)"
Name: custom; Description: "Custom installation"; Flags: iscustom

[Components]
Name: app; Description: "PixelLyrics"; Types: full qq custom; Flags: fixed
Name: netease; Description: "网易云同步组件 (BetterNCM + InfLink-rs)"; Types: full

[Tasks]
Name: desktopicon; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#Payload}\PixelLyrics.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Payload}\THIRD_PARTY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Payload}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Payload}\component-source.zip"; DestDir: "{app}\sources"; Flags: ignoreversion
Source: "Components.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "{#Payload}\BetterNCMII-1.3.4.dll"; Flags: dontcopy
Source: "{#Payload}\InfLink-rs-3.4.0.plugin"; Flags: dontcopy
Source: "{#Payload}\vc_redist.x64.exe"; Flags: dontcopy

[Icons]
Name: "{group}\PixelLyrics"; Filename: "{app}\PixelLyrics.exe"; IconFilename: "{app}\PixelLyrics.exe"; IconIndex: 0
Name: "{autodesktop}\PixelLyrics"; Filename: "{app}\PixelLyrics.exe"; IconFilename: "{app}\PixelLyrics.exe"; IconIndex: 0; Tasks: desktopicon

[Run]
Filename: "{app}\PixelLyrics.exe"; Description: "启动 PixelLyrics"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
var
  PathsPage: TInputDirWizardPage;
  ProfileLabel: TNewStaticText;
  ProfileDir: String;

function Quote(Value: String): String;
begin
  Result := '"' + Value + '"';
end;

function DetectCloudMusic(): String;
var Value: String;
begin
  Result := '';
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\App Paths\cloudmusic.exe', '', Value) or
     RegQueryStringValue(HKLM64, 'Software\Microsoft\Windows\CurrentVersion\App Paths\cloudmusic.exe', '', Value) or
     RegQueryStringValue(HKLM32, 'Software\Microsoft\Windows\CurrentVersion\App Paths\cloudmusic.exe', '', Value) then
    Result := ExtractFileDir(RemoveQuotes(Value));
  if (Result = '') and RegQueryStringValue(HKLM32, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\网易云音乐', 'UninstallString', Value) then
    Result := ExtractFileDir(RemoveQuotes(Value));
end;

procedure InitializeWizard();
begin
  ProfileDir := GetEnv('BETTERNCM_PROFILE');
  if ProfileDir = '' then ProfileDir := 'C:\betterncm';
  PathsPage := CreateInputDirPage(wpSelectComponents, '网易云同步组件',
    '安装前请从托盘完全退出网易云音乐',
    '将向网易云目录写入 BetterNCM，并向组件目录写入 InfLink-rs。仅支持已实测的 3.1.41.205564 x64。' + #13#10 +
    'BetterNCM 自带插件商店及在线更新机制，运行网易云时可能访问其上游服务。安装组件本身无需联网。', False, '');
  PathsPage.Add('网易云安装目录（包含 cloudmusic.exe）：');
  PathsPage.Values[0] := DetectCloudMusic();
  ProfileLabel := TNewStaticText.Create(PathsPage);
  ProfileLabel.Parent := PathsPage.Surface;
  ProfileLabel.Left := 0;
  ProfileLabel.Top := ScaleY(125);
  ProfileLabel.Width := PathsPage.SurfaceWidth;
  ProfileLabel.Height := ScaleY(65);
  ProfileLabel.AutoSize := False;
  ProfileLabel.WordWrap := True;
  ProfileLabel.Caption := '组件目录：' + ProfileDir + #13#10 + '使用 BETTERNCM_PROFILE 或默认目录；不会修改环境变量。请使用日常登录账户运行安装程序。';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = PathsPage.ID) and not WizardIsComponentSelected('netease');
end;

function RunComponents(Action: String; var ErrorText: String): Boolean;
var Args: String; Code: Integer; Lines: TArrayOfString; I: Integer; Script, State: String;
begin
  Script := ExpandConstant('{app}\installer\Components.ps1');
  State := ExpandConstant('{app}\installer\state');
  Args := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + Quote(Script) +
    ' -Action ' + Action + ' -StateDir ' + Quote(State);
  if Action = 'Install' then
    Args := Args + ' -CloudMusicDir ' + Quote(PathsPage.Values[0]) +
      ' -ProfileDir ' + Quote(ProfileDir) + ' -PayloadDir ' + Quote(ExpandConstant('{tmp}'));
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Args, '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := Result and (Code = 0);
  if not Result then begin
    ErrorText := '组件操作失败。请检查安装日志并重试。';
    if LoadStringsFromFile(State + '\component-error.txt', Lines) then begin
      ErrorText := '';
      for I := 0 to GetArrayLength(Lines) - 1 do ErrorText := ErrorText + Lines[I] + #13#10;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var ErrorText: String;
begin
  if (CurStep = ssPostInstall) and WizardIsComponentSelected('netease') then begin
    ExtractTemporaryFile('BetterNCMII-1.3.4.dll');
    ExtractTemporaryFile('InfLink-rs-3.4.0.plugin');
    ExtractTemporaryFile('vc_redist.x64.exe');
    if not RunComponents('Install', ErrorText) then
      RaiseException(ErrorText + #13#10 + 'PixelLyrics 已安装，网易云组件未完成；修正问题后重新运行安装包。');
    WizardForm.FinishedLabel.Caption := 'PixelLyrics 与网易云同步组件已安装。请重新启动网易云音乐。';
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := MsgBox('卸载 PixelLyrics，并移除本安装包创建且未被更改的网易云组件。已有插件和个人配置会保留。请先退出网易云音乐。', mbConfirmation, MB_YESNO) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var ErrorText: String;
begin
  if (CurUninstallStep = usUninstall) and FileExists(ExpandConstant('{app}\installer\state\netease-components.json')) then
    if not RunComponents('Uninstall', ErrorText) then RaiseException(ErrorText);
end;
