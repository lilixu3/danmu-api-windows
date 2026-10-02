using DanmuApi.App.Services;
using DanmuApi.Core.Frp;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

/// <summary>Configuration-only service regressions, kept separate from shared lifecycle tests.</summary>
public sealed class FrpConfigurationPersistenceTests
{
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
