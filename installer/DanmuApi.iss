#ifndef SourceDir
  #define SourceDir "..\artifacts\release-publish"
#endif
#ifndef AppVersion
  #define AppVersion "0.1.1"
#endif
#ifndef OutputRoot
  #define OutputRoot "..\artifacts\release"
#endif
#ifndef ArchName
  #define ArchName "x64"
#endif
[Setup]
AppId={{D6F1E4DB-9C73-4DE2-B985-9DAF96B8E9B4}
AppName=弹幕API
AppVersion={#AppVersion}
AppPublisher=Danmu API
DefaultDirName={autopf}\DanmuApi
DefaultGroupName=弹幕API
DisableProgramGroupPage=yes
#if ArchName == "x86"
; 32-bit Windows only: a 64-bit machine should use its own package.
ArchitecturesAllowed=x86
#elif ArchName == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
; x64compatible also matches Arm64 Windows 11, so an existing x64 install keeps updating itself.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
PrivilegesRequired=admin
OutputDir={#OutputRoot}
OutputBaseFilename=DanmuApi-{#AppVersion}-win-{#ArchName}-setup
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
; The relay bytes come from this package, never from user-writable instance.endpoint.
Source: "{#SourceDir}\DanmuApi.App.exe"; DestDir: "{tmp}"; DestName: "DanmuApi.ExitRelay.exe"; Flags: dontcopy
Source: "{#SourceDir}\*.exe"; DestDir: "{app}"; Flags: ignoreversion; BeforeInstall: EnsureUpdateLease
Source: "{#SourceDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion; BeforeInstall: EnsureUpdateLease
Source: "{#SourceDir}\runtime-bundle\*"; DestDir: "{app}\runtime-bundle"; Flags: ignoreversion recursesubdirs createallsubdirs; BeforeInstall: EnsureUpdateLease
Source: "{#SourceDir}\git\*"; DestDir: "{app}\git"; Flags: ignoreversion recursesubdirs createallsubdirs; BeforeInstall: EnsureUpdateLease

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
type
  TRelayStartupInfo = record
    Size: LongWord;
    Reserved, Desktop, Title: LongWord;
    X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags: LongWord;
    ShowWindow, ReservedSize: Word;
    ReservedBytes, InputHandle, OutputHandle, ErrorHandle: LongWord;
  end;
  TRelayProcessInformation = record
    ProcessHandle, ThreadHandle, ProcessId, ThreadId: LongWord;
  end;
const
  LegacyCode = '{E56FCF01-A357-33A6-8C20-41C23E8D1B97}';
  LegacyUninstall = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{E56FCF01-A357-33A6-8C20-41C23E8D1B97}';
  CurrentUninstall = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{D6F1E4DB-9C73-4DE2-B985-9DAF96B8E9B4}_is1';
// A 32-bit build cannot read the 64-bit registry view, so its uninstall and legacy lookups use HKLM32.
#if ArchName == "x86"
  RegRoot = HKLM32;
#else
  RegRoot = HKLM64;
#endif
var
  LegacyPresent: Boolean;
  HadAutostart: Boolean;
  LegacyDirectory: String;
  UpdateLeaseHandle: LongWord;
  UpdateLeasePid: Integer;
  UpdateLeaseDirectory: String;
  UpdateLeaseStarted: Boolean;
  UpdateLeaseReleased: Boolean;

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
  if RegQueryStringValue(RegRoot, CurrentUninstall, 'DisplayVersion', Version) then
    if CompareVersion(Version, '{#AppVersion}') > 0 then begin
      MsgBox('已安装更新版本，不能降级。', mbError, MB_OK);
      exit;
    end;
  LegacyPresent := MsiQueryProductState(LegacyCode) = 5;
  if LegacyPresent then begin
    if not RegQueryStringValue(RegRoot, LegacyUninstall, 'DisplayName', Name) or
       not RegQueryStringValue(RegRoot, LegacyUninstall, 'DisplayVersion', Version) then begin
      MsgBox('旧版 MSI 身份信息不完整，停止迁移。', mbError, MB_OK);
      exit;
    end;
    if (Name <> '弹幕API') or (Version <> '0.1.0') then begin
      MsgBox('旧版 MSI 元数据与发布记录不一致，停止迁移。', mbError, MB_OK);
      exit;
    end;
    RegQueryStringValue(RegRoot, LegacyUninstall, 'InstallLocation', LegacyDirectory);
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
function GetCurrentProcessId(): LongWord;
  external 'GetCurrentProcessId@kernel32.dll stdcall';
function GetExitCodeProcess(Handle: LongWord; var ExitCode: LongWord): Boolean;
  external 'GetExitCodeProcess@kernel32.dll stdcall';
function CreateProcess(ApplicationName, CommandLine: String;
  ProcessSecurity, ThreadSecurity: LongWord; InheritHandles: Boolean;
  CreationFlags: LongWord; Environment: String; CurrentDirectory: String;
  var StartupInfo: TRelayStartupInfo; var ProcessInfo: TRelayProcessInformation): Boolean;
  external 'CreateProcessW@kernel32.dll stdcall';
function GetRelayEnvironmentStrings(): LongWord;
  external 'GetEnvironmentStringsW@kernel32.dll stdcall';
function FreeRelayEnvironmentStrings(Environment: LongWord): Boolean;
  external 'FreeEnvironmentStringsW@kernel32.dll stdcall';
function RelayStringLength(Value: LongWord): Integer;
  external 'lstrlenW@kernel32.dll stdcall';
function CopyRelayString(Destination: String; Source: LongWord): LongWord;
  external 'lstrcpyW@kernel32.dll stdcall';

function IsRelayRuntimeEnvironment(Entry: String): Boolean;
begin
  Entry := Uppercase(Entry);
  Result := (Pos('DOTNET_', Entry) = 1) or (Pos('CORECLR_', Entry) = 1) or
    (Pos('COMPLUS_', Entry) = 1) or (Pos('COR_', Entry) = 1) or
    (Pos('COREHOST_', Entry) = 1);
end;

{ Copy while the Win32 snapshot is owned; CreateProcessW receives the independently
  owned Pascal UTF-16 string for the entire synchronous call. Never alter Setup's
  environment. Preserve non-runtime entries, including SystemRoot/TEMP and =C: keys. }
function BuildRelayEnvironmentBlock(): String;
var
  Snapshot, Cursor, WinError: LongWord;
  EntryLength, I, J, Count: Integer;
  Entry, Failure: String;
  Entries: TArrayOfString;
begin
  Result := '';
  Failure := '';
  Snapshot := GetRelayEnvironmentStrings();
  if Snapshot = 0 then
    RaiseException('无法读取安装中继的运行环境，错误码 ' + IntToStr(DLLGetLastError()));
  try
    try
      Cursor := Snapshot;
      EntryLength := RelayStringLength(Cursor);
      while EntryLength <> 0 do begin
        SetLength(Entry, EntryLength);
        if CopyRelayString(Entry, Cursor) = 0 then
          RaiseException('无法复制安装中继的运行环境，错误码 ' + IntToStr(DLLGetLastError()));
        if not IsRelayRuntimeEnvironment(Entry) then begin
          Count := GetArrayLength(Entries);
          SetArrayLength(Entries, Count + 1);
          Entries[Count] := Entry;
        end;
        Cursor := Cursor + (EntryLength + 1) * 2;
        EntryLength := RelayStringLength(Cursor);
      end;
      Count := GetArrayLength(Entries);
      SetArrayLength(Entries, Count + 1);
      Entries[Count] := 'DOTNET_EnableDiagnostics=0';
      { Windows requires a case-insensitively sorted, double-NUL terminated block. }
      for I := 1 to GetArrayLength(Entries) - 1 do begin
        Entry := Entries[I];
        J := I;
        while J > 0 do begin
          if CompareText(Entries[J - 1], Entry) <= 0 then break;
          Entries[J] := Entries[J - 1];
          J := J - 1;
        end;
        Entries[J] := Entry;
      end;
      for I := 0 to GetArrayLength(Entries) - 1 do
        Result := Result + Entries[I] + #0;
      Result := Result + #0;
    except
      Failure := GetExceptionMessage();
    end;
  finally
    if not FreeRelayEnvironmentStrings(Snapshot) then begin
      WinError := DLLGetLastError();
      if Failure <> '' then Failure := Failure + #13#10;
      Failure := Failure + '无法释放安装中继环境快照，错误码 ' + IntToStr(WinError);
    end;
  end;
  if Failure <> '' then RaiseException(Failure);
end;

{ Always execute package-owned relay bytes with the original user's token. The relay
  refuses elevated tokens, including initially-elevated Setup (where Inno cannot recover
  a non-elevated original user). Its fixed roaming lock probe also covers legacy app.lock.
  No lock or endpoint path is inferred from Setup's possibly different administrator profile. }
function RunOriginalUserRelay(Arguments: String; var ExitCode: Integer): Boolean;
begin
  ExtractTemporaryFile('DanmuApi.ExitRelay.exe');
  Result := ExecAsOriginalUser(ExpandConstant('{tmp}\DanmuApi.ExitRelay.exe'),
    Arguments, ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, ExitCode);
end;

{ 0 = both real locks available; 10 = instance held; all other outcomes block install. }
function ProbeOriginalUserInstance(var ExitCode: Integer): Boolean;
begin
  Result := RunOriginalUserRelay('--installer-instance-probe', ExitCode);
end;

function UpdateLeaseFailure(): String;
var
  Detail: AnsiString;
begin
  Result := '';
  if FileExists(UpdateLeaseDirectory + '\error.txt') then begin
    if not LoadStringFromFile(UpdateLeaseDirectory + '\error.txt', Detail) then
      Result := '无法读取安装锁中继的失败诊断'
    else
      Result := String(Detail);
    Log('Update lease failure: ' + Result);
  end;
end;

procedure EnsureUpdateLease();
var
  Detail: String;
begin
  if not UpdateLeaseStarted or UpdateLeaseReleased then exit;
  Detail := UpdateLeaseFailure();
  if Detail <> '' then RaiseException('安装锁中继失败：' + Detail);
  if WaitForSingleObject(UpdateLeaseHandle, 0) <> 258 then
    RaiseException('安装锁中继已退出，无法证明原用户实例锁仍被持有；停止替换文件。');
end;

function BeginUpdateLease(ParentPid: Integer; ReadyPath: String): String;
var
  Prepared: AnsiString;
  Detail, Arguments, RelayPath, CommandLine, EnvironmentBlock: String;
  Waited: Integer;
  WinError: LongWord;
  Started: Boolean;
  StartupInfo: TRelayStartupInfo;
  ProcessInfo: TRelayProcessInformation;
begin
  Result := '';
  UpdateLeaseDirectory := ExpandConstant('{tmp}\update-lease');
  if DirExists(UpdateLeaseDirectory) or not CreateDir(UpdateLeaseDirectory) then begin
    Result := '无法创建专用安装锁中继目录'; exit;
  end;
  ExtractTemporaryFile('DanmuApi.ExitRelay.exe');
  Arguments := '--installer-update-lease ' + IntToStr(ParentPid) + ' ' +
    IntToStr(GetCurrentProcessId()) + ' "' + ExpandConstant('{app}') + '" "' +
    ReadyPath + '" "' + UpdateLeaseDirectory + '"';
  RelayPath := ExpandConstant('{tmp}\DanmuApi.ExitRelay.exe');
  CommandLine := '"' + RelayPath + '" ' + Arguments;
  StartupInfo.Size := 68;
  StartupInfo.Flags := 1;
  StartupInfo.ShowWindow := SW_HIDE;
  EnvironmentBlock := BuildRelayEnvironmentBlock();
  Started := False;
  try
    Started := CreateProcess(RelayPath, CommandLine, 0, 0, False, $08000400,
      EnvironmentBlock, ExpandConstant('{tmp}'), StartupInfo, ProcessInfo);
    if not Started then begin
      WinError := DLLGetLastError();
      Result := '无法启动随包的安装锁中继，错误码 ' + IntToStr(WinError); exit;
    end;
    UpdateLeaseHandle := ProcessInfo.ProcessHandle;
    UpdateLeasePid := ProcessInfo.ProcessId;
    UpdateLeaseStarted := True;
  finally
    if Started then
      if not CloseHandle(ProcessInfo.ThreadHandle) then begin
        WinError := DLLGetLastError();
        Detail := '无法关闭安装锁中继的启动线程句柄，错误码 ' + IntToStr(WinError);
        Log('ERROR: ' + Detail);
        if Result <> '' then Result := Result + #13#10;
        Result := Result + Detail;
      end;
  end;
  if Result <> '' then exit;
  Waited := 0;
  while not FileExists(UpdateLeaseDirectory + '\prepared') do begin
    Detail := UpdateLeaseFailure();
    if Detail <> '' then begin Result := '原用户安装检查失败：' + Detail; exit; end;
    if WaitForSingleObject(UpdateLeaseHandle, 0) <> 258 then begin
      Detail := UpdateLeaseFailure();
      Result := '安装锁中继在准备完成前退出。' + Detail; exit;
    end;
    if Waited >= 130000 then begin Result := '原用户安装锁准备超时，停止安装。'; exit; end;
    Sleep(100);
    Waited := Waited + 100;
  end;
  if not LoadStringFromFile(UpdateLeaseDirectory + '\prepared', Prepared) or
     (String(Prepared) <> IntToStr(UpdateLeasePid)) then begin
    Result := '安装锁中继准备回执身份不符'; exit;
  end;
  EnsureUpdateLease();
  Log('Original application exited; validated original-user modern and legacy locks held by packaged relay.');
end;

procedure FinishUpdateLease();
var
  Detail: String;
  Code: LongWord;
begin
  if not UpdateLeaseStarted or UpdateLeaseReleased then exit;
  EnsureUpdateLease();
  if not SaveStringToFile(UpdateLeaseDirectory + '\finish', 'finish', False) then
    RaiseException('无法通知安装锁中继释放实例锁');
  if WaitForSingleObject(UpdateLeaseHandle, 10000) <> 0 then
    RaiseException('安装锁中继未完成释放，请检查安装日志');
  Detail := UpdateLeaseFailure();
  if Detail <> '' then RaiseException('安装锁释放失败：' + Detail);
  if not GetExitCodeProcess(UpdateLeaseHandle, Code) or (Code <> 0) or
     not FileExists(UpdateLeaseDirectory + '\released') then
    RaiseException('安装锁中继未确认两把实例锁均成功释放');
  UpdateLeaseReleased := True;
  Log('Original-user installation locks released successfully.');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Command, ReadyPath: String;
  ParentPid: Integer;
  ExitCode: Integer;
begin
  Result := '';
  ParentPid := StrToIntDef(ExpandConstant('{param:UPDATEPARENT|0}'), 0);
  if ParentPid > 0 then begin
    ReadyPath := ExpandConstant('{param:UPDATEREADY|}');
    Result := BeginUpdateLease(ParentPid, ReadyPath);
    if Result <> '' then exit;
  end;
  if ParentPid <= 0 then begin
  if not ProbeOriginalUserInstance(ExitCode) then begin
    Result := '无法启动随包的原用户实例验证中继，未继续安装。'; exit;
  end;
  if (ExitCode <> 0) and (ExitCode <> 10) then begin
    Result := '无法在原用户普通权限下验证运行实例（退出码 ' + IntToStr(ExitCode) + '）。' + #13#10 +
              '请退出应用，以普通用户启动安装器后再授权 UAC；不要直接以管理员身份启动。'; exit;
  end;
  if ExitCode = 10 then begin
    if MsgBox('弹幕 API 正在运行，替换程序文件前需要先退出。' + #13#10 + #13#10 +
              '点「是」：用随包中继请求安全停止服务并退出（最多等 60 秒）。' + #13#10 +
              '0.5.13 或更早版本不支持自动退出，请从托盘选择「退出」并手动停止旧服务。' + #13#10 +
              '点「否」：取消安装，稍后完全退出应用再重试。',
              mbConfirmation, MB_YESNO) <> IDYES then begin
      Result := '安装已取消：请先从托盘完全退出弹幕 API。'; exit;
    end;
    if not RunOriginalUserRelay('--installer-request-exit 60', ExitCode) then begin
      Result := '无法启动原用户退出中继，未继续安装。'; exit;
    end;
    if ExitCode <> 0 then begin
      Result := '运行实例未完成安全退出（退出码 ' + IntToStr(ExitCode) + '）。' + #13#10 +
                '0.5.13 或更早版本不支持自动退出；请从托盘完全退出并手动停止服务后重新安装。'; exit;
    end;
    { OK merely accepts a request. Only a fresh original-user real-lock probe proves completion. }
    if not ProbeOriginalUserInstance(ExitCode) then begin
      Result := '退出后的原用户锁复核失败，未继续安装。'; exit;
    end;
    if ExitCode <> 0 then begin
      Result := '弹幕 API 仍在运行或无法验证退出完成，请完全退出后重新安装。'; exit;
    end;
  end;
  end;
  HadAutostart := False;
  if ParentPid <= 0 then
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
  if CurStep = ssInstall then EnsureUpdateLease();
  if CurStep = ssDone then FinishUpdateLease();
  if (CurStep = ssPostInstall) and HadAutostart then
    if not RegWriteStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DanmuApi', '"' + ExpandConstant('{app}\DanmuApi.App.exe') + '" --autostart') then
      RaiseException('刷新开机启动入口失败，请在应用设置中重新设置。');
end;

procedure DeinitializeSetup();
var
  Detail: String;
begin
  if UpdateLeaseStarted and not UpdateLeaseReleased then begin
    if not SaveStringToFile(UpdateLeaseDirectory + '\finish', 'finish', False) then
      Log('ERROR: Failed to signal installation-lock cleanup.');
    if WaitForSingleObject(UpdateLeaseHandle, 10000) <> 0 then
      Log('ERROR: Installation-lock relay did not finish cleanup within 10 seconds.');
    Detail := UpdateLeaseFailure();
    if Detail <> '' then Log('ERROR: Installation-lock cleanup: ' + Detail);
  end;
  if UpdateLeaseHandle <> 0 then begin
    if not CloseHandle(UpdateLeaseHandle) then Log('ERROR: Failed to close installation-lock relay handle.');
    UpdateLeaseHandle := 0;
  end;
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
