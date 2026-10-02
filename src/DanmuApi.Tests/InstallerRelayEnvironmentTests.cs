namespace DanmuApi.Tests;

public sealed class InstallerRelayEnvironmentTests
{
    // The Pascal code runs in Inno, not the test host. These contract checks guard the
    // security boundary; scratch/envnativefixture supplies actual Win32/NUL evidence.
    [Fact]
    public void LeaseUsesOwnedUnicodeChildBlockWithoutMutatingSetupEnvironment()
    {
        var script = InstallerScript();
        var declaration = Section(script, "function CreateProcess(", "function GetRelayEnvironmentStrings(");
        Assert.Contains("ApplicationName, CommandLine: String", declaration);
        Assert.Contains("CreationFlags: LongWord; Environment: String; CurrentDirectory: String", declaration);
        Assert.Contains("CreateProcessW@kernel32.dll stdcall", declaration);
        var lease = Section(script, "function BeginUpdateLease(", "procedure FinishUpdateLease(");
        Assert.Contains("EnvironmentBlock := BuildRelayEnvironmentBlock()", lease);
        Assert.Contains("False, $08000400,", lease); // NO_WINDOW | UNICODE_ENVIRONMENT
        Assert.Contains("EnvironmentBlock, ExpandConstant('{tmp}'), StartupInfo, ProcessInfo", lease);
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
        var block = Section(script, "function BuildRelayEnvironmentBlock(", "function RunOriginalUserRelay(");
        Assert.Contains("if not IsRelayRuntimeEnvironment(Entry) then", block);
        Assert.Contains("Entries[Count] := 'DOTNET_EnableDiagnostics=0'", block);
        Assert.Contains("Result := Result + Entries[I] + #0", block);
        Assert.Contains("Result := Result + #0", block);
    }

    [Fact]
    public void SnapshotIsCopiedBeforeFreeAndStartupThreadIsClosedInsideFinally()
    {
        var script = InstallerScript();
        var block = Section(script, "function BuildRelayEnvironmentBlock(", "function RunOriginalUserRelay(");
        Assert.Contains("SetLength(Entry, EntryLength)", block);
        Assert.Contains("CopyRelayString(Entry, Cursor)", block);
        Assert.Contains("finally\n    if not FreeRelayEnvironmentStrings(Snapshot)", block);
        Assert.Contains("if Failure <> '' then RaiseException(Failure)", block);
        var lease = Section(script, "function BeginUpdateLease(", "procedure FinishUpdateLease(");
        Assert.Contains("if not Started then begin\n      WinError := DLLGetLastError()", lease);
        Assert.Contains("finally\n    if Started then\n      if not CloseHandle(ProcessInfo.ThreadHandle)", lease);
        Assert.Contains("Log('ERROR: ' + Detail)", lease);
        Assert.Contains("Result := Result + Detail", lease);
        Assert.Contains("UpdateLeaseHandle := ProcessInfo.ProcessHandle", lease);
        Assert.Contains("UpdateLeaseStarted := True", lease);
    }

    [Fact]
    public void OrdinaryRelayRetainsOriginalUserProtocolAndPackageOwnedBytes()
    {
        var script = InstallerScript();
        var manual = Section(script, "function RunOriginalUserRelay(", "function ProbeOriginalUserInstance(");
        Assert.Contains("ExtractTemporaryFile('DanmuApi.ExitRelay.exe')", manual);
        Assert.Contains("ExecAsOriginalUser(ExpandConstant('{tmp}\\DanmuApi.ExitRelay.exe')", manual);
        Assert.Contains("ewWaitUntilTerminated, ExitCode", manual);
        Assert.DoesNotContain("CreateProcess(", manual);
        Assert.DoesNotContain("BuildRelayEnvironmentBlock", manual);
        Assert.Contains("BeforeInstall: EnsureUpdateLease", script);
    }

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
