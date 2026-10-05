namespace DanmuApi.Tests;

public sealed class InstallerRelayEnvironmentTests
{
    // The Pascal code runs in Inno, not the test host. These contract checks guard the
    // security boundary; scratch/envnativefixture supplies actual Win32/NUL evidence.
    [Fact]
    public void ManualHelperUsesOwnedUnicodeChildBlockWithoutMutatingSetupEnvironment()
    {
        var script = InstallerScript();
        var declaration = Section(script, "function CreateProcess(", "function GetRelayEnvironmentStrings(");
        Assert.Contains("ApplicationName, CommandLine: String", declaration);
        Assert.Contains("CreationFlags: LongWord; Environment: String; CurrentDirectory: String", declaration);
        Assert.Contains("CreateProcessW@kernel32.dll stdcall", declaration);
        var manual = Section(script, "function StartManualExit(", "function FinishManualExit(");
        Assert.Contains("EnvironmentBlock := BuildRelayEnvironmentBlock()", manual);
        Assert.Contains("True, $08000400,", manual); // inherited std handles | NO_WINDOW | UNICODE_ENVIRONMENT
        Assert.Contains("EnvironmentBlock, ExpandConstant('{tmp}'), StartupInfo, ProcessInfo", manual);
        Assert.DoesNotContain("SetEnvironmentVariableW", script);
        Assert.DoesNotContain("RestoreRelayEnvironment", script);
        Assert.DoesNotContain("IsolateRelayEnvironment", script);
    }

    [Fact]
    public void EveryRuntimePrefixIsFilteredCaseInsensitivelyBeforeDiagnosticsAreDisabled()
    {
        var script = InstallerScript();
        var filter = Section(script, "function IsRelayRuntimeEnvironment(", "function BuildRelayEnvironmentBlock(");
        Assert.Contains("Entry := Uppercase(Entry)", filter);
        foreach (var prefix in new[] { "DOTNET_", "CORECLR_", "COMPLUS_", "COR_", "COREHOST_" })
            Assert.Contains($"Pos('{prefix}', Entry) = 1", filter);
        var block = Section(script, "function BuildRelayEnvironmentBlock(", "procedure LogManualNativeFailure(");
        Assert.Contains("if not IsRelayRuntimeEnvironment(Entry) then", block);
        Assert.Contains("Entries[Count] := 'DOTNET_EnableDiagnostics=0'", block);
        Assert.Contains("Result := Result + Entries[I] + #0", block);
        Assert.Contains("Result := Result + #0", block);
    }

    [Fact]
    public void SnapshotIsCopiedBeforeFreeAndStartupThreadIsClosedInsideFinally()
    {
        var script = InstallerScript();
        var block = Section(script, "function BuildRelayEnvironmentBlock(", "procedure LogManualNativeFailure(");
        Assert.Contains("SetLength(Entry, EntryLength)", block);
        Assert.Contains("CopyRelayString(Entry, Cursor)", block);
        Assert.Contains("finally\n    if not FreeRelayEnvironmentStrings(Snapshot)", block);
        Assert.Contains("if Failure <> '' then RaiseException(Failure)", block);
        var manual = Section(script, "function StartManualExit(", "function FinishManualExit(");
        Assert.Contains("if not Started then begin\n      WinError := NativeGetLastError()", manual);
        Assert.Contains("finally\n    if Started then\n      if not CloseHandle(ProcessInfo.ThreadHandle)", manual);
        Assert.Contains("LogManualNativeFailure('start', 'thread_close_failed', WinError)", manual);
        Assert.Contains("Result := ManualExitFailure('start', 'handle_cleanup_failed')", manual);
        Assert.Contains("ManualExitHandle := ProcessInfo.ProcessHandle", manual);
        Assert.Contains("ManualExitStarted := True", manual);
    }

    [Fact]
    public void ManualHelperRetainsProtectedPackageOwnedBytesAndNativeProcessHandle()
    {
        var script = InstallerScript();
        var manual = Section(script, "function StartManualExit(", "function FinishManualExit(");
        Assert.Contains("ExtractTemporaryFile('DanmuApi.InstallExit.exe')", manual);
        Assert.Contains("RelayPath := ExpandConstant('{tmp}\\DanmuApi.InstallExit.exe')", manual);
        Assert.Contains("--installer-manual-exit", manual);
        Assert.Contains("IntToStr(GetCurrentProcessId())", manual);
        Assert.Contains("ManualExitHandle := ProcessInfo.ProcessHandle", manual);
        Assert.Contains("CreateProcess(", manual);
        Assert.DoesNotContain("ExecAsOriginalUser", script);
        Assert.DoesNotContain("ewNoWait", script);
        Assert.Contains("BeforeInstall: EnsureManualExit", script);
    }

    [Fact]
    public void EnvironmentSortUsesOrdinalUnicodeKeysAndLeavesWholeEntriesUnchanged()
    {
        var script = InstallerScript();
        var native = Section(script, "function NativeCompareRelayKeys(", "function IsRelayRuntimeEnvironment(");
        Assert.Contains("LeftKey: String; LeftCount: Integer", native);
        Assert.Contains("RightKey: String; RightCount: Integer; IgnoreCase: Boolean", native);
        Assert.Contains("CompareStringOrdinal@kernel32.dll stdcall", native);
        Assert.DoesNotContain("var LeftKey", native);
        Assert.DoesNotContain("var RightKey", native);
        var key = Section(script, "function RelayEnvironmentKey(", "function CompareRelayEnvironmentKeys(");
        Assert.Contains("if Entry[1] = '=' then StartIndex := 2", key);
        Assert.Contains("for I := StartIndex to Length(Entry)", key);
        Assert.Contains("if Entry[I] = '=' then begin", key);
        Assert.Contains("Result := Copy(Entry, 1, I - 1)", key);
        Assert.Contains("RaiseException", key);
        Assert.DoesNotContain("Pos(", key);
        var compare = Section(script, "function CompareRelayEnvironmentKeys(", "function BuildRelayEnvironmentBlock(");
        Assert.Contains("LeftKey := RelayEnvironmentKey(Left)", compare);
        Assert.Contains("RightKey := RelayEnvironmentKey(Right)", compare);
        Assert.Contains("NativeCompareRelayKeys(LeftKey, Length(LeftKey), RightKey, Length(RightKey), True)", compare);
        Assert.Contains("if Order = 0 then begin\n    WinError := NativeGetLastError()", compare);
        Assert.Contains("ordinal_compare_failed win32=", compare);
        Assert.Contains("RaiseException", compare);
        Assert.Contains("Result := Order - 2", compare);
        var block = Section(script, "function BuildRelayEnvironmentBlock(", "procedure LogManualNativeFailure(");
        Assert.Contains("if CompareRelayEnvironmentKeys(Entries[J - 1], Entry) <= 0 then break", block);
        Assert.Contains("Entries[J] := Entries[J - 1]", block);
        Assert.Contains("Entries[J] := Entry", block);
        Assert.Contains("Result := Result + Entries[I] + #0", block);
        Assert.DoesNotContain("CompareText(", block);
    }

    // Native vectors establish the Windows rule. The source contract above binds
    // the production Pascal implementation; these vectors alone are not an Inno execution test.
    [Theory]
    [InlineData("CommonProgramFiles=C:\\A", "CommonProgramFiles", "CommonProgramFiles(x86)=C:\\B", "CommonProgramFiles(x86)", -1)]
    [InlineData("=C:=C:\\A", "=C:", "=D:=D:\\B", "=D:", -1)]
    [InlineData("=C:=C:\\first=tail", "=C:", "=c:=C:\\second", "=c:", 0)]
    [InlineData("PATH=zzz", "PATH", "path=aaa", "path", 0)]
    [InlineData("数据键=first=tail", "数据键", "数据键(扩展)=second", "数据键(扩展)", -1)]
    public void NativeOrdinalEnvironmentKeysRespectPrefixesDriveEntriesAndUnicodeCodeUnits(
        string leftEntry, string leftKey, string rightEntry, string rightKey, int expected)
    {
        Assert.Equal(leftKey, EnvironmentKeyForVector(leftEntry));
        Assert.Equal(rightKey, EnvironmentKeyForVector(rightEntry));
        var order = CompareStringOrdinal(leftKey, leftKey.Length, rightKey, rightKey.Length, true);
        Assert.InRange(order, 1, 3);
        Assert.Equal(expected, order - 2);
    }

    private static string EnvironmentKeyForVector(string entry)
    {
        var start = entry.Length > 0 && entry[0] == '=' ? 1 : 0;
        for (var index = start; index < entry.Length; index++)
        {
            if (entry[index] != '=') continue;
            Assert.True(index > start);
            return entry[..index];
        }
        throw new FormatException("Environment vector lacks a key delimiter.");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    private static extern int CompareStringOrdinal(string left, int leftCount, string right, int rightCount,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool ignoreCase);

    private static string InstallerScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "installer", "DanmuApi.iss");
            if (File.Exists(path)) return File.ReadAllText(path).Replace("\r\n", "\n");
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Cannot locate installer/DanmuApi.iss from the test output directory.");
    }

    private static string Section(string script, string start, string end)
    {
        var begin = script.IndexOf(start, StringComparison.Ordinal);
        Assert.True(begin >= 0, $"Installer declaration missing: {start}");
        var finish = script.IndexOf(end, begin + start.Length, StringComparison.Ordinal);
        Assert.True(finish > begin, $"Installer section end missing: {end}");
        return script[begin..finish];
    }
}
