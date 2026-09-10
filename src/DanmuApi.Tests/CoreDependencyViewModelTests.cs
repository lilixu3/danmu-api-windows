using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.Core;

namespace DanmuApi.Tests;

public sealed class CoreDependencyViewModelTests
{
    [Fact]
    public async Task CheckShowsDependencyNameRangeAndSafeDiagnostic()
    {
        var service = new StubCoreDependencyService();
        var vm = new CoreDependencyViewModel(service, new RecordingDialogService(), new Diagnostics());
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Contains("异常 1", vm.CountsText);
        Assert.Equal("example · 要求 ^1.0.0 · 缺少必需依赖包", Assert.Single(vm.IssueDetails));
        Assert.Equal(ManagedCoreVariant.Stable, service.LastVariant);
        Assert.DoesNotContain("secret", CoreDependencyViewModel.FormatIssue(new("example", "https://secret", "private", null, null, null, "TOKEN=secret")));
    }

    [Fact]
    public async Task RepairRequiresConfirmationAndCustomIsDisabled()
    {
        var service = new StubCoreDependencyService();
        var dialogs = new RecordingDialogService();
        var vm = new CoreDependencyViewModel(service, dialogs, new Diagnostics());
        await vm.RepairCommand.ExecuteAsync(null);
        Assert.Equal(0, service.Repairs);
        dialogs.Confirmation = true;
        await vm.RepairCommand.ExecuteAsync(null);
        Assert.Equal(1, service.Repairs);
        Assert.Contains("修复完成", vm.StatusText);
        vm.Variant = ManagedCoreVariant.Custom;
        Assert.False(vm.RepairCommand.CanExecute(null));
        await vm.RepairCommand.ExecuteAsync(null);
        Assert.Equal(1, service.Repairs);
        Assert.Empty(vm.IssueDetails);
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal(ManagedCoreVariant.Custom, service.LastVariant);
    }

    [Fact]
    public async Task CheckCancellationRemainsVisibleAndFailureDoesNotLeakPayload()
    {
        var service = new StubCoreDependencyService { Wait = true };
        var diagnostics = new Diagnostics();
        var vm = new CoreDependencyViewModel(service, new RecordingDialogService(), diagnostics);
        var task = vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        Assert.False(vm.RepairCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);
        await task;
        Assert.Contains("取消", vm.StatusText);
        service.Wait = false;
        service.Failure = new IOException("COOKIE=secret");
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Contains("失败", vm.StatusText);
        Assert.DoesNotContain("secret", vm.StatusText + diagnostics.LastDiagnostic);
        Assert.Same(service.Failure, vm.LastFailure);
    }

    [Theory]
    [InlineData("签名校验失败：TOKEN=secret", "签名验证失败")]
    [InlineData("签名依赖包未覆盖核心必需依赖：secret", "未包含所需依赖")]
    [InlineData("签名依赖包不能满足必需版本或入口：secret", "不满足核心要求")]
    [InlineData("核心依赖已修复并生效，但清理失败：secret", "已修复并生效，但清理")]
    public async Task KnownFailuresKeepSpecificSafeReasonAndOriginalChain(string message, string expected)
    {
        var failure = new IOException(message, new InvalidOperationException("private-secret-detail"));
        var service = new StubCoreDependencyService { Failure = failure };
        var vm = new CoreDependencyViewModel(service, new RecordingDialogService(), new Diagnostics());
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Contains(expected, vm.StatusText);
        Assert.DoesNotContain("secret", vm.StatusText);
        Assert.Same(failure, vm.LastFailure);
        Assert.Same(failure.InnerException, vm.LastFailure!.InnerException);
        if (message.StartsWith("核心依赖已修复并生效，但", StringComparison.Ordinal))
            Assert.StartsWith("核心依赖已修复并生效，但", vm.StatusText);
    }

    internal sealed class StubCoreDependencyService : ICoreDependencyService
    {
        public int Repairs;
        public bool Wait;
        public Exception? Failure;
        public ManagedCoreVariant LastVariant;
        public async Task<CoreDependencyCheckResult> CheckAsync(ManagedCoreVariant variant, CancellationToken cancellationToken = default)
        {
            LastVariant = variant;
            if (Failure is not null) throw Failure;
            if (Wait) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new(2, [new("example", "^1.0.0", "private-directory", null, null, null, "Missing required package")]);
        }
        public Task<CoreDependencyCheckResult> RepairAsync(ManagedCoreVariant variant, IProgress<CoreDependencyRepairProgress>? progress = null, CancellationToken cancellationToken = default)
        { Repairs++; LastVariant = variant; return Task.FromResult(new CoreDependencyCheckResult(2, [])); }
    }
    private sealed class Diagnostics : IAppDiagnostics
    {
        public string? LastDiagnostic { get; private set; }
        public void Record(string message, Exception? error = null) => LastDiagnostic = message;
    }
}
