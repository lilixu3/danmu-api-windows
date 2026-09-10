using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class AutostartStateViewModelTests
{
    [Theory]
    [InlineData(AutostartState.Unknown, false, false, "状态未知")]
    [InlineData(AutostartState.SystemDisabled, true, false, "系统已禁用")]
    [InlineData(AutostartState.Registered, true, true, "已登记")]
    [InlineData(AutostartState.NotRegistered, true, true, "未登记")]
    public void UnknownAndDisabledAreNotPresentedAsOrdinaryOff(AutostartState state, bool showSwitch, bool canToggle, string label)
    {
        using var directory = new TemporaryDirectory();
        var service = new Stub(state);
        var model = new SettingsPageViewModel(new SettingsStore(Path.Combine(directory.Path,"settings")),service,
            new Notifications(),new AppPaths(directory.Path));
        Assert.Equal(showSwitch,model.IsAutostartStateKnown);
        Assert.Equal(canToggle,model.CanChangeAutostart);
        Assert.Equal(label,model.AutostartStateLabel);
        service.State = AutostartState.Unknown;
        model.RefreshAutostartStatusCommand.Execute(null);
        Assert.False(model.IsAutostartStateKnown);
        Assert.False(model.ToggleAutostartCommand.CanExecute(null));
    }
    private sealed class Stub(AutostartState state) : IAutostartService
    {
        public AutostartState State { get; set; } = state;
        public AutostartStatus GetStatus() => new(true,State==AutostartState.Registered,"diagnostic",State,
            State is AutostartState.Registered or AutostartState.SystemDisabled ? true : State==AutostartState.NotRegistered ? false : null);
        public AutostartOperationResult RefreshIfEnabled() => new(true,GetStatus(),"test");
        public Task<AutostartOperationResult> SetEnabledAsync(bool enabled,CancellationToken cancellationToken=default) => throw new NotSupportedException();
    }
    private sealed class Notifications : IDesktopNotificationService
    {
        public Task<DesktopNotificationResult> ShowAsync(string title,string message,CancellationToken cancellationToken=default) => throw new NotSupportedException();
    }
}
