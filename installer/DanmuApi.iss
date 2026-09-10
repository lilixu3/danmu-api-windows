#ifndef SourceDir
  #define SourceDir "..\artifacts\release-publish"
#endif
#ifndef AppVersion
  #define AppVersion "0.1.1"
#endif
#ifndef OutputRoot
  #define OutputRoot "..\artifacts\release"
#endif
[Setup]
AppId={{D6F1E4DB-9C73-4DE2-B985-9DAF96B8E9B4}
AppName=弹幕API
AppVersion={#AppVersion}
AppPublisher=Danmu API
DefaultDirName={autopf}\DanmuApi
DefaultGroupName=弹幕API
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir={#OutputRoot}
OutputBaseFilename=DanmuApi-{#AppVersion}-win-x64-setup
SetupIconFile=..\assets\icons\danmuapi.ico
UninstallDisplayIcon={app}\DanmuApi.App.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
SignTool=DanmuSign
SignedUninstaller=yes

[Files]
Source: "{#SourceDir}\*.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\runtime-bundle\*"; DestDir: "{app}\runtime-bundle"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\弹幕API"; Filename: "{app}\DanmuApi.App.exe"; AppUserModelID: "DanmuApi.Windows"
Name: "{autodesktop}\弹幕API"; Filename: "{app}\DanmuApi.App.exe"; Tasks: desktopicon; AppUserModelID: "DanmuApi.Windows"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："

[InstallDelete]
Type: files; Name: "{autoprograms}\Danmu API.lnk"
Type: files; Name: "{autodesktop}\Danmu API.lnk"

[Run]
Filename: "{app}\DanmuApi.App.exe"; Description: "启动 弹幕API"; Flags: postinstall nowait skipifsilent unchecked

[Code]
const
  LegacyCode = '{E56FCF01-A357-33A6-8C20-41C23E8D1B97}';
  LegacyUninstall = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{E56FCF01-A357-33A6-8C20-41C23E8D1B97}';
  CurrentUninstall = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{D6F1E4DB-9C73-4DE2-B985-9DAF96B8E9B4}_is1';
var
  LegacyPresent: Boolean;
  HadAutostart: Boolean;
  LegacyDirectory: String;

function CreateFile(Name: String; Access, Share, Security, Creation, Flags, Template: LongWord): LongWord;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandle(Handle: LongWord): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function MsiQueryProductState(Product: String): Integer;
  external 'MsiQueryProductStateW@msi.dll stdcall';

function CompareVersion(Left, Right: String): Integer;
var
  I, P, A, B: Integer;
  Part: String;
begin
  Result := 0;
  for I := 1 to 3 do begin
    P := Pos('.', Left);
    if P = 0 then P := Length(Left) + 1;
    Part := Copy(Left, 1, P - 1);
    A := StrToIntDef(Part, -1);
    Delete(Left, 1, P);
    P := Pos('.', Right);
    if P = 0 then P := Length(Right) + 1;
    Part := Copy(Right, 1, P - 1);
    B := StrToIntDef(Part, -1);
    Delete(Right, 1, P);
    if (A < 0) or (B < 0) then RaiseException('安装版本号无效');
    if A > B then begin Result := 1; exit; end;
    if A < B then begin Result := -1; exit; end;
  end;
end;

function InitializeSetup(): Boolean;
var
  Version, Name: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM64, CurrentUninstall, 'DisplayVersion', Version) then
    if CompareVersion(Version, '{#AppVersion}') > 0 then begin
      MsgBox('已安装更新版本，不能降级。', mbError, MB_OK);
      exit;
    end;
  LegacyPresent := MsiQueryProductState(LegacyCode) = 5;
  if LegacyPresent then begin
    if not RegQueryStringValue(HKLM64, LegacyUninstall, 'DisplayName', Name) or
       not RegQueryStringValue(HKLM64, LegacyUninstall, 'DisplayVersion', Version) then begin
      MsgBox('旧版 MSI 身份信息不完整，停止迁移。', mbError, MB_OK);
      exit;
    end;
    if (Name <> '弹幕API') or (Version <> '0.1.0') then begin
      MsgBox('旧版 MSI 元数据与发布记录不一致，停止迁移。', mbError, MB_OK);
      exit;
    end;
    RegQueryStringValue(HKLM64, LegacyUninstall, 'InstallLocation', LegacyDirectory);
    if MsgBox('检测到 Kotlin 测试版。安装将移除旧应用并保留核心、配置和日志。请先从旧版托盘退出并停止服务。继续？', mbConfirmation, MB_YESNO) <> IDYES then exit;
  end;
  Result := True;
end;

procedure InitializeWizard();
begin
  if LegacyPresent and (LegacyDirectory <> '') then
    WizardForm.DirEdit.Text := RemoveBackslashUnlessRoot(LegacyDirectory);
end;

function OpenProcess(Access: LongWord; Inherit: Boolean; Pid: LongWord): LongWord;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle, Timeout: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  LockPath, Command, ReadyPath: String;
  Handle, ParentHandle: LongWord;
  ParentPid: Integer;
  ExitCode: Integer;
begin
  Result := '';
  ParentPid := StrToIntDef(ExpandConstant('{param:UPDATEPARENT|0}'), 0);
  if ParentPid > 0 then begin
    ReadyPath := ExpandConstant('{param:UPDATEREADY|}');
    if (Pos(Lowercase(AddBackslash(ExpandConstant('{localappdata}\DanmuApi\app-updates'))), Lowercase(ReadyPath)) <> 1) or
       (Pos('..', ReadyPath) > 0) or (ExtractFileName(ReadyPath) <> 'installer.ready') then begin
      Result := '更新握手路径无效'; exit;
    end;
    ParentHandle := OpenProcess($00100000, False, ParentPid);
    if ParentHandle = 0 then begin Result := '无法验证待退出进程'; exit; end;
    try
      if not SaveStringToFile(ReadyPath, 'ready', False) then begin Result := '无法写入更新握手'; exit; end;
      if WaitForSingleObject(ParentHandle, 120000) <> 0 then begin Result := '旧程序未退出，已取消更新'; exit; end;
      if FileExists(ExtractFilePath(ReadyPath) + 'cancel') then begin Result := '主程序取消了更新'; exit; end;
    finally CloseHandle(ParentHandle); end;
  end;
  LockPath := ExpandConstant('{userappdata}\DanmuApi\instance.lock');
  if FileExists(LockPath) then begin
    Handle := CreateFile(LockPath, $C0000000, 0, 0, 3, $80, 0);
    if Handle = $FFFFFFFF then begin
      Result := '弹幕 API 仍在运行，请从托盘完全退出后重新安装。';
      exit;
    end;
    CloseHandle(Handle);
  end;
  LockPath := ExpandConstant('{userappdata}\DanmuApi\app.lock');
  if FileExists(LockPath) then begin
    Handle := CreateFile(LockPath, $C0000000, 0, 0, 3, $80, 0);
    if Handle = $FFFFFFFF then begin
      Result := '旧 Kotlin 程序仍在运行，请退出旧程序并停止服务。';
      exit;
    end;
    CloseHandle(Handle);
  end;
  HadAutostart := RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DanmuApi', Command);
  if LegacyPresent then begin
    if not Exec(ExpandConstant('{sys}\msiexec.exe'), '/x ' + LegacyCode + ' /qn /norestart /L*v "' + ExpandConstant('{tmp}\danmu-legacy-uninstall.log') + '"', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then begin
      Result := '无法启动旧版卸载器，未继续安装。';
      exit;
    end;
    if ExitCode = 3010 then begin
      NeedsRestart := True;
      Result := '旧版卸载要求重启。请重启 Windows 后重新运行安装器；用户数据已保留。';
      exit;
    end;
    if ExitCode <> 0 then begin
      Result := '旧版卸载失败，退出码 ' + IntToStr(ExitCode) + '。日志位于 ' + ExpandConstant('{tmp}\danmu-legacy-uninstall.log');
      exit;
    end;
    LegacyPresent := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and HadAutostart then
    if not RegWriteStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DanmuApi', '"' + ExpandConstant('{app}\DanmuApi.App.exe') + '" --autostart') then
      RaiseException('刷新开机启动入口失败，请在应用设置中重新设置。');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep = usPostUninstall then begin
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DanmuApi', Command) and
       (Pos(Lowercase(ExpandConstant('{app}\DanmuApi.App.exe')), Lowercase(Command)) > 0) then
      RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DanmuApi');
    if RegQueryStringValue(HKCU, 'Software\Classes\danmuapi\shell\open\command', '', Command) and
       (Pos(Lowercase(ExpandConstant('{app}\DanmuApi.App.exe')), Lowercase(Command)) > 0) then begin
      RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\danmuapi');
      RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\AppUserModelId\DanmuApi.Windows');
      DeleteFile(ExpandConstant('{userprograms}\弹幕API.lnk'));
    end;
  end;
end;
