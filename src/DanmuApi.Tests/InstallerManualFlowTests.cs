namespace DanmuApi.Tests;

/// <summary>Source contracts only; these do not execute Setup or a user's application.</summary>
public sealed class InstallerManualFlowTests
{
    [Fact]
    public void ManualPreparationIsSeparateFromTheCompleteLegacyUpdateParentHandshake()
    {
        var script = InstallerScript();
        var prepare = Section(script, "function PrepareToInstall(", "procedure CurStepChanged(");
        var legacy = Section(prepare, "if ParentPid > 0 then begin", "end else begin");
        Assert.Contains("SaveStringToFile(ReadyPath, 'ready', False)", legacy);
        Assert.Contains("WaitForSingleObject(ParentHandle, 120000)", legacy);
        Assert.Contains("FileExists(ExtractFilePath(ReadyPath) + 'cancel')", legacy);
        Assert.Contains("{userappdata}\\DanmuApi\\instance.lock", legacy);
        Assert.Contains("{userappdata}\\DanmuApi\\app.lock", legacy);
        Assert.DoesNotContain("BeginManualExit", legacy);
        var manual = Section(prepare, "end else begin", "if LegacyPresent then begin");
        Assert.Contains("Result := BeginManualExit()", manual);
        Assert.Contains("if Result <> '' then exit", manual);
        Assert.DoesNotContain("LockPath", manual);
        Assert.DoesNotContain("BeginUpdateLease", script);
        Assert.DoesNotContain("ExecAsOriginalUser", script);
        Assert.DoesNotContain("ewNoWait", script);
    }

    [Fact]
    public void SetupLeavesAutostartSelfHealingToTheOriginalApplication()
    {
        var script = InstallerScript();
        // Uninstall's existing cleanup is a separate lifecycle, not Setup's install path.
        var setup = Section(script, "[Code]", "procedure CurUninstallStepChanged(");
        Assert.DoesNotContain("HadAutostart", script);
        Assert.DoesNotContain("HKCU", setup);
        Assert.DoesNotContain("Software\\Microsoft\\Windows\\CurrentVersion\\Run", setup);
        var run = Section(script, "[Run]", "[Code]");
        Assert.Contains("Flags: postinstall nowait skipifsilent unchecked", run);
        Assert.DoesNotContain("runascurrentuser", run.ToLowerInvariant());
        Assert.DoesNotContain("ExecAsOriginalUser", script);
    }

    [Fact]
    public void OnlyVerifiedRunningPromptsAndFreshInstallPreparedNeedsNoPrompt()
    {
        var begin = Section(InstallerScript(), "function BeginManualExit(", "function ManualExitAllowsPostInstallRun(");
        InOrder(begin, "ManualHelperAlive('prepare')", "ReadManualMarker('prepared', Prepared)",
            "if Prepared then begin", "ManualExitPrepared := True", "EnsureManualExit()",
            "ReadManualMarker('running', Running)", "if Running then begin", "if not WizardSilent then", "MsgBox(");
        Assert.Contains("if Prepared then begin\n      ManualExitPrepared := True;\n      EnsureManualExit();", begin);
        Assert.DoesNotContain("FileExists(ExpandConstant('{app}", begin);
        Assert.Contains("关闭应用并安装", begin);
    }

    [Fact]
    public void DecliningFinishesAndWaitsForExplicitCancelWithoutSendingAnExitRequest()
    {
        var begin = Section(InstallerScript(), "function BeginManualExit(", "function ManualExitAllowsPostInstallRun(");
        var declined = Section(begin, ") <> IDYES then begin", "Log('installer-manual-exit phase=request");
        InOrder(declined, "ManualExitCancelExpected := True", "FinishManualExit(2)", "安装已取消", "exit;");
        Assert.DoesNotContain("SignalManualExit('request')", declined);
        InOrder(begin, "if not WizardSilent then", "FinishManualExit(2)", "SignalManualExit('request')", "Requested := True");
    }

    [Fact]
    public void ChildUsesInheritedFileStdHandlesAndOwnedUnicodeEnvironmentInsteadOfAPipe()
    {
        var script = InstallerScript();
        var start = Section(script, "function StartManualExit(", "function FinishManualExit(");
        Assert.Contains("CreateManualDirectory(ManualExitDirectory, 0)", start);
        Assert.Contains("'state_directory_create_failed', WinError", start);
        Assert.Contains("Security.Size := 12", start);
        Assert.Contains("Security.InheritHandle := 1", start);
        Assert.Contains("StartupInfo.Size := 68", start);
        Assert.Contains("StartupInfo.Flags := $00000101", start);
        Assert.Contains("StartupInfo.InputHandle := InputHandle", start);
        Assert.Contains("StartupInfo.OutputHandle := OutputHandle", start);
        Assert.Contains("StartupInfo.ErrorHandle := OutputHandle", start);
        Assert.Contains("CreateInheritedManualFile('NUL'", start);
        Assert.Contains("{tmp}\\manual-exit-stderr.log", start);
        Assert.Contains("True, $08000400,", start);
        var environment = Section(script, "function BuildRelayEnvironmentBlock(", "procedure LogManualNativeFailure(");
        Assert.Contains("CompareRelayEnvironmentKeys(Entries[J - 1], Entry)", environment);
        Assert.DoesNotContain("CompareText(", environment);
        Assert.Contains("ManualExitHandle := ProcessInfo.ProcessHandle", start);
        Assert.Contains("ManualExitPid := ProcessInfo.ProcessId", start);
        Assert.DoesNotContain("CreatePipe", script);
        Assert.Contains("IntToStr(GetCurrentProcessId()) + ' \"' + ExpandConstant('{app}')", start);
    }

    [Fact]
    public void DiagnosticsAreBoundedSafeLinesWhileWaitFailuresAndRealExitCodesRemainVisible()
    {
        var script = InstallerScript();
        var diagnostics = Section(script, "procedure LogManualDiagnosticFile(", "function ReadManualMarker(");
        Assert.Contains("ReadBoundedManualFile(Path, 8192, Text)", diagnostics);
        Assert.Contains("Length(Line) <= 512", diagnostics);
        foreach (var field in new[] { "installer-manual-exit phase=", " reason=", " type=", " HRESULT=" })
            Assert.Contains($"Pos('{field}', Line)", diagnostics);
        Assert.Contains("if Safe then Log(Line)", diagnostics);
        Assert.Contains("non_protocol_output_omitted", diagnostics);
        Assert.Contains("LogManualDiagnosticFile(ManualExitLog)", diagnostics);
        Assert.Contains("ReadFile@kernel32.dll stdcall", script);
        Assert.Contains("GetLastError@kernel32.dll stdcall", script);
        var alive = Section(script, "function ManualHelperAlive(", "procedure EnsureManualExit(");
        Assert.Contains("if WaitResult = 258 then begin Result := True; exit; end", alive);
        Assert.Contains("if WaitResult = $FFFFFFFF then begin\n    WinError := NativeGetLastError()", alive);
        Assert.Contains("'WAIT_FAILED'", alive);
        Assert.Contains("else if WaitResult = 0 then begin\n    LogManualExitCode(Phase, Code)", alive);
        Assert.Contains("GetExitCodeProcess(ManualExitHandle, Code)", script);
        Assert.Contains("IntToStr(Int64(Code))", script);
    }

    [Fact]
    public void CopyAndProgressRequireHeldPreparationAndReleaseCompletesBeforePostinstallRun()
    {
        var script = InstallerScript();
        var guard = Section(script, "procedure EnsureManualExit(", "function StartManualExit(");
        Assert.Contains("if not ManualExitPrepared then RaiseException", guard);
        Assert.Contains("if not ManualHelperAlive('copy') then RaiseException", guard);
        Assert.Contains("FileExists(ManualExitDirectory + '\\error.txt')", guard);
        Assert.Equal(4, script.Split("BeforeInstall: EnsureManualExit").Length - 1);
        var steps = Section(script, "procedure CurStepChanged(", "procedure DeinitializeSetup(");
        InOrder(steps, "if CurStep = ssInstall then EnsureManualExit()", "if CurStep = ssPostInstall then begin",
            "EnsureManualExit()", "FinishManualExit(0)", "if Failure <> '' then RaiseException(Failure)");
        Assert.Contains("procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);\nbegin\n  EnsureManualExit()", steps);
        Assert.DoesNotContain("ssDone", steps);
        Assert.Contains("Check: ManualExitAllowsPostInstallRun", script);
        var finish = Section(script, "function FinishManualExit(", "function BeginManualExit(");
        InOrder(finish, "SignalManualExit('finish')", "WaitForSingleObject(ManualExitHandle, 10000)",
            "if not LogManualExitCode('finish', Code)", "if Code <> ExpectedCode", "ReadManualMarker('released', Released)",
            "ManualExitReleased := True");
        Assert.Contains("(ExpectedCode = 0) and FileExists", finish);
    }

    [Fact]
    public void SignalsPublishOnlyAfterACompleteClosedWriteAndCleanupNeverKillsTheHelper()
    {
        var script = InstallerScript();
        var signal = Section(script, "function SignalManualExit(", "function LogManualExitCode(");
        InOrder(signal, "Name + '.pending'", "WriteManualFile(Handle", "CloseHandle(Handle)",
            "if not Result then exit", "Result := MoveManualFile(");
        Assert.Contains("WinError := NativeGetLastError();\n    LogManualNativeFailure('signal', Name + '_publish_failed'", signal);
        var cleanup = Section(script, "procedure DeinitializeSetup(", "procedure CurUninstallStepChanged(");
        InOrder(cleanup, "SignalManualExit('finish')", "WaitForSingleObject(ManualExitHandle, 130000)",
            "LogManualExitCode('cleanup', Code)", "LogManualDiagnostics()", "CloseHandle(ManualExitHandle)");
        Assert.Contains("'WAIT_FAILED'", cleanup);
        Assert.Contains("release_not_confirmed", cleanup);
        Assert.Contains("ManualExitCancelExpected and (Code = 2)", cleanup);
        Assert.DoesNotContain("TerminateProcess", script);
        Assert.DoesNotContain("taskkill", script);
    }

    private static string InstallerScript()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "installer", "DanmuApi.iss");
            if (File.Exists(path)) return File.ReadAllText(path).Replace("\r\n", "\n");
        }
        throw new FileNotFoundException("Cannot locate the production installer template.");
    }

    private static string Section(string script, string start, string end)
    {
        var begin = script.IndexOf(start, StringComparison.Ordinal);
        Assert.True(begin >= 0, $"Missing section: {start}");
        var finish = script.IndexOf(end, begin + start.Length, StringComparison.Ordinal);
        Assert.True(finish > begin, $"Missing section end: {end}");
        return script[begin..finish];
    }

    private static void InOrder(string text, params string[] expected)
    {
        var position = 0;
        foreach (var item in expected)
        {
            var next = text.IndexOf(item, position, StringComparison.Ordinal);
            Assert.True(next >= position, $"Missing or out-of-order source contract: {item}");
            position = next + item.Length;
        }
    }
}
