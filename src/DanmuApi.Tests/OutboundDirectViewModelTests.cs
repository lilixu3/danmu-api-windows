using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.Platform;
using Xunit.Abstractions;

namespace DanmuApi.Tests;

public sealed class OutboundDirectViewModelTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, "已关闭")]
    [InlineData(true, "已开启待检查")]
    public void InitialStatusUsesSavedEnabledWithoutInventingRuntimeDetails(bool enabled, string status)
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Enabled = enabled });
        service.Publish(new("off", "尚未刷新状态。"));
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        Assert.Equal(enabled, model.Enabled);
        Assert.Equal(status, model.StatusText);
        Assert.True(model.IsInitialStatusPending);
        Assert.Equal("正在读取当前服务状态。", model.StatusDescription);
        Assert.False(model.IsEngineReady);
        Assert.False(model.IsEngineFailed);
        Assert.False(model.HasRuntimeDetails);
        Assert.Empty(model.CapabilityText);
        Assert.Empty(model.ProcessText);
        Assert.Empty(model.StatusReason);
        Assert.True(model.CanToggleEnabled);
        Assert.True(model.CanOpenConfiguration);
        Assert.True(model.CanOpenDiagnostics);
        Assert.False(model.HasRecentTest);
        Assert.Equal("—", model.RecentNormalCount);
        Assert.Equal("—", model.RecentNeedsKeyCount);
        Assert.Equal("—", model.RecentFailedCount);
        Assert.Equal(0, service.RefreshCalls);
        Assert.Empty(service.Saves);
    }

    [Fact]
    public async Task FirstStatusRequestStaysPendingUntilItReturnsActualState()
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Enabled = true });
        var response = new TaskCompletionSource<OutboundDirectSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.RefreshAction = token => response.Task.WaitAsync(token);
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        var pending = model.ActivateAsync();
        Assert.True(model.IsRefreshing);
        Assert.True(model.IsInitialStatusPending);
        Assert.Equal("已开启待检查", model.StatusText);
        Assert.Equal("正在读取当前服务状态。", model.StatusDescription);
        Assert.False(model.CanDiagnose);
        response.SetResult(new("off", "服务未运行", service.Settings));
        await pending;
        Assert.False(model.IsInitialStatusPending);
        Assert.Equal("已开启待服务", model.StatusText);
        Assert.Contains("概览页启动", model.StatusDescription);
        Assert.False(model.IsEngineReady);
        Assert.False(model.IsRefreshing);
    }

    [Fact]
    public async Task ToggleUsesSavedEnabledAndLeavesConnectionDraftUntouched()
    {
        var service = new OutboundUiTestService();
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        model.DohUrl = "https://dns.example.test/dns-query";
        model.ConnectTimeoutText = "4500";
        model.SelectedHttpOption = model.HttpOptions.Single(option => option.Value == "h3");
        model.Tmdb = false;
        // Even a caller writing the presentation property cannot change which saved value the command toggles.
        model.Enabled = true;
        await model.ToggleEnabledCommand.ExecuteAsync(null);
        var enabled = Assert.Single(service.Saves);
        Assert.True(enabled.Enabled);
        Assert.Equal(string.Empty, enabled.DohUrl);
        Assert.Equal("auto", enabled.HttpVersion);
        Assert.Equal(3000, enabled.ConnectTimeoutMs);
        Assert.Contains("tmdb", enabled.Sources);
        Assert.False(model.Tmdb);
        Assert.Equal("https://dns.example.test/dns-query", model.DohUrl);
        Assert.Equal("4500", model.ConnectTimeoutText);
        Assert.Equal("h3", model.SelectedHttpOption!.Value);
        Assert.True(model.HasUnsavedChanges);
        Assert.True(model.LastSaveSucceeded);
        Assert.False(model.IsEngineReady);

        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("4500", model.ConnectTimeoutText);
        Assert.True(model.HasUnsavedChanges);
        await model.ApplyConfigurationCommand.ExecuteAsync(null);
        Assert.Equal(2, service.Saves.Count);
        Assert.Equal(model.DohUrl, service.Settings.DohUrl);
        Assert.Equal(4500, service.Settings.ConnectTimeoutMs);
        Assert.Equal("h3", service.Settings.HttpVersion);
        Assert.DoesNotContain("tmdb", service.Settings.Sources);
        Assert.False(model.HasUnsavedChanges);
        Assert.True(model.LastSaveSucceeded);
        Assert.False(model.Snapshot.Applied);
        Assert.False(model.IsEngineReady);
    }

    [Theory]
    [InlineData("bahamut", false, false)]
    [InlineData("bahamut", true, false)]
    [InlineData("bahamut", false, true)]
    [InlineData("bahamut", true, true)]
    [InlineData("tmdb", false, false)]
    [InlineData("tmdb", true, false)]
    [InlineData("tmdb", false, true)]
    [InlineData("tmdb", true, true)]
    [InlineData("dandan", false, false)]
    [InlineData("dandan", true, false)]
    [InlineData("dandan", false, true)]
    [InlineData("dandan", true, true)]
    [InlineData("animeko", false, false)]
    [InlineData("animeko", true, false)]
    [InlineData("animeko", false, true)]
    [InlineData("animeko", true, true)]
    public async Task QuickSourceToggleChangesOnlySourcesAndSynchronizesACleanForm(string source, bool selected, bool enabled)
    {
        var saved = OutboundSettings.Default with
        {
            Enabled = enabled,
            Sources = selected ? OutboundSettings.Default.Sources : OutboundSettings.Default.Sources.Where(value => value != source).ToArray(),
            HttpVersion = "h3", DohUrl = "https://saved.example.test/private?api_key=secret-value", ConnectTimeoutMs = 4200,
        };
        var service = new OutboundUiTestService(saved);
        service.Publish(OutboundUiTestService.Ready(saved));
        service.SaveAction = (settings, _) =>
        {
            service.Settings = settings;
            service.Publish(OutboundUiTestService.Ready(settings));
            return Task.FromResult(new OutboundOperationResult(true, "已保存并应用。"));
        };
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        AssertSavedSources(model, saved);
        Assert.Equal($"已选 {saved.Sources.Count} / 4", model.SourceSelectionText);
        Assert.True(model.CanToggleSource);
        model.Enabled = !enabled;
        await model.ToggleSourceCommand.ExecuteAsync(source);
        var candidate = Assert.Single(service.Saves);
        var expectedSources = selected ? saved.Sources.Where(value => value != source).ToArray() : saved.Sources.Append(source).ToArray();
        Assert.True(OutboundSettings.Equivalent(saved with { Sources = expectedSources }, candidate));
        AssertSavedSources(model, candidate);
        Assert.Equal($"已选 {candidate.Sources.Count} / 4", model.SourceSelectionText);
        Assert.Equal(enabled, model.Enabled);
        Assert.Equal(candidate.Sources.Contains("bahamut"), model.Bahamut);
        Assert.Equal(candidate.Sources.Contains("tmdb"), model.Tmdb);
        Assert.Equal(candidate.Sources.Contains("dandan"), model.Dandan);
        Assert.Equal(candidate.Sources.Contains("animeko"), model.Animeko);
        Assert.Equal("h3", model.SelectedHttpOption!.Value);
        Assert.Equal(saved.DohUrl, model.DohUrl);
        Assert.Equal("4200", model.ConnectTimeoutText);
        Assert.False(model.HasUnsavedChanges);
        Assert.True(model.LastSaveSucceeded);
        Assert.Empty(model.Diagnostic);
        Assert.Equal(enabled, model.CanDiagnose);
    }

    [Theory]
    [InlineData("bahamut")]
    [InlineData("tmdb")]
    [InlineData("dandan")]
    [InlineData("animeko")]
    public async Task QuickSourceToggleDoesNotSaveOrOverwriteAnExistingDraftOutsideTheDialog(string source)
    {
        var saved = OutboundSettings.Default with
        {
            Enabled = true, Sources = ["bahamut", "dandan"], HttpVersion = "h2",
            DohUrl = "https://saved.example.test/query", ConnectTimeoutMs = 4200,
        };
        var service = new OutboundUiTestService(saved);
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        model.Bahamut = false;
        model.Tmdb = true;
        model.Dandan = false;
        model.Animeko = true;
        model.SelectedHttpOption = model.HttpOptions.Single(option => option.Value == "h3");
        model.DohUrl = "https://draft.example.test/query";
        model.ConnectTimeoutText = "5000";
        Assert.True(model.HasUnsavedChanges);
        Assert.False(model.IsConfigurationEditing);
        Assert.True(model.CanToggleSource);
        await model.ToggleSourceCommand.ExecuteAsync(source);
        var candidate = Assert.Single(service.Saves);
        var expectedSources = saved.Sources.Contains(source) ? saved.Sources.Where(value => value != source).ToArray() : saved.Sources.Append(source).ToArray();
        Assert.True(OutboundSettings.Equivalent(saved with { Sources = expectedSources }, candidate));
        AssertSavedSources(model, candidate);
        Assert.False(model.Bahamut);
        Assert.True(model.Tmdb);
        Assert.False(model.Dandan);
        Assert.True(model.Animeko);
        Assert.Equal("h3", model.SelectedHttpOption!.Value);
        Assert.Equal("https://draft.example.test/query", model.DohUrl);
        Assert.Equal("5000", model.ConnectTimeoutText);
        Assert.True(model.HasUnsavedChanges);
        Assert.False(model.IsConfigurationEditing);
        Assert.True(model.LastSaveSucceeded);
    }

    [Theory]
    [InlineData("editing")]
    [InlineData("unloaded")]
    [InlineData("disposed")]
    public async Task QuickSourceCommandRechecksTheConfigurationAndLifetimeGate(string blockedBy)
    {
        var service = new OutboundUiTestService();
        if (blockedBy == "unloaded") service.ReadError = new IOException("配置不可读");
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        if (blockedBy == "editing") Assert.True(model.BeginConfigurationEdit());
        if (blockedBy == "disposed") model.Dispose();
        var notifications = new List<string?>();
        model.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        Assert.False(model.CanToggleSource);
        Assert.False(model.ToggleSourceCommand.CanExecute("tmdb"));
        await model.ToggleSourceCommand.ExecuteAsync("tmdb");
        Assert.Empty(service.Saves);
        AssertAllSavedSourcesNotified(notifications);
    }

    [Fact]
    public async Task QuickSourceCommandRejectsAnotherSourceWhileSaving()
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Enabled = true });
        service.Publish(OutboundUiTestService.Ready(service.Settings));
        var response = new TaskCompletionSource<OutboundOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SaveAction = async (settings, token) =>
        {
            var result = await response.Task.WaitAsync(token);
            service.Settings = settings;
            service.Publish(OutboundUiTestService.Ready(settings));
            return result;
        };
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        var pending = model.ToggleSourceCommand.ExecuteAsync("bahamut");
        Assert.True(model.IsBusy);
        Assert.False(model.CanToggleSource);
        Assert.False(model.ToggleSourceCommand.CanExecute("tmdb"));
        var notifications = new List<string?>();
        model.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        await model.ToggleSourceCommand.ExecuteAsync("tmdb");
        Assert.Single(service.Saves);
        Assert.True(model.SavedBahamutSelected && model.SavedTmdbSelected);
        AssertAllSavedSourcesNotified(notifications);
        response.SetResult(new(true, "已保存。"));
        await pending;
        Assert.False(model.SavedBahamutSelected);
        Assert.True(model.SavedTmdbSelected);
        Assert.True(model.CanToggleSource);
        Assert.True(model.CanDiagnose);
        Assert.False(model.HasUnsavedChanges);
        Assert.Single(service.Saves);
    }

    [Fact]
    public async Task QuickSourceCommandRejectsChangesWhileRefreshing()
    {
        var service = new OutboundUiTestService();
        var response = new TaskCompletionSource<OutboundDirectSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.RefreshAction = token => response.Task.WaitAsync(token);
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        var pending = model.RefreshCommand.ExecuteAsync(null);
        Assert.True(model.IsRefreshing);
        Assert.False(model.CanToggleSource);
        Assert.False(model.ToggleSourceCommand.CanExecute("tmdb"));
        var notifications = new List<string?>();
        model.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        await model.ToggleSourceCommand.ExecuteAsync("tmdb");
        Assert.Empty(service.Saves);
        Assert.True(model.SavedTmdbSelected);
        AssertAllSavedSourcesNotified(notifications);
        response.SetResult(service.Snapshot);
        await pending;
        Assert.True(model.CanToggleSource);
        Assert.Empty(service.Saves);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("TMDB")]
    [InlineData(" tmdb")]
    [InlineData("unsupported")]
    [InlineData("token=secret-value")]
    public async Task UnknownQuickSourceParametersFailWithoutSavingOrChangingSelection(string? source)
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Enabled = true });
        service.Publish(OutboundUiTestService.Ready(service.Settings));
        var diagnostics = new OutboundUiTestDiagnostics();
        using var model = new OutboundDirectViewModel(service, diagnostics);
        var notifications = new List<string?>();
        model.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        await model.ToggleSourceCommand.ExecuteAsync(source);
        Assert.Empty(service.Saves);
        AssertSavedSources(model, service.Settings);
        Assert.Equal("已选 4 / 4", model.SourceSelectionText);
        AssertAllSavedSourcesNotified(notifications);
        Assert.False(model.LastSaveSucceeded);
        Assert.True(model.HasDiagnostic);
        Assert.Contains("未知", model.Diagnostic);
        Assert.Equal("应用失败", model.StatusText);
        Assert.False(model.IsEngineReady);
        Assert.DoesNotContain("secret-value", model.Diagnostic);
        Assert.DoesNotContain("secret-value", string.Join(" ", diagnostics.Messages));
    }

    [Theory]
    [InlineData("bahamut")]
    [InlineData("tmdb")]
    [InlineData("dandan")]
    [InlineData("animeko")]
    public async Task EnabledLastSourceCannotBeDeselectedAndAllSavedPropertiesRebound(string source)
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Enabled = true, Sources = [source] });
        service.Publish(OutboundUiTestService.Ready(service.Settings));
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        var notifications = new List<string?>();
        model.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        await model.ToggleSourceCommand.ExecuteAsync(source);
        Assert.Empty(service.Saves);
        AssertSavedSources(model, service.Settings);
        AssertAllSavedSourcesNotified(notifications);
        Assert.Equal("已选 1 / 4", model.SourceSelectionText);
        Assert.Contains("至少", model.Diagnostic);
        Assert.Contains("至少", model.ConfigurationMessage);
        Assert.False(model.LastSaveSucceeded);
        Assert.False(model.HasUnsavedChanges);
        Assert.False(model.IsEngineReady);
    }

    [Theory]
    [InlineData("bahamut")]
    [InlineData("tmdb")]
    [InlineData("dandan")]
    [InlineData("animeko")]
    public async Task DisabledSourcesCanBeClearedButEnablingCannotInventDefaultSources(string source)
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Sources = [source] });
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        await model.ToggleSourceCommand.ExecuteAsync(source);
        var cleared = Assert.Single(service.Saves);
        Assert.False(cleared.Enabled);
        Assert.Empty(cleared.Sources);
        AssertSavedSources(model, cleared);
        Assert.Equal("已选 0 / 4", model.SourceSelectionText);
        Assert.False(model.HasUnsavedChanges);
        Assert.True(model.LastSaveSucceeded);
        await model.ToggleEnabledCommand.ExecuteAsync(null);
        Assert.Single(service.Saves);
        Assert.False(service.Settings.Enabled);
        Assert.Empty(service.Settings.Sources);
        Assert.False(model.Enabled);
        Assert.False(model.LastSaveSucceeded);
        Assert.Contains("启用失败", model.Diagnostic);
        Assert.False(model.IsEngineReady);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedQuickSourceSaveShowsActualDiskSelectionWithoutClaimingRollback(bool written, bool dirtyDraft)
    {
        var saved = OutboundSettings.Default with { Enabled = true, HttpVersion = "h2", ConnectTimeoutMs = 4200 };
        var service = new OutboundUiTestService(saved);
        service.SaveAction = (settings, _) =>
        {
            if (written) service.Settings = settings;
            service.Publish(OutboundUiTestService.Ready(service.Settings));
            return Task.FromResult(new OutboundOperationResult(false, "应用失败 token=secret-value https://dns.example.test/private?api_key=secret-value"));
        };
        var diagnostics = new OutboundUiTestDiagnostics();
        using var model = new OutboundDirectViewModel(service, diagnostics);
        if (dirtyDraft) { model.Animeko = false; model.ConnectTimeoutText = "5000"; }
        await model.ToggleSourceCommand.ExecuteAsync("tmdb");
        var candidate = Assert.Single(service.Saves);
        Assert.True(OutboundSettings.Equivalent(saved with { Sources = ["bahamut", "dandan", "animeko"] }, candidate));
        Assert.Equal(written ? candidate.Sources : saved.Sources, service.Settings.Sources);
        AssertSavedSources(model, service.Settings);
        Assert.Equal(!written, model.SavedTmdbSelected);
        Assert.Equal(dirtyDraft || !written, model.Tmdb);
        Assert.Equal(!dirtyDraft, model.Animeko);
        Assert.Equal(dirtyDraft ? "5000" : "4200", model.ConnectTimeoutText);
        Assert.Equal(dirtyDraft, model.HasUnsavedChanges);
        Assert.False(model.LastSaveSucceeded);
        Assert.True(model.IsConfigurationLoaded);
        Assert.Equal("应用失败", model.StatusText);
        Assert.False(model.IsEngineReady);
        Assert.Contains("应用失败", model.Diagnostic);
        Assert.DoesNotContain("已还原", model.ConfigurationMessage + model.Diagnostic);
        Assert.DoesNotContain("secret-value", model.ConfigurationMessage + model.Diagnostic + string.Join(" ", diagnostics.Messages));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MismatchedQuickSourceReadbackDisplaysActualSelectionAndKeepsFailure(bool dirtyDraft)
    {
        var saved = OutboundSettings.Default with { Enabled = true, HttpVersion = "h2", ConnectTimeoutMs = 4200 };
        var service = new OutboundUiTestService(saved);
        service.SaveAction = (settings, _) =>
        {
            service.Settings = settings with { Sources = ["bahamut", "tmdb", "dandan"], ConnectTimeoutMs = 4700 };
            service.Publish(OutboundUiTestService.Ready(service.Settings));
            return Task.FromResult(new OutboundOperationResult(true, "已保存。"));
        };
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        if (dirtyDraft) { model.Tmdb = false; model.ConnectTimeoutText = "5000"; }
        await model.ToggleSourceCommand.ExecuteAsync("tmdb");
        Assert.Single(service.Saves);
        AssertSavedSources(model, service.Settings);
        Assert.True(model.SavedTmdbSelected);
        Assert.False(model.SavedAnimekoSelected);
        Assert.Equal(!dirtyDraft, model.Tmdb);
        Assert.Equal(dirtyDraft, model.Animeko);
        Assert.Equal(dirtyDraft ? "5000" : "4700", model.ConnectTimeoutText);
        Assert.Equal(dirtyDraft, model.HasUnsavedChanges);
        Assert.Contains("4700", model.ConnectionSummary);
        Assert.Contains("回读不一致", model.Diagnostic);
        Assert.False(model.LastSaveSucceeded);
        Assert.Equal("应用失败", model.StatusText);
        Assert.False(model.IsEngineReady);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuickSourceReadbackFailureIsExplicitAndDoesNotLoadDefaults(bool serviceSucceeded)
    {
        var saved = OutboundSettings.Default with { Enabled = true, Sources = ["tmdb"], HttpVersion = "h2", ConnectTimeoutMs = 4200 };
        var service = new OutboundUiTestService(saved);
        service.SaveAction = (settings, _) =>
        {
            service.Settings = settings;
            service.Publish(OutboundUiTestService.Ready(settings));
            service.ReadError = new IOException("回读权限失败 token=read-secret");
            return Task.FromResult(new OutboundOperationResult(serviceSucceeded, "保存结果 token=save-secret"));
        };
        var diagnostics = new OutboundUiTestDiagnostics();
        using var model = new OutboundDirectViewModel(service, diagnostics);
        await model.ToggleSourceCommand.ExecuteAsync("bahamut");
        Assert.Single(service.Saves);
        Assert.Equal(new[] { "tmdb", "bahamut" }, service.Settings.Sources);
        Assert.False(model.IsConfigurationLoaded);
        Assert.False(model.CanToggleSource);
        Assert.False(model.ToggleSourceCommand.CanExecute("animeko"));
        Assert.False(model.LastSaveSucceeded);
        Assert.Contains("回读权限失败", model.Diagnostic);
        Assert.Equal("已选 — / 4", model.SourceSelectionText);
        Assert.Equal("配置读取失败", model.SourceSummary);
        Assert.Equal("配置读取失败", model.ConnectionSummary);
        Assert.Equal("h2", model.SelectedHttpOption!.Value);
        Assert.Equal("4200", model.ConnectTimeoutText);
        Assert.False(model.IsEngineReady);
        Assert.Equal("应用失败", model.StatusText);
        var messages = model.ConfigurationMessage + model.Diagnostic + string.Join(" ", diagnostics.Messages);
        Assert.DoesNotContain("read-secret", messages);
        Assert.DoesNotContain("save-secret", messages);
        await model.ToggleSourceCommand.ExecuteAsync("animeko");
        Assert.Single(service.Saves);
    }

    [Theory]
    [InlineData("before-write")]
    [InlineData("after-write")]
    [InlineData("read-error")]
    public async Task QuickSourceSaveExceptionRemainsFailedAndReconcilesDiskOnlyWhenReadable(string failure)
    {
        var saved = OutboundSettings.Default with { Enabled = true, Sources = ["tmdb"], ConnectTimeoutMs = 4200 };
        var service = new OutboundUiTestService(saved);
        var error = new IOException("写入异常 Bearer save-secret", new InvalidOperationException("api_key=inner-secret"));
        if (failure == "before-write") service.SaveError = error;
        else service.SaveAction = (settings, _) =>
        {
            service.Settings = settings;
            service.Publish(OutboundUiTestService.Ready(settings));
            if (failure == "read-error") service.ReadError = new IOException("异常后回读失败 token=read-secret");
            return Task.FromException<OutboundOperationResult>(error);
        };
        var diagnostics = new OutboundUiTestDiagnostics();
        using var model = new OutboundDirectViewModel(service, diagnostics);
        await model.ToggleSourceCommand.ExecuteAsync("bahamut");
        Assert.Equal(failure == "before-write" ? 0 : 1, service.Saves.Count);
        Assert.False(model.LastSaveSucceeded);
        Assert.False(model.IsEngineReady);
        Assert.Equal("应用失败", model.StatusText);
        Assert.Contains("写入异常", model.Diagnostic);
        if (failure == "read-error")
        {
            Assert.False(model.IsConfigurationLoaded);
            Assert.Contains("异常后回读失败", model.Diagnostic);
            Assert.False(model.CanToggleSource);
        }
        else
        {
            Assert.True(model.IsConfigurationLoaded);
            AssertSavedSources(model, service.Settings);
            Assert.Equal(failure == "after-write", model.Bahamut);
            Assert.False(model.HasUnsavedChanges);
        }
        var messages = model.ConfigurationMessage + model.Diagnostic + string.Join(" ", diagnostics.Messages);
        Assert.DoesNotContain("save-secret", messages);
        Assert.DoesNotContain("inner-secret", messages);
        Assert.DoesNotContain("read-secret", messages);
    }

    [Fact]
    public async Task EnabledDiagnoseGateAlsoUsesSavedSettings()
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Enabled = true });
        service.Publish(OutboundUiTestService.Ready(service.Settings));
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        model.Enabled = false;
        Assert.True(model.CanDiagnose);
        await model.ToggleEnabledCommand.ExecuteAsync(null);
        Assert.False(Assert.Single(service.Saves).Enabled);
        Assert.False(model.CanDiagnose);
        Assert.True(model.CanOpenDiagnostics);
        await model.DiagnoseCommand.ExecuteAsync(null);
        Assert.Contains("不会自动开启", model.DiagnosticStatus);
        Assert.Equal(0, service.DiagnoseCalls);
        Assert.Single(service.Saves);
    }

    [Fact]
    public void ConfigurationCancelRestoresActualSavedSettingsAndDropsAllDraftFields()
    {
        var service = new OutboundUiTestService();
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        service.Settings = service.Settings with
        {
            Sources = ["tmdb"], HttpVersion = "h2", DohUrl = "https://actual.example.test/dns-query", ConnectTimeoutMs = 4200,
        };
        Assert.True(model.BeginConfigurationEdit());
        Assert.True(model.IsConfigurationEditing);
        Assert.Equal("https://actual.example.test/dns-query", model.DohUrl);
        Assert.False(model.CanOpenConfiguration);
        model.Tmdb = false;
        model.Bahamut = true;
        model.SelectedHttpOption = model.HttpOptions.Single(option => option.Value == "h3");
        model.DohUrl = "https://draft.example.test/query";
        model.ConnectTimeoutText = "9000";
        Assert.True(model.HasUnsavedChanges);
        model.DiscardConfigurationEdit();
        Assert.False(model.IsConfigurationEditing);
        Assert.False(model.HasUnsavedChanges);
        Assert.True(model.Tmdb);
        Assert.False(model.Bahamut || model.Dandan || model.Animeko);
        Assert.Equal("h2", model.SelectedHttpOption!.Value);
        Assert.Equal("https://actual.example.test/dns-query", model.DohUrl);
        Assert.Equal("4200", model.ConnectTimeoutText);
        Assert.Empty(service.Saves);
    }

    [Fact]
    public void FailedConfigurationReadNeverOpensOrLoadsDefaults()
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { DohUrl = "https://saved.example.test/query", ConnectTimeoutMs = 4200 });
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        model.DohUrl = "https://draft.example.test/query";
        service.ReadError = new IOException("配置损坏 token=secret-value");
        Assert.False(model.BeginConfigurationEdit());
        Assert.False(model.IsConfigurationEditing);
        Assert.False(model.IsConfigurationLoaded);
        Assert.False(model.LastSaveSucceeded);
        Assert.Contains("读取", model.Diagnostic);
        Assert.DoesNotContain("secret-value", model.Diagnostic);
        Assert.Equal("https://draft.example.test/query", model.DohUrl);
        model.DiscardConfigurationEdit();
        Assert.Equal("https://saved.example.test/query", model.DohUrl);
        Assert.Equal("4200", model.ConnectTimeoutText);
        Assert.False(model.IsConfigurationLoaded);
        Assert.False(model.CanApply);
        Assert.Empty(service.Saves);
    }

    [Fact]
    public async Task SuccessfulSaveClosesEditingOnlyAfterValidatedReadback()
    {
        var service = new OutboundUiTestService();
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SaveAction = async (settings, token) =>
        {
            await response.Task.WaitAsync(token);
            service.Settings = settings;
            service.Publish(new("starting", "已保存，等待当前服务应用", settings, ServiceRunning: true));
            return new(true, "配置已保存，等待应用。");
        };
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        Assert.True(model.BeginConfigurationEdit());
        model.DohUrl = "https://dns.example.test/query";
        var pending = model.ApplyConfigurationCommand.ExecuteAsync(null);
        Assert.True(model.IsConfigurationEditing);
        Assert.False(model.LastSaveSucceeded);
        Assert.Equal("正在应用", model.StatusText);
        Assert.False(model.BeginConfigurationEdit());
        response.SetResult();
        await pending;
        Assert.True(model.LastSaveSucceeded);
        Assert.False(model.IsConfigurationEditing);
        Assert.False(model.HasUnsavedChanges);
        Assert.False(model.IsEngineReady);
        Assert.False(model.Snapshot.Applied);
    }

    [Fact]
    public async Task SaveFailureResetsPreviousSuccessAndKeepsConfigurationDialogOpen()
    {
        var service = new OutboundUiTestService();
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        Assert.True(model.BeginConfigurationEdit());
        model.ConnectTimeoutText = "4100";
        await model.ApplyConfigurationCommand.ExecuteAsync(null);
        Assert.True(model.LastSaveSucceeded);
        Assert.True(model.BeginConfigurationEdit());
        Assert.False(model.LastSaveSucceeded);
        model.ConnectTimeoutText = "4200";
        service.SaveError = new IOException("save failed token=secret-value");
        await model.ApplyConfigurationCommand.ExecuteAsync(null);
        Assert.False(model.LastSaveSucceeded);
        Assert.True(model.IsConfigurationEditing);
        Assert.True(model.HasUnsavedChanges);
        Assert.Equal(4100, service.Settings.ConnectTimeoutMs);
        Assert.DoesNotContain("secret-value", model.Diagnostic);
        Assert.Equal("应用失败", model.StatusText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSaveResultOrMismatchedReadbackCannotCloseTheDialog(bool mismatchedReadback)
    {
        var service = new OutboundUiTestService();
        service.SaveAction = (settings, _) =>
        {
            service.Settings = settings with { ConnectTimeoutMs = mismatchedReadback ? 1234 : settings.ConnectTimeoutMs };
            return Task.FromResult(new OutboundOperationResult(mismatchedReadback, "保存或回读失败"));
        };
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        Assert.True(model.BeginConfigurationEdit());
        model.ConnectTimeoutText = "4200";
        await model.ApplyConfigurationCommand.ExecuteAsync(null);
        Assert.False(model.LastSaveSucceeded);
        Assert.True(model.IsConfigurationEditing);
        Assert.True(model.HasDiagnostic);
        Assert.Contains(mismatchedReadback ? "回读不一致" : "保存或回读失败", model.Diagnostic);
        Assert.Contains(service.Settings.ConnectTimeoutMs.ToString(), model.ConnectionSummary);
    }

    [Fact]
    public async Task FailedSaveReadbackKeepsTheReadFailureExplicit()
    {
        var service = new OutboundUiTestService();
        service.SaveAction = (settings, _) =>
        {
            service.Settings = settings;
            service.ReadError = new IOException("回读权限失败");
            return Task.FromResult(new OutboundOperationResult(true, "已写入"));
        };
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        Assert.True(model.BeginConfigurationEdit());
        model.ConnectTimeoutText = "4200";
        await model.ApplyConfigurationCommand.ExecuteAsync(null);
        Assert.False(model.LastSaveSucceeded);
        Assert.True(model.IsConfigurationEditing);
        Assert.False(model.IsConfigurationLoaded);
        Assert.Contains("回读权限失败", model.Diagnostic);
        Assert.Equal("配置读取失败", model.ConnectionSummary);
        Assert.False(model.CanToggleEnabled);
    }

    [Fact]
    public async Task InvalidDraftIsReportedBeforeApplyAndNeverSaved()
    {
        var service = new OutboundUiTestService();
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        await model.ToggleEnabledCommand.ExecuteAsync(null);
        Assert.True(model.BeginConfigurationEdit());
        model.Bahamut = model.Tmdb = model.Dandan = model.Animeko = false;
        model.ConnectTimeoutText = "3.5";
        Assert.True(model.HasProblems);
        Assert.Contains("至少选择一个", model.ValidationText);
        Assert.Contains("整数", model.ValidationText);
        Assert.False(model.CanApply);
        await model.ApplyConfigurationCommand.ExecuteAsync(null);
        Assert.Single(service.Saves);
        Assert.False(model.LastSaveSucceeded);
        model.Bahamut = true;
        model.ConnectTimeoutText = "3000";
        model.DohUrl = "http://dns.example.test/dns-query";
        Assert.Contains("HTTPS", model.ValidationText);
        Assert.False(model.CanApply);
    }

    [Theory]
    [MemberData(nameof(BadSettings))]
    public void InvalidSavedSettingsAreStrictlyRejected(OutboundSettings settings)
    {
        var service = new OutboundUiTestService(settings);
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        Assert.False(model.IsConfigurationLoaded);
        Assert.True(model.IsEngineFailed);
        Assert.False(model.IsEngineReady);
        Assert.False(model.CanApply);
        Assert.False(model.CanToggleEnabled);
        Assert.Null(model.SelectedHttpOption);
        Assert.True(model.HasDiagnostic);
        Assert.Equal("配置读取失败", model.SourceSummary);
        Assert.Empty(service.Saves);
    }

    public static IEnumerable<object[]> BadSettings()
    {
        yield return [OutboundSettings.Default with { Sources = ["unsupported"] }];
        yield return [OutboundSettings.Default with { Sources = ["tmdb", "tmdb"] }];
        yield return [OutboundSettings.Default with { Enabled = true, Sources = [] }];
        yield return [OutboundSettings.Default with { HttpVersion = "fallback" }];
        yield return [OutboundSettings.Default with { DohUrl = "http://dns.example.test/query" }];
        yield return [OutboundSettings.Default with { ConnectTimeoutMs = 0 }];
        yield return [OutboundSettings.Default with { SchemaVersion = 99 }];
    }

    [Fact]
    public async Task MainSummariesRemainSavedAndCustomDohNeverExposesItsPathOrQuery()
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with
        {
            Sources = ["tmdb"], HttpVersion = "h2", DohUrl = "https://dns.example.test/private-path?api_key=secret-value", ConnectTimeoutMs = 4200,
        });
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        var source = model.SourceSummary;
        var connection = model.ConnectionSummary;
        Assert.Equal("TMDB", source);
        Assert.Contains("HTTP/2", connection);
        Assert.Contains("dns.example.test", connection);
        Assert.Contains("4200 ms", connection);
        Assert.DoesNotContain("private-path", connection);
        Assert.DoesNotContain("secret-value", connection);
        Assert.DoesNotContain("api_key", connection);
        model.Tmdb = false;
        model.Bahamut = true;
        model.SelectedHttpOption = model.HttpOptions.Single(option => option.Value == "h3");
        model.DohUrl = "https://draft.example.test/query";
        model.ConnectTimeoutText = "5000";
        Assert.Equal(source, model.SourceSummary);
        Assert.Equal(connection, model.ConnectionSummary);
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(source, model.SourceSummary);
        Assert.Equal(connection, model.ConnectionSummary);
        Assert.Equal("5000", model.ConnectTimeoutText);
        Assert.True(model.HasUnsavedChanges);
        Assert.Equal(new[] { "自动选择", "仅 HTTP/2", "仅 HTTP/3" }, model.HttpOptions.Select(option => option.Label));
    }

    [Fact]
    public async Task SuccessfulRefreshClearsOldHostAndConfigurationErrorsButNotTestErrors()
    {
        var service = new OutboundUiTestService { ReadError = new IOException("坏配置 token=secret-value") };
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        Assert.True(model.HasDiagnostic);
        Assert.False(model.IsConfigurationLoaded);
        service.ReadError = null;
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.True(model.IsConfigurationLoaded);
        Assert.False(model.HasDiagnostic);
        Assert.False(model.IsEngineFailed);
        service.RefreshError = new IOException("refresh failed");
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("应用失败", model.StatusText);
        Assert.False(model.IsEngineReady);
        Assert.Contains("refresh failed", model.StatusReason);
        model.DiagnosticError = "上次测速失败";
        service.RefreshError = null;
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Empty(model.Diagnostic);
        Assert.Equal("上次测速失败", model.DiagnosticError);
        Assert.Equal("已关闭", model.StatusText);
    }

    [Fact]
    public void RealReadinessAndOldHostRestartRequirementAreNeverInferredFromConfiguration()
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Enabled = true });
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        service.Publish(new("failed", "旧宿主未应用", service.Settings, ServiceRunning: true, Applied: false));
        Assert.Equal("应用失败", model.StatusText);
        Assert.Contains("手动重启", model.StatusDescription);
        Assert.Contains("旧宿主", model.StatusReason);
        Assert.False(model.IsEngineReady);
        Assert.False(model.CanDiagnose);
        service.Publish(OutboundUiTestService.Ready(service.Settings));
        Assert.Equal("增强直连运行中", model.StatusText);
        Assert.True(model.IsEngineReady);
        Assert.True(model.HasRuntimeDetails);
        Assert.Contains("能力协议 1", model.CapabilityText);
        Assert.Contains("test-1", model.CapabilityText);
        Assert.Contains("500", model.ProcessText);
        Assert.Contains("业务认证", model.StatusDescription);
        service.Publish(new("ready", "没有应用配置", service.Settings, ServiceRunning: true, Applied: false));
        Assert.False(model.IsEngineReady);
        service.Publish(OutboundUiTestService.Ready(service.Settings with { ConnectTimeoutMs = 999 }));
        Assert.False(model.IsEngineReady);
        service.Publish(new("unsupported", "无效状态", service.Settings));
        Assert.True(model.IsEngineFailed);
        Assert.Equal("应用失败", model.StatusText);
        Assert.Contains("不支持", model.StatusReason);
    }

    [Fact]
    public async Task PerDomainResultsRemainIndependentAnd401DoesNotPolluteMainDiagnostic()
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Enabled = true });
        service.Publish(OutboundUiTestService.Ready(service.Settings));
        service.Rows =
        [
            new("dandan", "api.danmaku.weeblify.app", true, "h3", false, null, TimeSpan.FromMilliseconds(80), "ech", "原目标拒绝 ECH"),
            new("dandan", "nipaplay.aimes-soft.com", false, "h2", false, 200, TimeSpan.FromMilliseconds(50), null, "普通 TLS"),
            new("tmdb", "api.tmdb.org", false, "h2", false, 401, TimeSpan.FromMilliseconds(60), "http", "公开探测未认证 token=secret-value"),
            new("animeko", "api.animeko.org", false, null, false, 200, TimeSpan.FromMilliseconds(20), null, "缺实际协议"),
        ];
        var clock = new OutboundTestClock(new DateTimeOffset(2026, 10, 1, 12, 34, 56, TimeSpan.Zero));
        service.Clock = clock;
        var diagnostics = new OutboundUiTestDiagnostics();
        using var model = new OutboundDirectViewModel(service, diagnostics, clock);
        await model.DiagnoseCommand.ExecuteAsync(null);
        Assert.Equal(4, model.DiagnosticRows.Count);
        Assert.False(model.DiagnosticRows[0].Succeeded);
        Assert.Equal("HTTP/3", model.DiagnosticRows[0].Protocol);
        Assert.True(model.DiagnosticRows[1].Succeeded);
        Assert.Equal("连接正常", model.DiagnosticRows[1].OutcomeText);
        Assert.Equal("需要API密钥", model.DiagnosticRows[2].OutcomeText);
        Assert.False(model.DiagnosticRows[2].Succeeded);
        Assert.Contains("业务认证：需要 TMDB API 密钥（HTTP 401）", model.DiagnosticRows[2].DetailText);
        Assert.Contains("不记为业务成功", model.DiagnosticRows[2].DetailText);
        Assert.Contains("公开探测未认证", model.DiagnosticRows[2].DetailText);
        Assert.DoesNotContain("失败阶段：", model.DiagnosticRows[2].DetailText);
        Assert.DoesNotContain("secret-value", model.DiagnosticRows[2].DetailText);
        Assert.Equal("未确认", model.DiagnosticRows[3].Protocol);
        Assert.Equal("连接失败", model.DiagnosticRows[3].OutcomeText);
        Assert.Contains("失败阶段：ech", model.DiagnosticRows[0].DetailText);
        Assert.Contains("原目标拒绝 ECH", model.DiagnosticRows[0].DetailText);
        Assert.Contains("缺实际协议", model.DiagnosticRows[3].DetailText);
        Assert.Contains("连接失败 2", model.DiagnosticStatus);
        Assert.Contains("需要API密钥 1", model.DiagnosticStatus);
        Assert.Contains("选中对应行", model.DiagnosticStatus);
        Assert.DoesNotContain("api.danmaku.weeblify.app", model.DiagnosticStatus);
        Assert.DoesNotContain("原目标拒绝 ECH", model.DiagnosticStatus);
        Assert.Empty(model.DiagnosticError);
        Assert.False(model.HasDiagnosticError);
        Assert.Equal(3, diagnostics.Messages.Count);
        foreach (var row in model.DiagnosticRows.Where(row => !row.Succeeded))
        {
            var message = Assert.Single(diagnostics.Messages, value => value.Contains(row.Host, StringComparison.Ordinal));
            Assert.Contains(row.DetailText, message);
        }
        Assert.Empty(model.Diagnostic);
        Assert.True(model.IsEngineReady);
        Assert.False(model.IsEngineFailed);
        Assert.True(model.HasRecentTest);
        Assert.Equal("2026-10-01 12:34:56", model.RecentTestTime);
        Assert.Equal("1", model.RecentNormalCount);
        Assert.Equal("1", model.RecentNeedsKeyCount);
        Assert.Equal("2", model.RecentFailedCount);
        Assert.Contains("需要API密钥 1", model.RecentTestSummary);
        Assert.DoesNotContain("api.danmaku.weeblify.app", model.RecentTestSummary);
        Assert.DoesNotContain("secret-value", string.Join(" ", diagnostics.Messages));

        service.Settings = service.Settings with { ConnectTimeoutMs = 4200 };
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Contains("上次测试，配置已更改", model.RecentTestSummary);
        Assert.Equal("2026-10-01 12:34:56", model.RecentTestTime);
    }

    [Theory]
    [MemberData(nameof(BadRows))]
    public void MissingOrInconsistentTransportNeverBecomesConnectionSuccess(OutboundDiagnosticRow row, string httpVersion)
    {
        var item = new OutboundDiagnosticItem(row, httpVersion);
        Assert.False(item.Succeeded);
        Assert.False(item.NeedsApiKey);
        Assert.Equal("连接失败", item.OutcomeText);
        Assert.Contains(row.Diagnostic, item.DetailText);
        if (!string.IsNullOrWhiteSpace(row.FailurePhase))
            Assert.Contains("失败阶段：" + row.FailurePhase, item.DetailText);
    }

    public static IEnumerable<object[]> BadRows()
    {
        var row = new OutboundDiagnosticRow("tmdb", "api.tmdb.org", false, "h2", false, 200, TimeSpan.Zero, null, "原始错误证据");
        yield return [row with { Protocol = null }, "auto"];
        yield return [row with { Protocol = "unknown" }, "auto"];
        yield return [row with { Protocol = "http/1.1" }, "auto"];
        yield return [row with { EchAccepted = null }, "auto"];
        yield return [row with { EchRequired = true, EchAccepted = false }, "auto"];
        yield return [row with { EchAccepted = true }, "auto"];
        yield return [row with { HttpStatus = null }, "auto"];
        yield return [row with { HttpStatus = 0 }, "auto"];
        yield return [row with { HttpStatus = 500 }, "auto"];
        yield return [row with { FailurePhase = "response" }, "auto"];
        yield return [row, "h3"];
        yield return [row, "unsupported"];
        yield return [row with { HttpStatus = 401, FailurePhase = "http" }, "h3"];
        yield return [row with { HttpStatus = 401, FailurePhase = "auth" }, "auto"];
        yield return [row with { HttpStatus = 401, EchAccepted = true }, "auto"];
    }

    [Fact]
    public async Task CancellationAndEmptyResultsUseOnlyTestErrorAndNeverBecomeSuccess()
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Enabled = true });
        service.Publish(OutboundUiTestService.Ready(service.Settings));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnoseAction = async token => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return service.Run(); };
        var diagnostics = new OutboundUiTestDiagnostics();
        using var model = new OutboundDirectViewModel(service, diagnostics);
        var pending = model.DiagnoseCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(model.CanCancel);
        model.CancelCommand.Execute(null);
        await pending;
        Assert.Contains("已取消", model.DiagnosticStatus);
        Assert.Contains("已取消", model.DiagnosticError);
        Assert.Empty(model.Diagnostic);
        Assert.Empty(model.DiagnosticRows);
        Assert.False(model.IsBusy);
        Assert.False(model.HasRecentTest);
        Assert.Empty(model.RecentTestSummary);
        Assert.Empty(model.RecentTestTime);
        Assert.Equal("—", model.RecentNormalCount);
        Assert.Equal("—", model.RecentNeedsKeyCount);
        Assert.Equal("—", model.RecentFailedCount);
        Assert.Contains(diagnostics.Messages, value => value.Contains("已取消"));
        service.DiagnoseAction = null;
        await model.DiagnoseCommand.ExecuteAsync(null);
        Assert.Contains("未返回任何目标域名", model.DiagnosticError);
        Assert.Empty(model.Diagnostic);
        Assert.False(model.HasRecentTest);
        Assert.Empty(model.RecentTestSummary);
        Assert.Empty(model.RecentTestTime);
        Assert.Equal("—", model.RecentNormalCount);
        Assert.Equal("—", model.RecentNeedsKeyCount);
        Assert.Equal("—", model.RecentFailedCount);
    }

    [Theory]
    [InlineData("cancelled-task", true)]
    [InlineData("cancelled-run", true)]
    [InlineData("failed-task", true)]
    [InlineData("failed-run", true)]
    [InlineData("cancelled-task", false)]
    [InlineData("cancelled-run", false)]
    [InlineData("failed-task", false)]
    [InlineData("failed-run", false)]
    public async Task CancellationCompletionKeepsTerminalStatusAndCompletedHistory(string outcome, bool completeInline)
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Enabled = true }) { Rows = CompletedRows() };
        service.Publish(OutboundUiTestService.Ready(service.Settings));
        service.SaveAction = (settings, _) =>
        {
            service.Settings = settings;
            service.Publish(OutboundUiTestService.Ready(settings));
            return Task.FromResult(new OutboundOperationResult(true, "已保存。"));
        };
        var clock = new OutboundTestClock(new DateTimeOffset(2026, 10, 1, 12, 34, 56, TimeSpan.Zero));
        service.Clock = clock;
        var diagnostics = new OutboundUiTestDiagnostics();
        using var model = new OutboundDirectViewModel(service, diagnostics, clock);
        await model.DiagnoseCommand.ExecuteAsync(null);
        var completedSummary = model.RecentTestSummary;
        var completedTime = model.RecentTestTime;
        var rows = model.DiagnosticRows.ToArray();
        var selected = model.SelectedDiagnostic;
        await model.ToggleSourceCommand.ExecuteAsync("animeko");
        var changedSummary = model.RecentTestSummary;
        Assert.Equal(completedSummary + " · 上次测试，配置已更改", changedSummary);
        clock.Now = clock.Now.AddHours(1);

        // No asynchronous continuation option: completion must run the VM continuation inside the callback.
        var response = new TaskCompletionSource<OutboundDiagnosticRun>();
        var ordering = new List<string>();
        var cancellationRequested = false;
        var completedInsideCallback = false;
        var statusInsideCallback = string.Empty;
        var token = CancellationToken.None;
        CancellationTokenRegistration registration = default;
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(model.DiagnosticStatus)) ordering.Add("status: " + model.DiagnosticStatus);
        };
        service.DiagnoseAction = cancellationToken =>
        {
            token = cancellationToken;
            registration = token.Register(() =>
            {
                ordering.Add("callback: enter");
                cancellationRequested = true;
                if (completeInline) CompleteCancellationResponse(response, token, outcome);
                completedInsideCallback = !model.IsDiagnosing && !model.IsBusy;
                statusInsideCallback = model.DiagnosticStatus;
                ordering.Add($"callback: return; completed={completedInsideCallback}; status={statusInsideCallback}");
            });
            return response.Task;
        };
        Task pending;
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new InlineContext());
            pending = model.DiagnoseCommand.ExecuteAsync(null);
            Assert.False(pending.IsCompleted);
            Assert.True(model.CanCancel);
            ordering.Add("cancel: enter");
            model.CancelCommand.Execute(null);
            ordering.Add("cancel: return; status=" + model.DiagnosticStatus);
            output.WriteLine(string.Join(Environment.NewLine, ordering));
            Assert.True(cancellationRequested);
            Assert.Equal(completeInline, completedInsideCallback);
            Assert.False(model.CanCancel);
            Assert.False(model.CancelCommand.CanExecute(null));
            if (completeInline)
            {
                Assert.True(pending.IsCompletedSuccessfully);
                Assert.Equal(statusInsideCallback, model.DiagnosticStatus);
            }
            else
            {
                Assert.False(pending.IsCompleted);
                Assert.True(model.IsDiagnosing);
                Assert.True(model.IsBusy);
                Assert.Equal("已请求取消，正在等待当前连接检查结束…", model.DiagnosticStatus);
                Assert.Empty(model.DiagnosticError);
                Assert.Equal(changedSummary, model.RecentTestSummary);
                Assert.Equal(completedTime, model.RecentTestTime);
                Assert.Equal("1", model.RecentNormalCount);
                Assert.Equal("1", model.RecentNeedsKeyCount);
                Assert.Equal("1", model.RecentFailedCount);
                CompleteCancellationResponse(response, token, outcome);
                Assert.True(pending.IsCompletedSuccessfully);
                output.WriteLine("deferred: completed; status=" + model.DiagnosticStatus);
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            registration.Dispose();
        }
        await pending;
        var failed = outcome.StartsWith("failed-", StringComparison.Ordinal);
        Assert.StartsWith(failed ? "测速失败；" : "测速已取消；", model.DiagnosticStatus);
        Assert.DoesNotContain("正在等待", model.DiagnosticStatus);
        Assert.DoesNotContain("测速完成", model.DiagnosticStatus);
        Assert.True(model.HasDiagnosticError);
        if (outcome != "cancelled-task") Assert.Contains("取消回调", model.DiagnosticError);
        Assert.Contains(model.DiagnosticError, diagnostics.Messages);
        Assert.DoesNotContain("test-secret", model.DiagnosticError + string.Join(" ", diagnostics.Messages));
        Assert.Empty(model.Diagnostic);
        Assert.False(model.IsDiagnosing);
        Assert.False(model.IsBusy);
        Assert.True(model.HasRecentTest);
        Assert.Equal(changedSummary, model.RecentTestSummary);
        Assert.Equal(completedTime, model.RecentTestTime);
        Assert.Equal("1", model.RecentNormalCount);
        Assert.Equal("1", model.RecentNeedsKeyCount);
        Assert.Equal("1", model.RecentFailedCount);
        Assert.Equal(rows, model.DiagnosticRows);
        Assert.Same(selected, model.SelectedDiagnostic);
        Assert.Equal(2, service.DiagnoseCalls);
        await model.ToggleSourceCommand.ExecuteAsync("animeko");
        Assert.Equal(completedSummary, model.RecentTestSummary);
        Assert.Equal(completedTime, model.RecentTestTime);
    }

    private static void CompleteCancellationResponse(TaskCompletionSource<OutboundDiagnosticRun> response,
        CancellationToken token, string outcome)
    {
        switch (outcome)
        {
            case "cancelled-task": response.SetCanceled(token); break;
            case "cancelled-run":
                response.SetResult(new(OutboundDiagnosticRunStatus.Cancelled, null, [], null, "取消回调返回取消结果。"));
                break;
            case "failed-task": response.SetException(new IOException("取消回调内连接检查失败 token=test-secret")); break;
            case "failed-run":
                response.SetResult(new(OutboundDiagnosticRunStatus.Failed, null, [], null, "取消回调返回失败 token=test-secret"));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(outcome));
        }
    }

    [Fact]
    public async Task TestExceptionCannotReplaceAConcurrentMainDialogFailure()
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Enabled = true });
        service.Publish(OutboundUiTestService.Ready(service.Settings));
        var response = new TaskCompletionSource<OutboundDiagnosticRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnoseAction = token => response.Task.WaitAsync(token);
        var diagnostics = new OutboundUiTestDiagnostics();
        using var model = new OutboundDirectViewModel(service, diagnostics);
        var pending = model.DiagnoseCommand.ExecuteAsync(null);
        model.ReportDialogFailure("打开配置弹窗失败", new IOException("https://user:password@dns.example.test/path?api_key=secret-value", new InvalidOperationException("\"token\":\"inner-secret\"")));
        var mainFailure = model.Diagnostic;
        response.SetException(new IOException("测速超时 Bearer test-secret"));
        await pending;
        Assert.Equal(mainFailure, model.Diagnostic);
        Assert.Contains("测速超时", model.DiagnosticError);
        Assert.DoesNotContain("test-secret", model.DiagnosticError);
        Assert.DoesNotContain("password", string.Join(" ", diagnostics.Messages));
        Assert.DoesNotContain("secret-value", string.Join(" ", diagnostics.Messages));
        Assert.DoesNotContain("inner-secret", string.Join(" ", diagnostics.Messages));
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task DirtyDraftBlocksTestsWithoutSavingOrStartingAnything()
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Enabled = true });
        service.Publish(OutboundUiTestService.Ready(service.Settings));
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        Assert.True(model.BeginConfigurationEdit());
        model.ConnectTimeoutText = "4500";
        Assert.False(model.CanDiagnose);
        await model.DiagnoseCommand.ExecuteAsync(null);
        Assert.Contains("保存或取消", model.DiagnosticStatus);
        Assert.Equal(0, service.DiagnoseCalls);
        Assert.Empty(service.Saves);
        model.DiscardConfigurationEdit();
        Assert.True(model.CanDiagnose);
    }

    [Fact]
    public void PollingStartsOncePreservesDraftAndStopsOnLeaveAndDispose()
    {
        var service = new OutboundUiTestService();
        var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        model.DohUrl = "https://draft.example.test/dns-query";
        service.Settings = service.Settings with { DohUrl = "https://external.example.test/dns-query" };
        model.BeginMonitoring();
        model.BeginMonitoring();
        Assert.Equal(1, service.RefreshCalls);
        Assert.Equal("https://draft.example.test/dns-query", model.DohUrl);
        Assert.Contains("external.example.test", model.ConnectionSummary);
        Assert.True(model.HasUnsavedChanges);
        Assert.True(model.IsMonitoring);
        model.EndMonitoring();
        Assert.False(model.IsMonitoring);
        model.BeginMonitoring();
        Assert.Equal(2, service.RefreshCalls);
        var snapshot = model.Snapshot;
        model.Dispose();
        Assert.Equal(0, service.Subscribers);
        Assert.False(model.IsMonitoring);
        Assert.False(model.CanOpenConfiguration || model.CanOpenDiagnostics || model.CanRefresh);
        model.BeginMonitoring();
        service.Publish(new("ready", "迟到事件"));
        Assert.Equal(snapshot, model.Snapshot);
        Assert.Equal(2, service.RefreshCalls);
        Assert.Empty(service.Saves);
        model.Dispose();
    }

    [Fact]
    public async Task DisposeCancelsAnInFlightTestAndRejectsLateResults()
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Enabled = true });
        service.Publish(OutboundUiTestService.Ready(service.Settings));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnoseAction = async token => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return service.Run(); };
        var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        var pending = model.DiagnoseCommand.ExecuteAsync(null);
        await started.Task;
        var snapshot = model.Snapshot;
        model.Dispose();
        await pending;
        service.Publish(new("failed", "迟到失败"));
        Assert.Equal(snapshot, model.Snapshot);
        Assert.Empty(model.DiagnosticRows);
        Assert.False(model.HasRecentTest);
        Assert.False(model.CanDiagnose);
        Assert.Equal(0, service.Subscribers);
    }

    [Fact]
    public async Task QuickSourceChangesKeepCompletedRowsAndMarkTheirOriginalConfiguration()
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Enabled = true }) { Rows = CompletedRows() };
        service.Publish(OutboundUiTestService.Ready(service.Settings));
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        await model.DiagnoseCommand.ExecuteAsync(null);
        var rows = model.DiagnosticRows.ToArray();
        var selected = model.SelectedDiagnostic;
        var time = model.RecentTestTime;
        var summary = model.RecentTestSummary;
        await model.ToggleSourceCommand.ExecuteAsync("animeko");
        Assert.Equal(rows, model.DiagnosticRows);
        Assert.Same(selected, model.SelectedDiagnostic);
        Assert.True(model.HasRecentTest);
        Assert.Equal(summary + " · 上次测试，配置已更改", model.RecentTestSummary);
        Assert.Equal(time, model.RecentTestTime);
        Assert.Equal("1", model.RecentNormalCount);
        Assert.Equal("1", model.RecentNeedsKeyCount);
        Assert.Equal("1", model.RecentFailedCount);
        Assert.False(model.HasUnsavedChanges);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("exception")]
    [InlineData("empty")]
    public async Task IncompleteTestsPreserveTheLastCompletedCountsTimeAndConfiguration(string outcome)
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Enabled = true }) { Rows = CompletedRows() };
        service.Publish(OutboundUiTestService.Ready(service.Settings));
        service.SaveAction = (settings, _) =>
        {
            service.Settings = settings;
            service.Publish(OutboundUiTestService.Ready(settings));
            return Task.FromResult(new OutboundOperationResult(true, "已保存。"));
        };
        var clock = new OutboundTestClock(new DateTimeOffset(2026, 10, 1, 12, 34, 56, TimeSpan.Zero));
        service.Clock = clock;
        var diagnostics = new OutboundUiTestDiagnostics();
        using var model = new OutboundDirectViewModel(service, diagnostics, clock);
        await model.DiagnoseCommand.ExecuteAsync(null);
        var completedSummary = model.RecentTestSummary;
        var completedTime = model.RecentTestTime;
        await model.ToggleSourceCommand.ExecuteAsync("animeko");
        var changedSummary = model.RecentTestSummary;
        Assert.Equal(completedSummary + " · 上次测试，配置已更改", changedSummary);
        Assert.True(model.CanDiagnose);
        clock.Now = clock.Now.AddHours(1);
        if (outcome == "cancel")
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.DiagnoseAction = async token => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return service.Run(); };
            var pending = model.DiagnoseCommand.ExecuteAsync(null);
            await started.Task;
            Assert.False(model.CanToggleSource);
            model.CancelCommand.Execute(null);
            await pending;
            Assert.Contains("已取消", model.DiagnosticStatus);
        }
        else
        {
            if (outcome == "exception")
                service.DiagnoseAction = _ => Task.FromException<OutboundDiagnosticRun>(new IOException("整次测速失败 token=test-secret"));
            else service.Rows = [];
            await model.DiagnoseCommand.ExecuteAsync(null);
            Assert.Contains("测速失败", model.DiagnosticStatus);
        }
        Assert.Equal(2, service.DiagnoseCalls);
        Assert.True(model.HasDiagnosticError);
        Assert.Empty(model.Diagnostic);
        Assert.True(model.HasRecentTest);
        Assert.Equal("1", model.RecentNormalCount);
        Assert.Equal("1", model.RecentNeedsKeyCount);
        Assert.Equal("1", model.RecentFailedCount);
        Assert.Equal(completedTime, model.RecentTestTime);
        Assert.Equal(changedSummary, model.RecentTestSummary);
        Assert.DoesNotContain("test-secret", model.DiagnosticError + string.Join(" ", diagnostics.Messages));
        await model.ToggleSourceCommand.ExecuteAsync("animeko");
        Assert.Equal(completedSummary, model.RecentTestSummary);
        Assert.Equal(completedTime, model.RecentTestTime);
        service.DiagnoseAction = null;
        service.Rows = [CompletedRows()[0]];
        await model.DiagnoseCommand.ExecuteAsync(null);
        Assert.Equal("1", model.RecentNormalCount);
        Assert.Equal("0", model.RecentNeedsKeyCount);
        Assert.Equal("0", model.RecentFailedCount);
        Assert.Equal("2026-10-01 13:34:56", model.RecentTestTime);
        Assert.Contains("1 个域名", model.RecentTestSummary);
        Assert.DoesNotContain("配置已更改", model.RecentTestSummary);
        Assert.False(model.HasDiagnosticError);
    }

    [Fact]
    public async Task ActualRunConfigurationClassifiesRowsAndLinksHistoryEvenWhenVmCachedAnotherConfig()
    {
        var h2 = OutboundSettings.Default with { Enabled = true, Sources = ["tmdb"], HttpVersion = "h2" };
        var h3 = h2 with { HttpVersion = "h3" };
        var service = new OutboundUiTestService(h2);
        service.Publish(OutboundUiTestService.Ready(h2));
        var completedAt = new DateTimeOffset(2026, 10, 1, 7, 8, 9, TimeSpan.Zero);
        service.DiagnoseAction = _ =>
        {
            service.Settings = h3;
            service.Publish(OutboundUiTestService.Ready(h3));
            return Task.FromResult(new OutboundDiagnosticRun(OutboundDiagnosticRunStatus.Completed, h3,
                [new("tmdb", "api.tmdb.org", false, "h3", false, 401, TimeSpan.FromMilliseconds(10), "http", "公开无凭据探测")],
                completedAt, "完成"));
        };
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics(),
            new OutboundTestClock(completedAt.AddHours(3)));
        Assert.Contains("HTTP/2", model.ConnectionSummary);
        await model.DiagnoseCommand.ExecuteAsync(null);
        Assert.True(Assert.Single(model.DiagnosticRows).NeedsApiKey);
        Assert.Equal("1", model.RecentNeedsKeyCount);
        Assert.Equal("0", model.RecentFailedCount);
        Assert.Equal("2026-10-01 07:08:09", model.RecentTestTime);
        Assert.Contains("HTTP/3", model.ConnectionSummary);
        Assert.DoesNotContain("配置已更改", model.RecentTestSummary);
        service.Settings = h2;
        service.Publish(OutboundUiTestService.Ready(h2));
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Contains("配置已更改", model.RecentTestSummary);
        Assert.Equal("2026-10-01 07:08:09", model.RecentTestTime);
    }

    [Theory]
    [InlineData(OutboundDiagnosticRunStatus.Failed)]
    [InlineData(OutboundDiagnosticRunStatus.Cancelled)]
    public async Task IncompleteEnvelopeNeverReplacesCompletedHistory(OutboundDiagnosticRunStatus status)
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Enabled = true }) { Rows = CompletedRows() };
        service.Publish(OutboundUiTestService.Ready(service.Settings));
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        await model.DiagnoseCommand.ExecuteAsync(null);
        var time = model.RecentTestTime;
        var summary = model.RecentTestSummary;
        var rows = model.DiagnosticRows.ToArray();
        service.DiagnoseAction = _ => Task.FromResult(new OutboundDiagnosticRun(status, null, [], null, "整次预检失败或取消"));
        await model.DiagnoseCommand.ExecuteAsync(null);
        Assert.Equal(time, model.RecentTestTime);
        Assert.Equal(summary, model.RecentTestSummary);
        Assert.Equal("1", model.RecentNormalCount);
        Assert.Equal("1", model.RecentNeedsKeyCount);
        Assert.Equal("1", model.RecentFailedCount);
        Assert.Equal(rows, model.DiagnosticRows);
        Assert.Contains("保留", model.DiagnosticStatus);
        Assert.Contains("预检", model.DiagnosticError);
        Assert.DoesNotContain("测速完成", model.DiagnosticStatus);
    }

    [Fact]
    public async Task StaleRefreshReturnCannotReplaceAnOffNotification()
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Enabled = true });
        service.Publish(OutboundUiTestService.Ready(service.Settings));
        var ready = service.Snapshot;
        service.RefreshAction = _ =>
        {
            service.Publish(new("off", "服务已停止", service.Settings, RuntimeEpoch: 1));
            return Task.FromResult(ready);
        };
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("off", model.Snapshot.Status);
        Assert.False(model.IsEngineReady);
        Assert.Equal(service.Snapshot.Revision, model.Snapshot.Revision);
    }

    [Fact]
    public async Task QueuedOldReadyAndOldOffCannotOverrideLaterStopOrRestart()
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Enabled = true });
        var context = new QueuedContext();
        var previous = SynchronizationContext.Current;
        OutboundDirectViewModel model;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            model = new(service, new OutboundUiTestDiagnostics());
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        using (model)
        {
            await Task.Run(() =>
            {
                service.Publish(OutboundUiTestService.Ready(service.Settings));
                service.Publish(new("off", "stopped", service.Settings, RuntimeEpoch: 1));
            });
            context.DrainReverse();
            Assert.Equal("off", model.Snapshot.Status);
            Assert.False(model.IsEngineReady);
            await Task.Run(() =>
            {
                service.Publish(new("off", "old stop", service.Settings, RuntimeEpoch: 1));
                service.Publish(OutboundUiTestService.Ready(service.Settings) with { RuntimeEpoch = 2 });
            });
            context.DrainReverse();
            Assert.Equal("ready", model.Snapshot.Status);
            Assert.Equal(2, model.Snapshot.RuntimeEpoch);
            Assert.True(model.IsEngineReady);
        }
    }

    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _actions = new();
        public override void Post(SendOrPostCallback callback, object? state) => _actions.Enqueue(() => callback(state));
        public void DrainReverse()
        {
            var actions = new List<Action>();
            while (_actions.TryDequeue(out var action)) actions.Add(action);
            actions.Reverse();
            foreach (var action in actions) action();
        }
    }

    private static IReadOnlyList<OutboundDiagnosticRow> CompletedRows() =>
    [
        new("dandan", "nipaplay.aimes-soft.com", false, "h2", false, 200, TimeSpan.FromMilliseconds(50), null, "普通 TLS"),
        new("tmdb", "api.tmdb.org", false, "h2", false, 401, TimeSpan.FromMilliseconds(60), "http", "公开探测未认证"),
        new("bahamut", "ani.gamer.com.tw", true, "h3", false, null, TimeSpan.FromMilliseconds(80), "ech", "原目标拒绝 ECH"),
    ];

    private static void AssertSavedSources(OutboundDirectViewModel model, OutboundSettings settings)
    {
        Assert.Equal(settings.Sources.Contains("bahamut"), model.SavedBahamutSelected);
        Assert.Equal(settings.Sources.Contains("tmdb"), model.SavedTmdbSelected);
        Assert.Equal(settings.Sources.Contains("dandan"), model.SavedDandanSelected);
        Assert.Equal(settings.Sources.Contains("animeko"), model.SavedAnimekoSelected);
    }

    private static void AssertAllSavedSourcesNotified(IReadOnlyList<string?> notifications)
    {
        Assert.Contains(nameof(OutboundDirectViewModel.SavedBahamutSelected), notifications);
        Assert.Contains(nameof(OutboundDirectViewModel.SavedTmdbSelected), notifications);
        Assert.Contains(nameof(OutboundDirectViewModel.SavedDandanSelected), notifications);
        Assert.Contains(nameof(OutboundDirectViewModel.SavedAnimekoSelected), notifications);
    }

    private sealed class OutboundTestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}

internal sealed class OutboundUiTestDiagnostics : IAppDiagnostics
{
    public string? LastDiagnostic { get; private set; }
    public List<string> Messages { get; } = [];
    public void Record(string message, Exception? error = null) { LastDiagnostic = message; Messages.Add(message); }
}

internal sealed class OutboundUiTestService : IOutboundDirectService
{
    private EventHandler<OutboundDirectSnapshot>? _changed;
    public OutboundUiTestService(OutboundSettings? settings = null)
    {
        Settings = settings ?? OutboundSettings.Default;
        Snapshot = new("off", "服务未运行", Settings);
    }
    public OutboundSettings Settings { get; set; }
    public OutboundDirectSnapshot Snapshot { get; private set; }
    public int Subscribers { get; private set; }
    public int RefreshCalls { get; private set; }
    public int DiagnoseCalls { get; private set; }
    public Exception? ReadError { get; set; }
    public Exception? SaveError { get; set; }
    public Exception? RefreshError { get; set; }
    public List<OutboundSettings> Saves { get; } = [];
    public IReadOnlyList<OutboundDiagnosticRow> Rows { get; set; } = [];
    public Func<OutboundSettings, CancellationToken, Task<OutboundOperationResult>>? SaveAction { get; set; }
    public Func<CancellationToken, Task<OutboundDirectSnapshot>>? RefreshAction { get; set; }
    public Func<CancellationToken, Task<OutboundDiagnosticRun>>? DiagnoseAction { get; set; }
    public event EventHandler<OutboundDirectSnapshot>? Changed
    {
        add { _changed += value; Subscribers++; }
        remove { _changed -= value; Subscribers--; }
    }
    public OutboundSettings ReadSettings() { if (ReadError is not null) throw ReadError; return Settings; }
    public Task<OutboundOperationResult> SaveAsync(OutboundSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SaveError is not null) throw SaveError;
        OutboundSettings.Validate(settings);
        Saves.Add(settings);
        if (SaveAction is not null) return SaveAction(settings, cancellationToken);
        Settings = settings;
        Publish(new("off", "已保存，服务未运行", settings));
        return Task.FromResult(new OutboundOperationResult(true, "配置已保存；不会自动启动或停止核心。"));
    }
    public Task<OutboundDirectSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RefreshCalls++;
        if (RefreshError is not null) throw RefreshError;
        return RefreshAction?.Invoke(cancellationToken) ?? Task.FromResult(Snapshot);
    }
    public Task<OutboundDiagnosticRun> DiagnoseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DiagnoseCalls++;
        return DiagnoseAction?.Invoke(cancellationToken) ?? Task.FromResult(Run());
    }
    public TimeProvider Clock { get; set; } = TimeProvider.System;
    public OutboundDiagnosticRun Run() => Rows.Count == 0
        ? new(OutboundDiagnosticRunStatus.Failed, null, [], null, "测速未返回任何目标域名结果，拒绝报告成功。")
        : new(OutboundDiagnosticRunStatus.Completed, Settings, Rows, Clock.GetUtcNow(), "测速完成。");
    public void Publish(OutboundDirectSnapshot snapshot)
    {
        snapshot = snapshot with { Revision = Snapshot.Revision + 1 };
        Snapshot = snapshot;
        _changed?.Invoke(this, snapshot);
    }
    public static OutboundDirectSnapshot Ready(OutboundSettings settings) => new("ready", "已通过真实 helper 会话验证", settings, 500, 501, "test-1", true, true, 1);
}
