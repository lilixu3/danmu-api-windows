using DanmuApi.App.Services;
using DanmuApi.Core.Frp;
using DanmuApi.Platform;
using DanmuApi.Runtime;
using DanmuApi.Runtime.Frp;

namespace DanmuApi.Tests;

/// <summary>Configuration-only service regressions, kept separate from shared lifecycle tests.</summary>
public sealed class FrpConfigurationPersistenceTests
{
    private const string RawClient = """
        {"serverAddr":"127.0.0.1","serverPort":7000,"user":"native-user","auth":{"method":"token","token":"raw-secret"},
        "webServer":{"port":17400},"proxies":[
          {"name":"one","type":"tcp","localIP":"127.0.0.1","localPort":9321,"remotePort":19321},
          {"name":"two","type":"udp","localIP":"127.0.0.1","localPort":9355,"remotePort":19355}]}
        """;

    [Fact]
    public async Task RawSaveRetainsVisualFieldsRoleAndTokenWithoutPlaintextOrRestart()
    {
        using var harness = FrpTestHarness.Create();
        var visual = FrpSettings.Default(9321) with
        {
            Role = FrpRole.Server,
            Client = FrpSettings.Default(9321).Client with { ServerAddress = "visual.example.invalid", User = "visual-user" },
            Server = new FrpServerSettings(7101, 8101, "visual.invalid", 17501),
        };
        harness.Store.Save(visual);
        harness.Store.SaveToken("visual-secret");
        var result = await harness.Service.SaveTextAsync(RawClient);
        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("尚未通过原生校验", result.Message, StringComparison.Ordinal);
        var saved = harness.Store.Read(9321);
        Assert.True(saved.Succeeded, string.Join("；", saved.Problems));
        Assert.Equal(FrpConfigMode.Text, saved.Settings.ConfigMode);
        Assert.Equal(RawClient, saved.Settings.RawConfig);
        Assert.Equal(FrpRole.Server, saved.Settings.Role); // Raw role is independent, not imported into the visual form.
        Assert.Equal(visual.Client, saved.Settings.Client);
        Assert.Equal(visual.Server, saved.Settings.Server);
        Assert.Equal("visual-secret", harness.Store.ReadToken());
        Assert.Equal(FrpRole.Client, harness.Service.EffectiveSettings.Role);
        Assert.Equal("native-user", harness.Service.EffectiveSettings.Client.User);
        Assert.Empty(harness.Service.EffectiveSettings.RawConfig);
        Assert.DoesNotContain("raw-secret", File.ReadAllText(harness.Paths.SettingsFile), StringComparison.Ordinal);
        Assert.DoesNotContain("raw-secret", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(harness.Paths.FrpConfigTextFile)), StringComparison.Ordinal);
        Assert.Equal(0, harness.Supervisor.StartCalls);
        Assert.Equal(0, harness.Verifier.Calls);
        Assert.Empty(Directory.GetFiles(harness.Paths.SettingsDirectory, "*.tmp-*"));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("restart")]
    [InlineData("follow")]
    [InlineData("automatic")]
    public async Task EveryStartEntryUsesTheSavedNativeDocumentRatherThanDormantVisualFields(string operation)
    {
        using var harness = FrpTestHarness.Create();
        await using var service = harness.Service;
        harness.Installer.Version = "0.71.0";
        harness.Store.Save(FrpSettings.Default(9321) with { Role = FrpRole.Server });
        harness.Store.SaveText(RawClient, 9321);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += (_, snapshot) =>
        {
            if (snapshot.State == FrpTunnelState.Running) running.TrySetResult();
        };
        if (operation == "automatic")
            Assert.True(harness.Store.SetFollowService(true, 9321).Succeeded);
        harness.Runtime.SetState(DesktopRuntimeState.Running);

        if (operation != "automatic")
        {
            var result = operation switch
            {
                "start" => await service.StartTunnelAsync(),
                "restart" => await service.RestartTunnelAsync(),
                _ => await service.SetFollowServiceAsync(true),
            };
            Assert.True(result.Succeeded, result.Message);
        }
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var plan = harness.Supervisor.LastPlan!;
        Assert.Equal(1, harness.Supervisor.StartCalls);
        Assert.Equal(1, harness.Verifier.Calls);
        Assert.Equal(FrpRole.Client, plan.Role);
        Assert.EndsWith("frpc.json", plan.ConfigPath, StringComparison.Ordinal);
        Assert.Equal(["native-user.one", "native-user.two"], plan.ExpectedProxies!.Select(proxy => proxy.Name));
        Assert.Equal(FrpConfigMode.Text, plan.CapturedSettings!.ConfigMode);
        Assert.Empty(plan.CapturedSettings.RawConfig);
        Assert.Equal(RawClient, harness.Store.Read(9321).Settings.RawConfig);
    }

    [Fact]
    public async Task FollowAndVisualSavePreserveDormantRawThenStartUsesOnlyActiveMode()
    {
        using var harness = FrpTestHarness.Create();
        Assert.True((await harness.Service.SaveTextAsync(RawClient)).Succeeded);
        Assert.True((await harness.Service.SetFollowServiceAsync(true)).Succeeded);
        Assert.Equal(FrpConfigMode.Text, harness.Service.Settings.ConfigMode);
        Assert.Equal(RawClient, harness.Service.Settings.RawConfig);
        // This case checks explicit Start, not a race with the separate follow lifecycle test.
        Assert.True((await harness.Service.SetFollowServiceAsync(false)).Succeeded);
        harness.Installer.Version = "0.71.0";
        harness.Runtime.SetState(DesktopRuntimeState.Running);
        Assert.True((await harness.Service.StartTunnelAsync()).Succeeded);
        var rawPlan = harness.Supervisor.LastPlan!;
        Assert.EndsWith("frpc.json", rawPlan.ConfigPath, StringComparison.Ordinal);
        Assert.Equal(["native-user.one", "native-user.two"], rawPlan.ExpectedProxies!.Select(proxy => proxy.Name));
        Assert.Equal(1, harness.Verifier.Calls);
        Assert.Equal(FrpConfigMode.Text, harness.Service.Snapshot.ActiveSettings!.ConfigMode);
        Assert.Empty(harness.Service.Snapshot.ActiveSettings.RawConfig);
        var visual = FrpSettings.Default(9321) with { Role = FrpRole.Server };
        Assert.True((await harness.Service.SaveAsync(visual, null, false)).Succeeded);
        Assert.Equal(FrpConfigMode.Visual, harness.Service.Settings.ConfigMode);
        Assert.Equal(RawClient, harness.Service.Settings.RawConfig);
        Assert.Equal(FrpRole.Client, harness.Service.EffectiveSettings.Role); // Running process keeps captured raw metadata.
        Assert.Equal(rawPlan.ExpectedProxies!.Select(proxy => proxy.AdminName), harness.Service.Snapshot.ExpectedProxies.Select(proxy => proxy.AdminName));
        Assert.All(harness.Service.Snapshot.ExpectedProxies, proxy => Assert.DoesNotContain("native-user", proxy.Name, StringComparison.Ordinal));
        Assert.Equal(1, harness.Supervisor.StartCalls);
        Assert.True((await harness.Service.StopTunnelAsync()).Succeeded);
        Assert.Equal(FrpRole.Server, harness.Service.EffectiveSettings.Role);
        Assert.True((await harness.Service.StartTunnelAsync()).Succeeded);
        Assert.EndsWith("frps.toml", harness.Supervisor.LastPlan!.ConfigPath, StringComparison.Ordinal);
        Assert.Equal(1, harness.Verifier.Calls); // Visual plan does not use the native raw verifier.
    }

    [Fact]
    public async Task NativeRejectRollsBackDocumentAndModeAndCannotSpawnOnStart()
    {
        using var harness = FrpTestHarness.Create();
        var original = FrpSettings.Default(9321) with { Role = FrpRole.Server };
        harness.Store.Save(original);
        harness.Installer.Version = "0.71.0";
        harness.Verifier.Result = new(false, "native rejected raw-secret");
        var save = await harness.Service.SaveTextAsync(RawClient);
        Assert.False(save.Succeeded);
        Assert.DoesNotContain("raw-secret", save.Message, StringComparison.Ordinal);
        Assert.Equal(FrpConfigMode.Visual, harness.Service.Settings.ConfigMode);
        Assert.False(File.Exists(harness.Paths.FrpConfigTextFile));
        Assert.Equal(FrpConfigMode.Visual, harness.Store.Read(9321).Settings.ConfigMode);
        Assert.Empty(Directory.GetFiles(harness.Paths.FrpConfigDirectory, "verify-*.json"));
        harness.Verifier.Result = new(true, "verified");
        Assert.True((await harness.Service.SaveTextAsync(RawClient)).Succeeded);
        harness.Verifier.Result = new(false, "native rejected raw-secret");
        harness.Runtime.SetState(DesktopRuntimeState.Running);
        var start = await harness.Service.StartTunnelAsync();
        Assert.False(start.Succeeded);
        Assert.DoesNotContain("raw-secret", start.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.Supervisor.StartCalls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("hash")]
    [InlineData("mode")]
    public async Task CorruptSavedRawBlocksAllSavesAndStartButNeverStop(string failure)
    {
        using var harness = FrpTestHarness.Create();
        harness.Installer.Version = "0.71.0";
        harness.Runtime.SetState(DesktopRuntimeState.Running);
        Assert.True((await harness.Service.SaveTextAsync(RawClient)).Succeeded);
        Assert.True((await harness.Service.StartTunnelAsync()).Succeeded);
        if (failure == "missing") File.Delete(harness.Paths.FrpConfigTextFile);
        if (failure == "corrupt") File.WriteAllBytes(harness.Paths.FrpConfigTextFile, [1, 2, 3]);
        if (failure == "hash") new WindowsProtectedDocumentStore(harness.Paths.FrpConfigTextFile).Save(RawClient + " ");
        if (failure == "mode") new SettingsStore(harness.Paths.SettingsFile).Write(new Dictionary<string, string?> { [FrpSettingsStore.ConfigModeKey] = "invalid" });
        Assert.False((await harness.Service.ReloadSettingsAsync()).Succeeded);
        Assert.NotEmpty(harness.Service.SettingsProblems);
        Assert.Equal(FrpConfigMode.Text, harness.Service.EffectiveSettings.ConfigMode); // Live captured metadata is still truthful.
        Assert.False((await harness.Service.SaveTextAsync(RawClient)).Succeeded);
        Assert.False((await harness.Service.SaveAsync(FrpSettings.Default(9321) with { Role = FrpRole.Server }, null, false)).Succeeded);
        Assert.False((await harness.Service.SetFollowServiceAsync(true)).Succeeded);
        Assert.True((await harness.Service.StopTunnelAsync()).Succeeded);
        Assert.False((await harness.Service.StartTunnelAsync()).Succeeded);
        Assert.Equal(1, harness.Supervisor.StartCalls);
    }

    [Fact]
    public async Task ValidateDoesNotPersistAndNativeVerificationArtifactsAreDeleted()
    {
        using var harness = FrpTestHarness.Create();
        harness.Installer.Version = "0.71.0";
        var result = await harness.Service.ValidateTextAsync(RawClient);
        Assert.True(result.Succeeded, result.Message);
        Assert.False(File.Exists(harness.Paths.SettingsFile));
        Assert.False(File.Exists(harness.Paths.FrpConfigTextFile));
        Assert.Empty(Directory.GetFiles(harness.Paths.FrpConfigDirectory, "verify-*.json"));
        Assert.Equal(1, harness.Verifier.Calls);
        Assert.Equal(0, harness.Supervisor.StartCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FollowShortcutRereadsDiskAndPatchesOnlyItsKey(bool loadStaleSettingsFirst)
    {
        using var harness = FrpTestHarness.Create();
        if (loadStaleSettingsFirst) await harness.Service.ReloadSettingsAsync();
        var store = new SettingsStore(harness.Paths.SettingsFile);
        store.Write(new Dictionary<string, string?>
        {
            [FrpSettingsStore.RoleKey] = "server",
            [FrpSettingsStore.ServerAddressKey] = "external.example.com",
            [FrpSettingsStore.VhostHttpPortKey] = "08080",
            [FrpSettingsStore.SubdomainHostKey] = "external.example.com",
            [FrpSettingsStore.UseCompressionKey] = "yes",
            ["unmanaged_key"] = "preserve",
        });
        var before = store.Read();
        var result = await harness.Service.SetFollowServiceAsync(true);
        Assert.True(result.Succeeded, result.Message);
        var after = store.Read();
        Assert.Equal(before.Count + 1, after.Count);
        foreach (var pair in before) Assert.Equal(pair.Value, after[pair.Key]);
        Assert.Equal("true", after[FrpSettingsStore.FollowServiceKey]);
        Assert.Equal(FrpRole.Server, harness.Service.Settings.Role);
        Assert.Equal("external.example.com", harness.Service.Settings.Client.ServerAddress);
        Assert.Equal(8080, harness.Service.Settings.Server.VhostHttpPort);
        Assert.Equal(0, harness.Supervisor.StartCalls);
    }

    [Fact]
    public async Task MalformedDiskBlocksFollowAndSaveRatherThanWritingFallbackValues()
    {
        using var harness = FrpTestHarness.Create();
        var store = new SettingsStore(harness.Paths.SettingsFile);
        store.Write(new Dictionary<string, string?> { [FrpSettingsStore.FollowServiceKey] = "broken" });
        var before = File.ReadAllText(harness.Paths.SettingsFile);
        var follow = await harness.Service.SetFollowServiceAsync(true);
        var settings = FrpSettings.Default(9321) with { Role = FrpRole.Server };
        var save = await harness.Service.SaveAsync(settings, null, false);
        Assert.False(follow.Succeeded);
        Assert.False(save.Succeeded);
        Assert.Contains(FrpSettingsStore.FollowServiceKey, follow.Message, StringComparison.Ordinal);
        Assert.Contains(FrpSettingsStore.FollowServiceKey, save.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(harness.Paths.SettingsFile));
        Assert.Equal(0, harness.Supervisor.StartCalls);
    }

    [Theory]
    [InlineData("new-token", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public async Task SaveRejectsAmbiguousOrEmptyTokenIntentBeforeWriting(string token, bool clear)
    {
        using var harness = FrpTestHarness.Create();
        var result = await harness.Service.SaveAsync(FrpSettings.Default(9321) with { Role = FrpRole.Server }, token, clear);
        Assert.False(result.Succeeded);
        Assert.Contains("Token", result.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(harness.Paths.SettingsFile));
        Assert.False(harness.Store.HasToken());
    }
}
