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
; Only the signed application bytes in this package may implement the manual-exit protocol.
Source: "{#SourceDir}\DanmuApi.App.exe"; DestDir: "{tmp}"; DestName: "DanmuApi.InstallExit.exe"; Flags: dontcopy
Source: "{#SourceDir}\*.exe"; DestDir: "{app}"; Flags: ignoreversion; BeforeInstall: EnsureManualExit
Source: "{#SourceDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion; BeforeInstall: EnsureManualExit
Source: "{#SourceDir}\runtime-bundle\*"; DestDir: "{app}\runtime-bundle"; Flags: ignoreversion recursesubdirs createallsubdirs; BeforeInstall: EnsureManualExit
Source: "{#SourceDir}\git\*"; DestDir: "{app}\git"; Flags: ignoreversion recursesubdirs createallsubdirs; BeforeInstall: EnsureManualExit

[Icons]
Name: "{autoprograms}\弹幕API"; Filename: "{app}\DanmuApi.App.exe"; AppUserModelID: "DanmuApi.Windows"
Name: "{autodesktop}\弹幕API"; Filename: "{app}\DanmuApi.App.exe"; Tasks: desktopicon; AppUserModelID: "DanmuApi.Windows"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："

[InstallDelete]
Type: files; Name: "{autoprograms}\Danmu API.lnk"
Type: files; Name: "{autodesktop}\Danmu API.lnk"

[Run]
; Postinstall uses Inno's default RunAsOriginalUser context, not the helper's captured
; target token. If Setup was already elevated at launch, Inno cannot recover an
; unelevated original user; never substitute another profile/token as a fallback.
Filename: "{app}\DanmuApi.App.exe"; Description: "启动 弹幕API"; Flags: postinstall nowait skipifsilent unchecked; Check: ManualExitAllowsPostInstallRun

[Code]
type
  { Inno's script host is 32-bit for every target architecture: 68/16/12 bytes. }
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
  TManualSecurityAttributes = record
    Size, Descriptor, InheritHandle: LongWord;
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
  LegacyDirectory: String;
  ManualExitHandle, ManualExitPid: LongWord;
  ManualExitDirectory, ManualExitLog: String;
  ManualExitStarted, ManualExitPrepared, ManualExitReleased: Boolean;
  ManualExitCancelExpected: Boolean;

function CreateFile(Name: String; Access, Share, Security, Creation, Flags, Template: LongWord): LongWord;
  external 'CreateFileW@kernel32.dll stdcall';
function CreateInheritedManualFile(Name: String; Access, Share: LongWord;
  var Security: TManualSecurityAttributes; Creation, Flags, Template: LongWord): LongWord;
  external 'CreateFileW@kernel32.dll stdcall';
function CreateManualDirectory(Name: String; Security: LongWord): Boolean;
  external 'CreateDirectoryW@kernel32.dll stdcall';
function CloseHandle(Handle: LongWord): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function NativeGetLastError(): LongWord;
  external 'GetLastError@kernel32.dll stdcall';
function ReadManualFile(Handle: LongWord; Buffer: AnsiString; Count: LongWord;
  var BytesRead: LongWord; Overlapped: LongWord): Boolean;
  external 'ReadFile@kernel32.dll stdcall';
function WriteManualFile(Handle: LongWord; Buffer: AnsiString; Count: LongWord;
  var BytesWritten: LongWord; Overlapped: LongWord): Boolean;
  external 'WriteFile@kernel32.dll stdcall';
function MoveManualFile(ExistingPath, NewPath: String): Boolean;
  external 'MoveFileW@kernel32.dll stdcall';

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
function NativeCompareRelayKeys(LeftKey: String; LeftCount: Integer;
  RightKey: String; RightCount: Integer; IgnoreCase: Boolean): Integer;
  external 'CompareStringOrdinal@kernel32.dll stdcall';

function IsRelayRuntimeEnvironment(Entry: String): Boolean;
begin
  Entry := Uppercase(Entry);
  Result := (Pos('DOTNET_', Entry) = 1) or (Pos('CORECLR_', Entry) = 1) or
    (Pos('COMPLUS_', Entry) = 1) or (Pos('COR_', Entry) = 1) or
    (Pos('COREHOST_', Entry) = 1);
end;

{ String indexes/Copy/Length are UTF-16 code-unit based. Inno Pos may return an
  ANSI byte index, so it must not locate a delimiter in a Unicode environment entry.
  Ordinary entries use their first '='; drive entries such as '=C:=C:\dir' use the second. }
function RelayEnvironmentKey(Entry: String): String;
var
  I, StartIndex: Integer;
begin
  if Length(Entry) = 0 then RaiseException('安装辅助程序环境变量为空');
  StartIndex := 1;
  if Entry[1] = '=' then StartIndex := 2;
  for I := StartIndex to Length(Entry) do begin
    if Entry[I] = '=' then begin
      if I = StartIndex then RaiseException('安装辅助程序环境变量键无效');
      Result := Copy(Entry, 1, I - 1);
      exit;
    end;
  end;
  RaiseException('安装辅助程序环境变量缺少分隔符');
end;

function CompareRelayEnvironmentKeys(Left, Right: String): Integer;
var
  LeftKey, RightKey: String;
  Order: Integer;
  WinError: LongWord;
begin
  LeftKey := RelayEnvironmentKey(Left);
  RightKey := RelayEnvironmentKey(Right);
  Order := NativeCompareRelayKeys(LeftKey, Length(LeftKey), RightKey, Length(RightKey), True);
  if Order = 0 then begin
    WinError := NativeGetLastError();
    Log('ERROR: installer-manual-exit phase=environment reason=ordinal_compare_failed win32=' + IntToStr(WinError));
    RaiseException('无法按键名排序安装辅助程序环境，错误码 ' + IntToStr(WinError));
  end;
  if (Order < 1) or (Order > 3) then
    RaiseException('安装辅助程序环境键比较返回无效结果');
  Result := Order - 2;
end;

{ Copy while the Win32 snapshot is owned. Preserve non-runtime entries including
  SystemRoot/TEMP and =C: keys; never change Setup's own environment. }
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
    RaiseException('无法读取安装辅助程序的运行环境，错误码 ' + IntToStr(NativeGetLastError()));
  try
    try
      Cursor := Snapshot;
      EntryLength := RelayStringLength(Cursor);
      while EntryLength <> 0 do begin
        SetLength(Entry, EntryLength);
        if CopyRelayString(Entry, Cursor) = 0 then
          RaiseException('无法复制安装辅助程序的运行环境，错误码 ' + IntToStr(NativeGetLastError()));
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
      { Stable Windows ordinal key-name order, independently owned UTF-16, double NUL.
        Comparing full entries is wrong for CommonProgramFiles vs CommonProgramFiles(x86). }
      for I := 1 to GetArrayLength(Entries) - 1 do begin
        Entry := Entries[I];
        J := I;
        while J > 0 do begin
          if CompareRelayEnvironmentKeys(Entries[J - 1], Entry) <= 0 then break;
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
      WinError := NativeGetLastError();
      if Failure <> '' then Failure := Failure + #13#10;
      Failure := Failure + '无法释放安装辅助程序环境快照，错误码 ' + IntToStr(WinError);
    end;
  end;
  if Failure <> '' then RaiseException(Failure);
end;

procedure LogManualNativeFailure(Phase, Reason: String; WinError: LongWord);
begin
  Log('ERROR: installer-manual-exit phase=' + Phase + ' reason=' + Reason +
    ' win32=' + IntToStr(WinError));
end;

{ Native reads are bounded even if the helper failed before writing any marker.
  Sharing permits reading the inherited stdout/stderr file while the helper lives. }
function ReadBoundedManualFile(Path: String; Limit: Integer; var Text: AnsiString): Boolean;
var
  Handle, BytesRead, WinError: LongWord;
begin
  Result := False;
  Text := '';
  Handle := CreateFile(Path, $80000000, 3, 0, 3, $80, 0);
  if Handle = $FFFFFFFF then begin
    WinError := NativeGetLastError();
    LogManualNativeFailure('diagnostic', 'open_failed', WinError); exit;
  end;
  try
    SetLength(Text, Limit + 1);
    if not ReadManualFile(Handle, Text, Limit + 1, BytesRead, 0) then begin
      WinError := NativeGetLastError();
      LogManualNativeFailure('diagnostic', 'read_failed', WinError); exit;
    end;
    SetLength(Text, BytesRead);
    Result := True;
  finally
    if not CloseHandle(Handle) then begin
      WinError := NativeGetLastError();
      LogManualNativeFailure('diagnostic', 'close_failed', WinError);
      Result := False;
    end;
  end;
end;

procedure LogManualDiagnosticFile(Path: String);
var
  Text: AnsiString;
  Line: String;
  I, P: Integer;
  Safe: Boolean;
begin
  if not FileExists(Path) then exit;
  if not ReadBoundedManualFile(Path, 8192, Text) then exit;
  if Length(Text) > 8192 then begin
    Log('ERROR: installer-manual-exit phase=diagnostic reason=output_bounded limit=8192');
    SetLength(Text, 8192);
    { Do not log a partially read final diagnostic. }
    while (Length(Text) > 0) and (Text[Length(Text)] <> #10) do
      SetLength(Text, Length(Text) - 1);
  end;
  while Length(Text) > 0 do begin
    P := Pos(#10, Text);
    if P = 0 then P := Length(Text) + 1;
    Line := String(Copy(Text, 1, P - 1));
    Delete(Text, 1, P);
    if (Length(Line) > 0) and (Line[Length(Line)] = #13) then
      SetLength(Line, Length(Line) - 1);
    { Only the helper's fixed safe phase/reason/type/HRESULT format enters logs.
      Arbitrary runtime stderr, exception bodies, environment and commands do not. }
    Safe := (Length(Line) <= 512) and
      (Pos('installer-manual-exit phase=', Line) = 1) and
      (Pos(' reason=', Line) > 0) and (Pos(' type=', Line) > 0) and
      (Pos(' HRESULT=', Line) > 0);
    for I := 1 to Length(Line) do
      if (Ord(Line[I]) < 32) or (Ord(Line[I]) > 126) then Safe := False;
    if Safe then Log(Line)
    else if Line <> '' then
      Log('ERROR: installer-manual-exit phase=diagnostic reason=non_protocol_output_omitted');
  end;
end;

procedure LogManualDiagnostics();
begin
  if ManualExitDirectory <> '' then
    LogManualDiagnosticFile(ManualExitDirectory + '\error.txt');
  if ManualExitLog <> '' then LogManualDiagnosticFile(ManualExitLog);
end;

function ManualExitFailure(Phase, Reason: String): String;
begin
  Log('ERROR: installer-manual-exit phase=' + Phase + ' reason=' + Reason);
  LogManualDiagnostics();
  Result := '无法确认弹幕 API 已安全退出或安装保护已完成，已停止安装。' + #13#10 +
    '请完全退出应用后重新运行安装器；详细原因请查看安装日志。';
end;

function ReadManualMarker(Name: String; var Present: Boolean): Boolean;
var
  Text: AnsiString;
begin
  Present := False;
  Result := True;
  if not FileExists(ManualExitDirectory + '\' + Name) then exit;
  Result := ReadBoundedManualFile(ManualExitDirectory + '\' + Name, 32, Text);
  if not Result then exit;
  if String(Text) <> Name then begin
    Log('ERROR: installer-manual-exit phase=marker reason=invalid_' + Name);
    Result := False; exit;
  end;
  Present := True;
end;

function SignalManualExit(Name: String): Boolean;
var
  Handle, BytesWritten, WinError: LongWord;
  Present: Boolean;
  Text: AnsiString;
begin
  { A repeated finish is idempotent only after validating its fixed contents. }
  Result := ReadManualMarker(Name, Present);
  if not Result or Present then exit;
  Result := False;
  Handle := CreateFile(ManualExitDirectory + '\' + Name + '.pending', $40000000, 0, 0, 1, $80, 0);
  if Handle = $FFFFFFFF then begin
    WinError := NativeGetLastError();
    LogManualNativeFailure('signal', Name + '_create_failed', WinError); exit;
  end;
  try
    Text := AnsiString(Name);
    if not WriteManualFile(Handle, Text, Length(Text), BytesWritten, 0) then begin
      WinError := NativeGetLastError();
      LogManualNativeFailure('signal', Name + '_write_failed', WinError); exit;
    end;
    Result := BytesWritten = LongWord(Length(Text));
    if not Result then
      Log('ERROR: installer-manual-exit phase=signal reason=' + Name + '_short_write expected=' +
        IntToStr(Length(Text)) + ' written=' + IntToStr(BytesWritten));
  finally
    if not CloseHandle(Handle) then begin
      WinError := NativeGetLastError();
      LogManualNativeFailure('signal', Name + '_close_failed', WinError);
      Result := False;
    end;
  end;
  if not Result then exit;
  { Publish only closed, complete contents; the helper must never see an empty signal. }
  Result := MoveManualFile(ManualExitDirectory + '\' + Name + '.pending',
    ManualExitDirectory + '\' + Name);
  if not Result then begin
    WinError := NativeGetLastError();
    LogManualNativeFailure('signal', Name + '_publish_failed', WinError);
  end;
end;

function LogManualExitCode(Phase: String; var Code: LongWord): Boolean;
var
  WinError: LongWord;
begin
  Result := GetExitCodeProcess(ManualExitHandle, Code);
  if not Result then begin
    WinError := NativeGetLastError();
    LogManualNativeFailure(Phase, 'exit_code_failed', WinError);
  end else
    Log('installer-manual-exit phase=' + Phase + ' exitCode=' + IntToStr(Int64(Code)));
end;

function ManualHelperAlive(Phase: String): Boolean;
var
  WaitResult, WinError, Code: LongWord;
begin
  Result := False;
  if ManualExitHandle = 0 then begin
    Log('ERROR: installer-manual-exit phase=' + Phase + ' reason=missing_process_handle'); exit;
  end;
  WaitResult := WaitForSingleObject(ManualExitHandle, 0);
  if WaitResult = 258 then begin Result := True; exit; end;
  if WaitResult = $FFFFFFFF then begin
    WinError := NativeGetLastError();
    LogManualNativeFailure(Phase, 'WAIT_FAILED', WinError);
  end else if WaitResult = 0 then begin
    LogManualExitCode(Phase, Code);
    Log('ERROR: installer-manual-exit phase=' + Phase + ' reason=helper_exited');
  end else
    Log('ERROR: installer-manual-exit phase=' + Phase + ' unexpectedWait=' + IntToStr(Int64(WaitResult)));
  LogManualDiagnostics();
end;

procedure EnsureManualExit();
begin
  if not ManualExitStarted or ManualExitReleased then exit;
  if not ManualExitPrepared then RaiseException(ManualExitFailure('copy', 'not_prepared'));
  if not ManualHelperAlive('copy') then RaiseException(ManualExitFailure('copy', 'helper_not_alive'));
  if FileExists(ManualExitDirectory + '\error.txt') then
    RaiseException(ManualExitFailure('copy', 'helper_error'));
end;

function StartManualExit(): String;
var
  RelayPath, CommandLine, EnvironmentBlock: String;
  OutputHandle, InputHandle, WinError: LongWord;
  StartupInfo: TRelayStartupInfo;
  ProcessInfo: TRelayProcessInformation;
  Security: TManualSecurityAttributes;
  Started: Boolean;
begin
  Result := '';
  ManualExitDirectory := ExpandConstant('{tmp}\manual-exit');
  ManualExitLog := ExpandConstant('{tmp}\manual-exit-stderr.log');
  if not CreateManualDirectory(ManualExitDirectory, 0) then begin
    WinError := NativeGetLastError();
    LogManualNativeFailure('start', 'state_directory_create_failed', WinError);
    Result := ManualExitFailure('start', 'state_directory_failed'); exit;
  end;
  { The elevated Setup creates the fixed directory under protected Inno temp;
    the helper verifies its creator, trust and exact target, not string short paths. }
  ExtractTemporaryFile('DanmuApi.InstallExit.exe');
  RelayPath := ExpandConstant('{tmp}\DanmuApi.InstallExit.exe');
  CommandLine := '"' + RelayPath + '" --installer-manual-exit ' +
    IntToStr(GetCurrentProcessId()) + ' "' + ExpandConstant('{app}') + '"';
  EnvironmentBlock := BuildRelayEnvironmentBlock();
  OutputHandle := 0;
  InputHandle := 0;
  Started := False;
  Security.Size := 12;
  Security.Descriptor := 0;
  Security.InheritHandle := 1;
  try
    OutputHandle := CreateInheritedManualFile(ManualExitLog, $40000000, 3, Security, 1, $80, 0);
    if OutputHandle = $FFFFFFFF then begin
      WinError := NativeGetLastError();
      OutputHandle := 0;
      LogManualNativeFailure('start', 'stderr_create_failed', WinError);
      Result := ManualExitFailure('start', 'stderr_unavailable'); exit;
    end;
    InputHandle := CreateInheritedManualFile('NUL', $80000000, 3, Security, 3, $80, 0);
    if InputHandle = $FFFFFFFF then begin
      WinError := NativeGetLastError();
      InputHandle := 0;
      LogManualNativeFailure('start', 'stdin_create_failed', WinError);
      Result := ManualExitFailure('start', 'stdin_unavailable'); exit;
    end;
    StartupInfo.Size := 68;
    { STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW. No pipe/buffer deadlock. }
    StartupInfo.Flags := $00000101;
    StartupInfo.ShowWindow := SW_HIDE;
    StartupInfo.InputHandle := InputHandle;
    StartupInfo.OutputHandle := OutputHandle;
    StartupInfo.ErrorHandle := OutputHandle;
    Started := CreateProcess(RelayPath, CommandLine, 0, 0, True, $08000400,
      EnvironmentBlock, ExpandConstant('{tmp}'), StartupInfo, ProcessInfo);
    if not Started then begin
      WinError := NativeGetLastError();
      LogManualNativeFailure('start', 'CreateProcess_failed', WinError);
      Result := ManualExitFailure('start', 'helper_start_failed'); exit;
    end;
    ManualExitHandle := ProcessInfo.ProcessHandle;
    ManualExitPid := ProcessInfo.ProcessId;
    ManualExitStarted := True;
    Log('installer-manual-exit phase=start helperPid=' + IntToStr(ManualExitPid));
  finally
    if Started then
      if not CloseHandle(ProcessInfo.ThreadHandle) then begin
        WinError := NativeGetLastError();
        LogManualNativeFailure('start', 'thread_close_failed', WinError);
        Result := ManualExitFailure('start', 'handle_cleanup_failed');
      end;
    if InputHandle <> 0 then
      if not CloseHandle(InputHandle) then begin
        WinError := NativeGetLastError();
        LogManualNativeFailure('start', 'stdin_close_failed', WinError);
        Result := ManualExitFailure('start', 'handle_cleanup_failed');
      end;
    if OutputHandle <> 0 then
      if not CloseHandle(OutputHandle) then begin
        WinError := NativeGetLastError();
        LogManualNativeFailure('start', 'stderr_close_failed', WinError);
        Result := ManualExitFailure('start', 'handle_cleanup_failed');
      end;
  end;
end;

function FinishManualExit(ExpectedCode: LongWord): String;
var
  WaitResult, WinError, Code: LongWord;
  Released: Boolean;
begin
  Result := '';
  if not ManualExitStarted or ManualExitReleased then exit;
  { Also signal finish on an error: never kill the helper to release held locks. }
  if not SignalManualExit('finish') then begin
    Result := ManualExitFailure('finish', 'signal_failed'); exit;
  end;
  WaitResult := WaitForSingleObject(ManualExitHandle, 10000);
  if WaitResult = $FFFFFFFF then begin
    WinError := NativeGetLastError();
    LogManualNativeFailure('finish', 'WAIT_FAILED', WinError);
  end;
  if WaitResult <> 0 then begin
    Log('ERROR: installer-manual-exit phase=finish waitResult=' + IntToStr(Int64(WaitResult)));
    Result := ManualExitFailure('finish', 'exit_not_confirmed'); exit;
  end;
  if not LogManualExitCode('finish', Code) then begin
    Result := ManualExitFailure('finish', 'exit_code_unavailable'); exit;
  end;
  LogManualDiagnostics();
  if Code <> ExpectedCode then begin
    Result := ManualExitFailure('finish', 'unexpected_exit_code'); exit;
  end;
  if (ExpectedCode = 0) and FileExists(ManualExitDirectory + '\error.txt') then begin
    Result := ManualExitFailure('finish', 'helper_error'); exit;
  end;
  if not ReadManualMarker('released', Released) or not Released then begin
    Result := ManualExitFailure('finish', 'release_not_confirmed'); exit;
  end;
  ManualExitReleased := True;
  Log('installer-manual-exit phase=finish reason=released');
end;

function BeginManualExit(): String;
var
  Waited: Integer;
  Prepared, Running, Requested: Boolean;
begin
  Result := '';
  if ManualExitStarted then begin
    Result := ManualExitFailure('start', 'session_already_started'); exit;
  end;
  try
    Result := StartManualExit();
  except
    Log('ERROR: installer-manual-exit phase=start exception=' + GetExceptionMessage());
    Result := ManualExitFailure('start', 'setup_exception');
  end;
  if Result <> '' then exit;
  Waited := 0;
  Requested := False;
  while True do begin
    if not ManualHelperAlive('prepare') then begin
      Result := ManualExitFailure('prepare', 'helper_not_alive'); exit;
    end;
    if FileExists(ManualExitDirectory + '\error.txt') then begin
      Result := ManualExitFailure('prepare', 'helper_error'); exit;
    end;
    if not ReadManualMarker('prepared', Prepared) then begin
      Result := ManualExitFailure('prepare', 'invalid_prepared'); exit;
    end;
    if Prepared then begin
      ManualExitPrepared := True;
      EnsureManualExit();
      Log('installer-manual-exit phase=prepare reason=prepared'); exit;
    end;
    if not Requested then begin
      if not ReadManualMarker('running', Running) then begin
        Result := ManualExitFailure('prepare', 'invalid_running'); exit;
      end;
      if Running then begin
        if not WizardSilent then
          if MsgBox('弹幕 API 正在运行。是否关闭应用并安装？' + #13#10 + #13#10 +
            '安装辅助程序会请求安全停止服务及受管内网穿透并退出，不会强杀应用。' + #13#10 +
            '0.5.13 或更早版本可能不支持自动退出；无法确认安全退出时将停止安装。' + #13#10 +
            '选择「否」将取消安装，不请求应用退出。', mbConfirmation, MB_YESNO) <> IDYES then begin
            ManualExitCancelExpected := True;
            Result := FinishManualExit(2);
            if Result = '' then Result := '安装已取消：未请求关闭弹幕 API。';
            exit;
          end;
        Log('installer-manual-exit phase=request reason=approved silent=' + IntToStr(Ord(WizardSilent)));
        if not SignalManualExit('request') then begin
          Result := ManualExitFailure('request', 'signal_failed'); exit;
        end;
        Requested := True;
        Waited := 0;
      end;
    end;
    if Waited >= 130000 then begin
      Result := ManualExitFailure('prepare', 'timeout'); exit;
    end;
    Sleep(100);
    Waited := Waited + 100;
  end;
end;

function ManualExitAllowsPostInstallRun(): Boolean;
begin
  if ManualExitStarted and (not ManualExitPrepared or not ManualExitReleased) then
    RaiseException(ManualExitFailure('run', 'release_not_confirmed'));
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  LockPath, ReadyPath: String;
  Handle, ParentHandle: LongWord;
  ParentPid: Integer;
  ExitCode: Integer;
begin
  Result := '';
  ParentPid := StrToIntDef(ExpandConstant('{param:UPDATEPARENT|0}'), 0);
  if ParentPid > 0 then begin
    { The complete v0.5.13 in-app UPDATEPARENT handshake stays in its original order.
      It never launches the manual helper or a new update lease. }
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
  end else begin
    { The helper holds the actual target user's locks. Setup must not probe its
      administrator profile after manual preparation (including fresh installs). }
    Result := BeginManualExit();
    if Result <> '' then exit;
  end;
  { Autostart repair belongs to the application's startup token, never Setup's
    potentially different administrator profile. }
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
var
  Failure: String;
begin
  if CurStep = ssInstall then EnsureManualExit();
  if CurStep = ssPostInstall then begin
    if ManualExitStarted then begin
      EnsureManualExit();
      Failure := FinishManualExit(0);
      if Failure <> '' then RaiseException(Failure);
    end;
  end;
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
begin
  EnsureManualExit();
end;

procedure DeinitializeSetup();
var
  WaitResult, WinError, Code: LongWord;
  Released: Boolean;
begin
  if ManualExitStarted and not ManualExitReleased then begin
    { Finish first, then wait on the exact CreateProcess process handle. Never
      terminate the lock-holding helper, including installer failure/cancellation. }
    if not SignalManualExit('finish') then
      Log('ERROR: installer-manual-exit phase=cleanup reason=finish_signal_failed');
    WaitResult := WaitForSingleObject(ManualExitHandle, 130000);
    if WaitResult = $FFFFFFFF then begin
      WinError := NativeGetLastError();
      LogManualNativeFailure('cleanup', 'WAIT_FAILED', WinError);
    end;
    if WaitResult = 0 then begin
      if LogManualExitCode('cleanup', Code) then begin
        if not ReadManualMarker('released', Released) or not Released then
          Log('ERROR: installer-manual-exit phase=cleanup reason=release_not_confirmed');
        if (Code <> 0) and not (ManualExitCancelExpected and (Code = 2)) then
          Log('ERROR: installer-manual-exit phase=cleanup reason=helper_failed');
      end;
    end else begin
      Log('ERROR: installer-manual-exit phase=cleanup reason=exit_not_confirmed waitResult=' + IntToStr(Int64(WaitResult)));
      LogManualExitCode('cleanup_status_only', Code);
    end;
    LogManualDiagnostics();
  end;
  if ManualExitHandle <> 0 then begin
    if not CloseHandle(ManualExitHandle) then begin
      WinError := NativeGetLastError();
      LogManualNativeFailure('cleanup', 'process_close_failed', WinError);
    end;
    ManualExitHandle := 0;
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
