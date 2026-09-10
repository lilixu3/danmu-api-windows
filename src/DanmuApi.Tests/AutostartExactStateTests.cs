using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class AutostartExactStateTests
{
    [Fact]
    public void ExplicitDisableRemovesLegacyBeforeRefreshCanMigrateIt()
    {
        using var directory = new TemporaryDirectory();
        var executable = Path.Combine(directory.Path, "DanmuApi.exe");
        File.WriteAllText(executable, "stub");
        var executor = new LegacyExecutor();
        var manager = new AutostartManager(executor, () => executable, () => @"C:\Windows");
        Assert.True(manager.Disable().Succeeded);
        Assert.True(manager.RefreshIfEnabled().Succeeded);
        Assert.False(executor.Written);
        Assert.False(executor.LegacyExists);
    }

    [Theory]
    [InlineData(false, true, true, AutostartState.NotRegistered)]
    [InlineData(true, true, true, AutostartState.Registered)]
    [InlineData(true, true, false, AutostartState.InvalidPath)]
    [InlineData(true, false, true, AutostartState.SystemDisabled)]
    [InlineData(true, null, true, AutostartState.Unknown)]
    public void ExactQueryDistinguishesStates(bool exists, bool? approved, bool valid, AutostartState expected)
    {
        using var fixture = new ExactFixture();
        fixture.Reader.Entries["DanmuApi"] = new(exists, valid ? fixture.Command : "secret invalid command", approved);
        var result = fixture.Manager.IsEnabled();
        Assert.Equal(expected, result.State);
        Assert.DoesNotContain("secret", result.Diagnostic);
        Assert.Equal(exists, result.IsRegistered);
        Assert.Empty(fixture.Executor.Calls);
        var status = new DanmuApi.App.Services.PlatformAutostartService(fixture.Manager).GetStatus();
        Assert.Equal(expected, status.EffectiveState);
        if (expected == AutostartState.Unknown) Assert.Null(status.EffectiveEnabled);
        if (expected == AutostartState.SystemDisabled)
        {
            Assert.True(status.IsRegistered);
            Assert.False(status.EffectiveEnabled);
            Assert.False(status.CanToggle);
        }
    }

    [Fact]
    public async Task QueryFailureRemainsUnknownAndBlocksToggle()
    {
        using var fixture = new ExactFixture();
        fixture.Reader.Error = new UnauthorizedAccessException("TOKEN=secret raw command");
        var service = new DanmuApi.App.Services.PlatformAutostartService(fixture.Manager);
        var status = service.GetStatus();
        Assert.Equal(AutostartState.Unknown, status.EffectiveState);
        Assert.Null(status.EffectiveEnabled);
        Assert.False(status.CanToggle);
        Assert.DoesNotContain("secret", status.Diagnostic);
        Assert.Contains("UnauthorizedAccessException", status.Diagnostic);
        Assert.False((await service.SetEnabledAsync(true)).Succeeded);
        Assert.Empty(fixture.Executor.Calls);
    }

    [Fact]
    public void UnsupportedDoesNotReadRegistry()
    {
        var reader = new FakeReader { Error = new InvalidOperationException() };
        var result = new AutostartManager(new FakeExecutor(), () => @"C:\Tools\dotnet.exe", registryReader: reader).IsEnabled();
        Assert.Equal(AutostartState.Unsupported, result.State);
        Assert.False(result.Supported);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SystemDisabledCurrentOrLegacyIsNeverRefreshed(bool legacy)
    {
        using var fixture = new ExactFixture();
        fixture.Reader.Entries[legacy ? "DanmuApiDesktop" : "DanmuApi"] = new(true, fixture.Command, false);
        Assert.Equal(AutostartState.SystemDisabled, fixture.Manager.RefreshIfEnabled().State);
        Assert.Empty(fixture.Executor.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactMigrationVerifiesWriteBeforeDeletingLegacy(bool failWrite)
    {
        using var fixture = new ExactFixture();
        fixture.Reader.Entries["DanmuApiDesktop"] = new(true, fixture.Command);
        fixture.Executor.FailWrite = failWrite;
        var result = fixture.Manager.RefreshIfEnabled();
        Assert.Equal(!failWrite, result.Succeeded);
        Assert.Equal(failWrite, fixture.Reader.Read("DanmuApiDesktop").Exists);
        if (failWrite) Assert.DoesNotContain(fixture.Executor.Calls, c => c == "delete");
        else Assert.Equal(new[] { "write", "delete" }, fixture.Executor.Calls);
    }

    [Fact]
    public void RefreshWriteFailurePreservesBothExistingEntries()
    {
        using var fixture = new ExactFixture();
        fixture.Reader.Entries["DanmuApi"] = new(true, "obsolete path");
        fixture.Reader.Entries["DanmuApiDesktop"] = new(true, fixture.Command);
        fixture.Executor.FailWrite = true;
        Assert.False(fixture.Manager.RefreshIfEnabled().Succeeded);
        Assert.True(fixture.Reader.Read("DanmuApiDesktop").Exists);
        Assert.Equal("obsolete path", fixture.Reader.Read("DanmuApi").Command);
        Assert.Equal(new[] { "write" }, fixture.Executor.Calls);
    }

    [Fact]
    public void OldStatusConstructorsRetainTheirMeaning()
    {
        var enabled = new DanmuApi.App.Services.AutostartStatus(true, true, "enabled");
        var disabled = new DanmuApi.App.Services.AutostartStatus(true, false, "disabled");
        var unsupported = new DanmuApi.App.Services.AutostartStatus(false, false, "unsupported");
        Assert.Equal(AutostartState.Registered, enabled.EffectiveState);
        Assert.Equal(AutostartState.NotRegistered, disabled.EffectiveState);
        Assert.Equal(AutostartState.Unsupported, unsupported.EffectiveState);
        Assert.Null(unsupported.EffectiveEnabled);
        Assert.False(unsupported.CanToggle);
    }

    [Fact]
    public void SuccessfulExitWithoutWriteDoesNotDeleteLegacy()
    {
        using var fixture = new ExactFixture();
        fixture.Reader.Entries["DanmuApiDesktop"] = new(true, fixture.Command);
        fixture.Executor.SkipWrite = true;
        Assert.False(fixture.Manager.RefreshIfEnabled().Succeeded);
        Assert.True(fixture.Reader.Read("DanmuApiDesktop").Exists);
        Assert.Equal(new[] { "write" }, fixture.Executor.Calls);
    }

    [Fact]
    public void ExactDisableRemovesBothNamesAndDoesNotResurrect()
    {
        using var fixture = new ExactFixture();
        fixture.Reader.Entries["DanmuApiDesktop"] = new(true, fixture.Command);
        fixture.Reader.Entries["DanmuApi"] = new(true, fixture.Command);
        Assert.True(fixture.Manager.Disable().Succeeded);
        Assert.Equal(AutostartState.NotRegistered, fixture.Manager.RefreshIfEnabled().State);
        Assert.Equal(new[] { "delete", "delete" }, fixture.Executor.Calls);
    }

    [Fact]
    public void DeleteExitOneIsFailureWhenValueWasPresent()
    {
        using var fixture = new ExactFixture();
        fixture.Reader.Entries["DanmuApi"] = new(true, fixture.Command);
        fixture.Executor.FailDelete = true;
        var result = fixture.Manager.Disable();
        Assert.False(result.Succeeded);
        Assert.Equal(AutostartState.Unknown, result.State);
        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("secret", result.Diagnostic);
        Assert.Empty(result.Output);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(0, null)]
    [InlineData(7, null)]
    public void StartupApprovalOnlyDecodesKnownFormats(byte state, bool? expected)
    {
        var data = new byte[12];
        data[0] = state;
        Assert.Equal(expected, WindowsAutostartRegistryReader.DecodeStartupApproved(data));
        Assert.Null(WindowsAutostartRegistryReader.DecodeStartupApproved(new byte[] { state }));
        Assert.Null(WindowsAutostartRegistryReader.DecodeStartupApproved("secret"));
    }

    private sealed class ExactFixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        public FakeReader Reader { get; } = new();
        public FakeExecutor Executor { get; }
        public AutostartManager Manager { get; }
        public string Command { get; }
        public ExactFixture()
        {
            var executable = Path.Combine(_directory.Path, "中文 space", "DanmuApi.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, "stub");
            Command = $"\"{executable}\" --autostart";
            Executor = new() { Reader = Reader, Command = Command };
            Manager = new(Executor, () => executable, () => @"C:\Windows", Reader);
        }
        public void Dispose() => _directory.Dispose();
    }

    private sealed class FakeReader : IAutostartRegistryReader
    {
        public Dictionary<string, AutostartRegistryEntry> Entries { get; } = new();
        public Exception? Error;
        public AutostartRegistryEntry Read(string name) => Error is not null ? throw Error
            : Entries.GetValueOrDefault(name) ?? new(false);
    }

    private sealed class FakeExecutor : IPlatformCommandExecutor
    {
        public FakeReader Reader = new();
        public string Command = "";
        public bool FailWrite, SkipWrite, FailDelete;
        public List<string> Calls { get; } = new();
        public CommandExecutionResult Execute(string path, IReadOnlyList<string> args)
        {
            if (path.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase))
            {
                Calls.Add("write");
                if (FailWrite) return new(true, 1, "", "secret raw command");
                if (!SkipWrite) Reader.Entries["DanmuApi"] = new(true, Command);
            }
            else
            {
                Assert.Contains("delete", args);
                Calls.Add("delete");
                if (FailDelete) return new(true, 1, "", "secret raw command");
                Reader.Entries.Remove(args[3]);
            }
            return new(true, 0, "", "");
        }
    }

    private sealed class LegacyExecutor : IPlatformCommandExecutor
    {
        public bool LegacyExists = true;
        public bool Written;
        public CommandExecutionResult Execute(string path, IReadOnlyList<string> args)
        {
            if (path.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase))
            {
                Written = true;
                return new(true, 0, "", "");
            }
            var exists = args.Contains("DanmuApiDesktop") && LegacyExists;
            if (args.Contains("delete") && args.Contains("DanmuApiDesktop")) LegacyExists = false;
            return new(true, exists ? 0 : 1, "", "");
        }
    }
}
