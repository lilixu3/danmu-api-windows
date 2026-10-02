using System.Text;
using DanmuApi.App.Services;

namespace DanmuApi.Tests;

public sealed class ApplicationInstallerDiagnosticsTests
{
    private const int AccessDenied = unchecked((int)0x80070005);
    private static string LeaseLine(string phase = "verify", string reason = "tokenidentity", string type = "LeaseException", int hresult = AccessDenied) =>
        new InstallerUpdateLease.Diagnostic(phase, reason, type, hresult).Format();
    private static string Code(string line) => line[..line.IndexOf(';')];
    private static void WriteLog(string directory, string text) => File.WriteAllText(Path.Combine(directory, "installer.log"), text, new UTF8Encoding(true));

    [Theory]
    [InlineData(true, "初始化")]
    [InlineData(false, "安装")]
    public void FailureKeepsExitAndStageAndOnlyExposesVerifiedCodes(bool initializing, string stage)
    {
        using var directory = new TemporaryDirectory();
        var line = LeaseLine();
        WriteLog(directory.Path, "2026-10-02 21:26:26.371   Update lease failure: " + line + "\r\n");

        var failure = ApplicationUpdateHelper.CreateInstallerFailure(directory.Path, 7, initializing);

        Assert.Contains("exit=7", failure.Message);
        Assert.Contains("stage=" + stage, failure.Message);
        Assert.Contains(Code(line), failure.Message);
        Assert.DoesNotContain(line[(line.IndexOf(';') + 2)..], failure.Message);
        Assert.Null(failure.InnerException);
        if (!initializing) Assert.Contains("应用未自动重启", failure.Message);
    }

    [Theory]
    [InlineData("arguments", "argumentsinvalid", "LeaseException")]
    [InlineData("verify", "installertrust", "LeaseException")]
    [InlineData("verify", "knownfolderinvalid", "LeaseException")]
    [InlineData("acquire", "instanceheld", "LeaseException")]
    [InlineData("parent-wait", "parenttimeout", "LeaseException")]
    [InlineData("cleanup", "lockreleasefail", "IOException")]
    [InlineData("marker-cleanup", "markercleanupfailed", "AggregateException")]
    [InlineData("diagnostic", "diagnosticwritefail", "UnauthorizedAccessException")]
    [InlineData("owner-monitor", "operationfailed", "Win32Exception")]
    public void RecognizesCurrentProducerProtocol(string phase, string reason, string type)
    {
        using var directory = new TemporaryDirectory();
        var line = LeaseLine(phase, reason, type);
        WriteLog(directory.Path, "Update lease failure: " + line);

        Assert.Contains(Code(line), ApplicationUpdateHelper.CreateInstallerFailure(directory.Path, 7, true).Message);
    }

    [Fact]
    public void MultilineInnoRecordDeduplicatesAndKeepsDistinctDiagnosticsInOrder()
    {
        using var directory = new TemporaryDirectory();
        var first = LeaseLine();
        var second = LeaseLine("cleanup", "lockreleasefail", "IOException");
        WriteLog(directory.Path, "2026-10-02 21:26:26.371   Update lease failure: " + first + "\r\n" + second + "\r\n" +
            "2026-10-02 21:26:26.400   Update lease failure: " + first + "\r\n" + second + "\r\n");

        var message = ApplicationUpdateHelper.CreateInstallerFailure(directory.Path, 7, false).Message;

        Assert.Equal(2, message.Split("installer-update-lease", StringSplitOptions.None).Length - 1);
        Assert.True(message.IndexOf(Code(first), StringComparison.Ordinal) < message.IndexOf(Code(second), StringComparison.Ordinal));
    }

    [Fact]
    public void ArbitraryLogTextCredentialsAndPathsNeverReachTheFailure()
    {
        using var directory = new TemporaryDirectory();
        var secretDirectory = Path.Combine(directory.Path, "TOKEN=directory-secret");
        Directory.CreateDirectory(secretDirectory);
        var line = LeaseLine();
        WriteLog(secretDirectory, "TOKEN=log-secret COOKIE=cookie-secret\r\n" +
            @"C:\Users\private-user\sensitive-path\job.json" + "\r\n" +
            "2026-10-02 21:26:26.371   Update lease failure: " + line + "\r\n" +
            line + "; TOKEN=injected-secret\r\n" +
            line.Replace("type=LeaseException", "type=TOKENSecretException", StringComparison.Ordinal) + "\r\n");

        var failure = ApplicationUpdateHelper.CreateInstallerFailure(secretDirectory, 7, true);

        Assert.Contains(Code(line), failure.Message);
        Assert.DoesNotContain("TOKEN", failure.ToString());
        Assert.DoesNotContain("COOKIE", failure.ToString());
        Assert.DoesNotContain("secret", failure.ToString());
        Assert.DoesNotContain("private-user", failure.ToString());
        Assert.DoesNotContain("sensitive-path", failure.ToString());
        Assert.DoesNotContain(directory.Path, failure.ToString());
    }

    [Theory]
    [InlineData("phase=verify", "phase=verify phase=cleanup")]
    [InlineData("phase=verify", "phase=unknown")]
    [InlineData("phase=verify", "phase=VERIFY")]
    [InlineData("reason=tokenidentity", "reason=unknown")]
    [InlineData("reason=tokenidentity", "reason=TOKENsecret")]
    [InlineData("type=LeaseException", "type=UntrustedException")]
    [InlineData("type=LeaseException", "type=TOKENSecretException")]
    [InlineData("type=LeaseException", "type=C:\\private-path\\IOException")]
    [InlineData("HRESULT=0x80070005", "HRESULT=0x8007000a")]
    [InlineData("HRESULT=0x80070005", "HRESULT=0x800700050")]
    [InlineData("HRESULT=0x80070005", "HRESULT=0x8007000")]
    [InlineData("HRESULT=0x80070005", "HRESULT=80070005")]
    [InlineData("HRESULT=0x80070005", "HRESULT=0x80070005 reason=tokenidentity")]
    [InlineData("phase=verify reason=tokenidentity", "reason=tokenidentity phase=verify")]
    [InlineData("phase=verify", "phase=verify\u202E")]
    [InlineData("; ", "; TOKEN=secret ")]
    public void RejectsUnknownMalformedRepeatedOrInjectedFields(string original, string replacement)
    {
        using var directory = new TemporaryDirectory();
        WriteLog(directory.Path, "Update lease failure: " + LeaseLine().Replace(original, replacement, StringComparison.Ordinal));

        var failure = ApplicationUpdateHelper.CreateInstallerFailure(directory.Path, 7, true);

        Assert.Contains("未找到已验证的结构化诊断", failure.Message);
        Assert.Contains("installer.log", failure.Message);
        Assert.DoesNotContain("installer-update-lease", failure.Message);
        Assert.DoesNotContain("TOKEN", failure.ToString());
        Assert.DoesNotContain("private-path", failure.ToString());
    }

    [Theory]
    [InlineData("TOKEN=secret ")]
    [InlineData("2026-10-02 21:26:26.371   Command line: ")]
    [InlineData("2026-10-02 21:26:26.371   Update lease failure: TOKEN=secret ")]
    [InlineData("C:\\private-path\\ ")]
    public void DoesNotSearchForDiagnosticSubstringsInsideArbitraryLines(string prefix)
    {
        using var directory = new TemporaryDirectory();
        WriteLog(directory.Path, prefix + LeaseLine());

        var failure = ApplicationUpdateHelper.CreateInstallerFailure(directory.Path, 7, true);

        Assert.DoesNotContain("installer-update-lease", failure.Message);
        Assert.DoesNotContain("TOKEN", failure.ToString());
        Assert.DoesNotContain("private-path", failure.ToString());
    }

    [Theory]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    [InlineData(4096, false)]
    public void EnforcesLineLengthLimitBeforeMatchingOtherwiseValidRecords(int length, bool accepted)
    {
        using var directory = new TemporaryDirectory();
        const string timestamp = "2026-10-02 21:26:26.371";
        var diagnostic = LeaseLine();
        var suffix = "Update lease failure: " + diagnostic;
        var line = timestamp + new string(' ', length - timestamp.Length - suffix.Length) + suffix;
        Assert.Equal(length, line.Length);
        WriteLog(directory.Path, line);

        var failure = ApplicationUpdateHelper.CreateInstallerFailure(directory.Path, 7, true);

        Assert.Contains("exit=7", failure.Message);
        Assert.Contains("stage=初始化", failure.Message);
        if (accepted) Assert.Contains(Code(diagnostic), failure.Message);
        else
        {
            Assert.DoesNotContain("installer-update-lease", failure.Message);
            Assert.Contains("未找到已验证的结构化诊断", failure.Message);
        }
    }

    [Fact]
    public void LargeLogOnlyUsesTheBoundedTail()
    {
        using var directory = new TemporaryDirectory();
        var oldLine = LeaseLine("probe", "probefailed");
        var latest = LeaseLine();
        using (var stream = new FileStream(Path.Combine(directory.Path, "installer.log"), FileMode.Create, FileAccess.Write))
        {
            stream.Write(Encoding.UTF8.GetBytes(oldLine + "\n"));
            stream.SetLength(32L * 1024 * 1024);
            var finalBytes = Encoding.UTF8.GetBytes("\nUpdate lease failure: " + latest + "\n");
            stream.Position = stream.Length - finalBytes.Length;
            stream.Write(finalBytes);
        }

        var message = ApplicationUpdateHelper.CreateInstallerFailure(directory.Path, 7, true).Message;

        Assert.Equal(64 * 1024, ApplicationUpdateHelper.InstallerDiagnosticTailBytes);
        Assert.Contains(Code(latest), message);
        Assert.DoesNotContain(Code(oldLine), message);
        Assert.True(message.Length < 1024);
    }

    [Fact]
    public void TailBoundaryDiscardsPartialLineEvenIfItEndsWithAValidRecord()
    {
        using var directory = new TemporaryDirectory();
        var fragment = LeaseLine("probe", "probefailed");
        var complete = LeaseLine();
        WriteLog(directory.Path, "TOKEN=secret" + new string('X', ApplicationUpdateHelper.InstallerDiagnosticTailBytes * 2) +
            fragment + "\n" + complete);

        var message = ApplicationUpdateHelper.CreateInstallerFailure(directory.Path, 7, true).Message;

        Assert.Contains(Code(complete), message);
        Assert.DoesNotContain(Code(fragment), message);
        Assert.DoesNotContain("TOKEN", message);
    }

    [Fact]
    public void CapsOutputAtLatestEightDistinctVerifiedRecordsAndReportsTheLimit()
    {
        using var directory = new TemporaryDirectory();
        var lines = Enumerable.Range(0, 12).Select(index => LeaseLine(hresult: AccessDenied + index)).ToArray();
        WriteLog(directory.Path, string.Join('\n', lines.Concat(lines)));

        var message = ApplicationUpdateHelper.CreateInstallerFailure(directory.Path, 7, false).Message;

        Assert.Equal(8, ApplicationUpdateHelper.InstallerDiagnosticLimit);
        Assert.Equal(8, message.Split("installer-update-lease", StringSplitOptions.None).Length - 1);
        foreach (var line in lines.Take(4)) Assert.DoesNotContain(Code(line), message);
        foreach (var line in lines.Skip(4)) Assert.Contains(Code(line), message);
        Assert.Contains("诊断超过上限", message);
        Assert.Contains("installer.log", message);
    }

    [Theory]
    [InlineData(7, true, "初始化")]
    [InlineData(23, false, "安装")]
    [InlineData(-1, false, "安装")]
    [InlineData(0, true, "初始化")]
    public void EmptyLogKeepsExitAndStageWithoutInferringSuccess(int exitCode, bool initializing, string stage)
    {
        using var directory = new TemporaryDirectory();
        WriteLog(directory.Path, "");

        var failure = ApplicationUpdateHelper.CreateInstallerFailure(directory.Path, exitCode, initializing);

        Assert.Contains("exit=" + exitCode, failure.Message);
        Assert.Contains("stage=" + stage, failure.Message);
        Assert.Contains("未找到已验证的结构化诊断", failure.Message);
        Assert.Contains("更新任务目录中的 installer.log", failure.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LogNotYetCreatedIsAnExplicitDiagnosticAndKeepsExit(bool initializing)
    {
        using var directory = new TemporaryDirectory();

        var failure = ApplicationUpdateHelper.CreateInstallerFailure(directory.Path, 7, initializing);

        Assert.Contains("exit=7", failure.Message);
        Assert.Contains("stage=" + (initializing ? "初始化" : "安装"), failure.Message);
        Assert.Contains("installer.log 尚不存在", failure.Message);
        Assert.Contains("type=FileNotFoundException HRESULT=0x80070002", failure.Message);
        Assert.DoesNotContain(directory.Path, failure.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SharingViolationReportsExactReadFailureWithoutTheRawException(bool initializing)
    {
        using var directory = new TemporaryDirectory();
        var logPath = Path.Combine(directory.Path, "installer.log");
        WriteLog(directory.Path, "TOKEN=log-secret");
        using var locked = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var readError = Assert.Throws<IOException>(() => { using var unreadable = File.OpenRead(logPath); });

        var failure = ApplicationUpdateHelper.CreateInstallerFailure(directory.Path, 7, initializing);

        Assert.Contains("exit=7", failure.Message);
        Assert.Contains("stage=" + (initializing ? "初始化" : "安装"), failure.Message);
        Assert.Contains("installer.log 读取失败", failure.Message);
        Assert.Contains($"type=IOException HRESULT=0x{readError.HResult:X8}", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(directory.Path, failure.ToString());
        Assert.DoesNotContain("TOKEN", failure.ToString());
        Assert.DoesNotContain("log-secret", failure.ToString());
    }

    [Fact]
    public void UnreadableDirectoryNamedInstallerLogReportsAccessFailureWithoutAPath()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "installer.log"));

        var failure = ApplicationUpdateHelper.CreateInstallerFailure(directory.Path, 7, false);

        Assert.Contains("exit=7", failure.Message);
        Assert.Contains("stage=安装", failure.Message);
        Assert.Contains("installer.log 读取失败", failure.Message);
        Assert.Contains("type=UnauthorizedAccessException HRESULT=0x80070005", failure.Message);
        Assert.DoesNotContain(directory.Path, failure.ToString());
    }
}
