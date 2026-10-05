using DanmuApi.App.Services;
using DanmuApi.Core.Frp;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class FrpSettingsStoreTests
{
    private sealed record Fixture(
        FrpSettingsStore Store,
        ISettingsStore Settings,
        string Directory) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static Fixture Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), "danmu-api-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var settings = new SettingsStore(Path.Combine(directory, "settings.properties"));
        return new Fixture(
            new FrpSettingsStore(
                settings,
                new WindowsProtectedStringStore(Path.Combine(directory, "frp-token.dat"), "DanmuApi.Windows.FrpToken.v1"),
                new WindowsProtectedStringStore(Path.Combine(directory, "frp-admin.dat"), "DanmuApi.Windows.FrpAdminPassword.v1"),
                new WindowsProtectedDocumentStore(Path.Combine(directory, "frp-config-text.dat"))),
            settings,
            directory);
    }

    private const string RawServer = " \r\n{\"bindPort\":7100}\r\n ";

    [Fact]
    public void TextTransactionPreservesEveryVisualKeyAndFollowKeepsDocumentAndMode()
    {
        using var fixture = Create();
        fixture.Store.Save(FrpSettings.Default(9321) with { Role = FrpRole.Server });
        var before = fixture.Settings.Read();
        var saved = fixture.Store.SaveText(RawServer, 9321);
        Assert.True(saved.Succeeded, string.Join("；", saved.Problems));
        Assert.Equal(FrpConfigMode.Text, saved.Settings.ConfigMode);
        Assert.Equal(RawServer, saved.Settings.RawConfig);
        var after = fixture.Settings.Read();
        foreach (var pair in before.Where(pair => pair.Key != FrpSettingsStore.ConfigModeKey)) Assert.Equal(pair.Value, after[pair.Key]);
        var followed = fixture.Store.SetFollowService(true, 9321);
        Assert.True(followed.Succeeded);
        Assert.Equal(FrpConfigMode.Text, followed.Settings.ConfigMode);
        Assert.Equal(RawServer, followed.Settings.RawConfig);
        fixture.Store.Save(followed.Settings with { ConfigMode = FrpConfigMode.Visual });
        Assert.Equal(RawServer, fixture.Store.Read(9321).Settings.RawConfig);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedModeMetadataWriteRestoresOldDocumentEvenInSameTextMode(bool sameMode)
    {
        using var directory = new TemporaryDirectory();
        var values = new FailMetadataStore(new SettingsStore(Path.Combine(directory.Path, "settings.properties")));
        var store = new FrpSettingsStore(values, new MemorySecretStore(), new MemorySecretStore(),
            new WindowsProtectedDocumentStore(Path.Combine(directory.Path, "frp-config-text.dat")));
        store.Save(FrpSettings.Default(9321) with { Role = FrpRole.Server });
        if (sameMode) store.SaveText(RawServer, 9321);
        var before = store.Read(9321).Settings;
        var beforeValues = values.Read();
        values.FailWrites = 1;
        var error = Assert.Throws<IOException>(() => store.SaveText("{\"bindPort\":7200}", 9321));
        Assert.Contains("injected metadata failure", error.Message, StringComparison.Ordinal);
        var after = store.Read(9321);
        Assert.True(after.Succeeded, string.Join("；", after.Problems));
        Assert.Equal(before.ConfigMode, after.Settings.ConfigMode);
        Assert.Equal(before.RawConfig, after.Settings.RawConfig);
        Assert.Equal(beforeValues.OrderBy(pair => pair.Key), values.Read().OrderBy(pair => pair.Key));
    }

    [Fact]
    public void RollbackFailureIsNotSwallowedAndIncompleteTransactionBlocksReads()
    {
        using var directory = new TemporaryDirectory();
        var values = new FailMetadataStore(new SettingsStore(Path.Combine(directory.Path, "settings.properties")));
        var store = new FrpSettingsStore(values, new MemorySecretStore(), new MemorySecretStore(),
            new WindowsProtectedDocumentStore(Path.Combine(directory.Path, "frp-config-text.dat")));
        values.FailWrites = 2;
        var error = Assert.Throws<IOException>(() => store.SaveText(RawServer, 9321));
        Assert.Contains("回滚也失败", error.Message, StringComparison.Ordinal);
        // Both failed writes below deliberately occur AFTER atomic properties replacement. This restores mode but
        // still reports the rollback write failure instead of silently claiming the operation completed.
        Assert.IsType<AggregateException>(error.InnerException);
    }

    [Fact]
    public void VisualOnlyLegacyConstructionCannotActivateTextWithoutProtectedStore()
    {
        var values = new FailMetadataStore(new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".properties")));
        var store = new FrpSettingsStore(values, new MemorySecretStore(), new MemorySecretStore());
        Assert.Equal(FrpConfigMode.Visual, store.Read(9321).Settings.ConfigMode);
        Assert.Throws<IOException>(() => store.SaveText(RawServer, 9321));
        Assert.Empty(values.Read());
    }

    private sealed class MemorySecretStore : IProtectedStringStore
    {
        private string? _value;
        public string? Load() => _value;
        public void Save(string value) => _value = value;
        public void Clear() => _value = null;
    }

    private sealed class FailMetadataStore(ISettingsStore inner) : ISettingsStore
    {
        public int FailWrites { get; set; }
        public IReadOnlyDictionary<string, string> Read() => inner.Read();
        public void Write(IReadOnlyDictionary<string, string?> changes)
        {
            inner.Write(changes);
            if (FailWrites > 0 && changes.ContainsKey(FrpSettingsStore.ConfigTextHashKey))
            {
                FailWrites--;
                throw new IOException("injected metadata failure after replacement");
            }
        }
    }

    [Fact]
    public void EmptySettingsProduceUsableDefaults()
    {
        using var fixture = Create();

        var result = fixture.Store.Read(defaultLocalPort: 9321);

        Assert.True(result.Succeeded);
        Assert.Equal(FrpRole.Client, result.Settings.Role);
        Assert.False(result.Settings.FollowService);
        Assert.Equal(string.Empty, result.Settings.InstalledVersion);
        Assert.Equal(9321, result.Settings.Client.LocalPort);
        Assert.Equal(9321, result.Settings.Client.RemotePort);
        Assert.Equal(FrpClientSettings.DefaultServerPort, result.Settings.Client.ServerPort);
        Assert.Equal(FrpClientSettings.DefaultAdminPort, result.Settings.Client.AdminPort);
        Assert.Empty(result.Settings.Client.CustomDomains);
    }

    [Fact]
    public void RoundTripsEveryPersistedField()
    {
        using var fixture = Create();
        var settings = FrpSettings.Default(9321) with
        {
            Role = FrpRole.Server,
            FollowService = true,
            InstalledVersion = "0.71.0",
            Client = FrpSettings.Default(9321).Client with
            {
                ServerAddress = "frp.example.com",
                ServerPort = 7100,
                User = "panel-user",
                ProxyName = "my-danmu",
                ProxyKind = FrpProxyKind.Http,
                LocalAddress = "127.0.0.1",
                LocalPort = 9322,
                RemotePort = 19322,
                CustomDomains = ["a.example.com", "b.example.com"],
                UseEncryption = true,
                UseCompression = true,
                TransportTls = false,
                AdminPort = 7401,
            },
            Server = new FrpServerSettings(BindPort: 7200, VhostHttpPort: 8080, SubdomainHost: "example.com", AdminPort: 7501),
        };

        fixture.Store.Save(settings);
        var read = fixture.Store.Read(defaultLocalPort: 9321);

        Assert.True(read.Succeeded);
        // 逐字段比对：CustomDomains 是集合，记录相等性按引用比较，用 Assert.Equal(settings, read)
        // 会在集合上误报（两边内容一样但实例不同）。
        Assert.Equal(settings.Role, read.Settings.Role);
        Assert.Equal(settings.FollowService, read.Settings.FollowService);
        Assert.Equal(settings.InstalledVersion, read.Settings.InstalledVersion);
        Assert.Equal(settings.Client.ServerAddress, read.Settings.Client.ServerAddress);
        Assert.Equal(settings.Client.ServerPort, read.Settings.Client.ServerPort);
        Assert.Equal(settings.Client.User, read.Settings.Client.User);
        Assert.Equal(settings.Client.ProxyName, read.Settings.Client.ProxyName);
        Assert.Equal(settings.Client.ProxyKind, read.Settings.Client.ProxyKind);
        Assert.Equal(settings.Client.LocalAddress, read.Settings.Client.LocalAddress);
        Assert.Equal(settings.Client.LocalPort, read.Settings.Client.LocalPort);
        Assert.Equal(settings.Client.RemotePort, read.Settings.Client.RemotePort);
        Assert.Equal(settings.Client.CustomDomains, read.Settings.Client.CustomDomains);
        Assert.Equal(settings.Client.UseEncryption, read.Settings.Client.UseEncryption);
        Assert.Equal(settings.Client.UseCompression, read.Settings.Client.UseCompression);
        Assert.Equal(settings.Client.TransportTls, read.Settings.Client.TransportTls);
        Assert.Equal(settings.Client.AdminPort, read.Settings.Client.AdminPort);
        Assert.Equal(settings.Server.BindPort, read.Settings.Server.BindPort);
        Assert.Equal(settings.Server.VhostHttpPort, read.Settings.Server.VhostHttpPort);
        Assert.Equal(settings.Server.SubdomainHost, read.Settings.Server.SubdomainHost);
        Assert.Equal(settings.Server.AdminPort, read.Settings.Server.AdminPort);
    }

    [Fact]
    public void TokenUsesTheProtectedStoreAndIsNotWrittenToSettingsText()
    {
        using var fixture = Create();
        fixture.Store.Save(FrpSettings.Default(9321) with { InstalledVersion = "0.71.0" });

        Assert.False(fixture.Store.HasToken());
        fixture.Store.SaveToken("super-secret");
        Assert.True(fixture.Store.HasToken());
        Assert.Equal("super-secret", fixture.Store.ReadToken());

        var text = File.ReadAllText(Path.Combine(fixture.Directory, "settings.properties"));
        Assert.DoesNotContain("super-secret", text, StringComparison.Ordinal);

        fixture.Store.ClearToken();
        Assert.False(fixture.Store.HasToken());
    }

    [Fact]
    public void AdminPasswordIsGeneratedOnceAndReused()
    {
        using var fixture = Create();

        var first = fixture.Store.EnsureAdminPassword();
        var second = fixture.Store.EnsureAdminPassword();

        Assert.Equal(48, first.Length);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("frp_server_port", "abc")]
    [InlineData("frp_local_port", "-1")]
    [InlineData("frp_role", "sideways")]
    [InlineData("frp_proxy_kind", "socks5")]
    [InlineData("frp_follow_service", "maybe")]
    public void MalformedValuesAreReportedInsteadOfSilentlyDefaulted(string key, string value)
    {
        using var fixture = Create();
        fixture.Settings.Write(new Dictionary<string, string?> { [key] = value });

        var result = fixture.Store.Read(defaultLocalPort: 9321);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Problems, problem => problem.Contains(key, StringComparison.Ordinal));
    }

    [Fact]
    public void DomainListAcceptsCommasSemicolonsAndWhitespace()
    {
        var domains = FrpSettingsStore.ParseDomains("a.example.com, b.example.com;c.example.com\nA.example.com  d.example.com");

        Assert.Equal(["a.example.com", "b.example.com", "c.example.com", "d.example.com"], domains);
    }

    [Fact]
    public void FollowOnlyPatchPreservesAllOtherRawValuesAndDoesNotMaterializeDefaults()
    {
        using var fixture = Create();
        fixture.Settings.Write(new Dictionary<string, string?>
        {
            [FrpSettingsStore.RoleKey] = "server",
            [FrpSettingsStore.ServerAddressKey] = "external.example.com",
            [FrpSettingsStore.VhostHttpPortKey] = "08080",
            [FrpSettingsStore.SubdomainHostKey] = "old.example.com",
            [FrpSettingsStore.UseCompressionKey] = "yes",
            ["unmanaged_key"] = "do-not-change",
        });
        var before = fixture.Settings.Read();
        var result = fixture.Store.SetFollowService(true, 9321);
        Assert.True(result.Succeeded, string.Join("；", result.Problems));
        Assert.True(result.Settings.FollowService);
        Assert.Equal(FrpRole.Server, result.Settings.Role);
        var after = fixture.Settings.Read();
        Assert.Equal(before.Count + 1, after.Count);
        foreach (var pair in before) Assert.Equal(pair.Value, after[pair.Key]);
        Assert.Equal("true", after[FrpSettingsStore.FollowServiceKey]);
    }

    [Theory]
    [InlineData(FrpSettingsStore.ServerPortKey, "bad-int")]
    [InlineData(FrpSettingsStore.RoleKey, "sideways")]
    [InlineData(FrpSettingsStore.FollowServiceKey, "maybe")]
    public void FollowPatchRejectsMalformedRereadWithoutAnyWrite(string key, string value)
    {
        using var fixture = Create();
        fixture.Settings.Write(new Dictionary<string, string?> { [key] = value });
        var before = File.ReadAllText(Path.Combine(fixture.Directory, "settings.properties"));
        var result = fixture.Store.SetFollowService(true, 9321);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Problems, problem => problem.Contains(key, StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllText(Path.Combine(fixture.Directory, "settings.properties")));
    }

    [Fact]
    public void ZeroIsAcceptedForTheOptionalServerVhostPort()
    {
        using var fixture = Create();
        fixture.Settings.Write(new Dictionary<string, string?> { ["frp_vhost_http_port"] = "0" });

        var result = fixture.Store.Read(defaultLocalPort: 9321);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Settings.Server.VhostHttpPort);
    }
}
