using System.Net;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.Core;
using DanmuApi.Core.ApplicationUpdates;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

/// <summary>
/// 进前台静默检查软件更新的三条约束：
/// ① 自动检查（前台、定时器）共用一份 30 分钟冷却，命中就绝不发网络请求；
/// ② 用户手动「检查更新」不受冷却限制；
/// ③ 发现新版本要能跨重启复用 —— 重启后既不重新联网，卡片也还在，且已跳过的版本不会复发。
/// </summary>
public sealed class ApplicationUpdateForegroundTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-21T08:00:00Z");

    [Theory]
    [InlineData(0, false)]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(31, true)]
    [InlineData(-5, true)]
    public void CadenceOnlyOpensTheGateAfterTheCooldownElapsed(int minutesSinceLastCheck, bool expectedDue)
    {
        var clock = new MutableTimeProvider(Now);
        var last = minutesSinceLastCheck == 0 ? Now : Now.AddMinutes(-minutesSinceLastCheck);

        // 时钟被调回过去（last 在未来）判到期：宁可多查一次，也不要从此不再检查。
        Assert.Equal(expectedDue, UpdateCheckCadence.IsDue(clock, last, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void NullLastCheckIsAlwaysDueAndBadCooldownIsRejected()
    {
        Assert.True(UpdateCheckCadence.IsDue(TimeProvider.System, null, TimeSpan.FromMinutes(30)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            UpdateCheckCadence.IsDue(TimeProvider.System, Now, TimeSpan.Zero));
    }

    [Fact]
    public async Task DiscoveryRestoredFromSettingsNeedsNoNetworkAndCooldownBlocksForegroundCheck()
    {
        using var directory = new TemporaryDirectory();
        var settings = new SettingsStore(Path.Combine(directory.Path, "settings.properties"));
        WriteDiscovery(settings, "9.9.9-preview.1", Now);

        var model = Create(settings);
        Assert.True(model.HasUpdate, model.Status);
        Assert.Equal("9.9.9-preview.1", model.AvailableVersion);
        Assert.Contains("分钟前检查过", model.Status, StringComparison.Ordinal);

        // 冷却期内进前台：不发请求（也就不会改写检查时间），卡片与文案原样保留。
        await model.CheckOnForegroundAsync();
        Assert.True(model.HasUpdate, model.Status);
        Assert.Equal(
            Now.ToUnixTimeMilliseconds(),
            long.Parse(settings.Read()["app_update_last_check_ms"], CultureInfo.InvariantCulture));
        Assert.DoesNotContain("正在检查", model.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpiredDiscoveryAndSkippedVersionDoNotReappear()
    {
        using var old = new TemporaryDirectory();
        var oldSettings = new SettingsStore(Path.Combine(old.Path, "settings.properties"));
        WriteDiscovery(oldSettings, "9.9.9-preview.1", Now.AddDays(-2));
        Assert.False(Create(oldSettings).HasUpdate);

        using var skipped = new TemporaryDirectory();
        var skippedSettings = new SettingsStore(Path.Combine(skipped.Path, "settings.properties"));
        WriteDiscovery(skippedSettings, "9.9.9-preview.1", Now);
        skippedSettings.Write(new Dictionary<string, string?> { ["app_update_skipped"] = "9.9.9-preview.1" });
        Assert.False(Create(skippedSettings).HasUpdate);
    }

    [Fact]
    public void GarbageDiscoveryTimestampDegradesToNoUpdateInsteadOfBreakingStartup()
    {
        using var directory = new TemporaryDirectory();
        var settings = new SettingsStore(Path.Combine(directory.Path, "settings.properties"));
        settings.Write(new Dictionary<string, string?>
        {
            ["app_update_found_version"] = "9.9.9-preview.1",
            ["app_update_found_at_ms"] = "not-a-number",
        });
        var diagnostics = new Diagnostics();

        var model = Create(settings, diagnostics);

        Assert.False(model.HasUpdate);
        Assert.Contains("软件更新缓存时间无效", diagnostics.LastDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualCheckBypassesCooldownAndForegroundWaitsForTheCooldownToElapse()
    {
        using var directory = new TemporaryDirectory();
        var settings = new SettingsStore(Path.Combine(directory.Path, "settings.properties"));
        var clock = new MutableTimeProvider(Now);
        using var feed = new StubFeed();
        using var remote = new ApplicationUpdateService(feed.PublicKey, feed.Transport);
        var model = Create(settings, remote: remote, clock: clock);

        // 手动检查不受冷却约束：直接把请求发出去，并把结果与检查时间落盘。
        await model.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.True(model.HasUpdate, model.Status);
        Assert.Equal(1, feed.ListCalls);
        var saved = settings.Read();
        Assert.Equal("2.0.0-preview.9", saved["app_update_found_version"]);
        Assert.Equal(
            Now.ToUnixTimeMilliseconds(),
            long.Parse(saved["app_update_last_check_ms"], CultureInfo.InvariantCulture));

        // 刚查过就进前台：绝不重复请求。
        await model.CheckOnForegroundAsync();
        Assert.Equal(1, feed.ListCalls);

        // 冷却过后（31 分钟）进前台：静默重查一次。
        clock.Advance(TimeSpan.FromMinutes(31));
        await model.CheckOnForegroundAsync();
        Assert.Equal(2, feed.ListCalls);
    }

    [Fact]
    public async Task ForegroundCheckIsSilentWhenAutomaticChecksAreOff()
    {
        using var directory = new TemporaryDirectory();
        var settings = new SettingsStore(Path.Combine(directory.Path, "settings.properties"));
        using var feed = new StubFeed();
        using var remote = new ApplicationUpdateService(feed.PublicKey, feed.Transport);
        var model = Create(settings, remote: remote);
        model.AutomaticChecks = false;

        await model.CheckOnForegroundAsync();

        Assert.Equal(0, feed.ListCalls);
        Assert.False(model.HasUpdate);
    }

    private static ApplicationUpdateViewModel Create(
        ISettingsStore settings,
        IAppDiagnostics? diagnostics = null,
        ApplicationUpdateService? remote = null,
        TimeProvider? clock = null) =>
        new(settings, new RecordingDialogService(), new Notifications(), diagnostics ?? new Diagnostics(), remote, clock ?? new MutableTimeProvider(Now));

    private static void WriteDiscovery(ISettingsStore settings, string version, DateTimeOffset checkedAt)
    {
        settings.Write(new Dictionary<string, string?>
        {
            ["app_update_found_version"] = version,
            ["app_update_found_at_ms"] = checkedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            ["app_update_last_check_ms"] = checkedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        });
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset current = utcNow;

        public void Advance(TimeSpan by) => current += by;

        public override DateTimeOffset GetUtcNow() => current;
    }

    /// <summary>自带 RSA 密钥的假 GitHub 发行源：清单与签名自洽，服务用的是它的公钥而非内置那份。</summary>
    private sealed class StubFeed : IDisposable
    {
        private readonly RSA rsa = RSA.Create(2048);
        private readonly byte[] package = Encoding.UTF8.GetBytes("payload");

        public HttpMessageHandler Transport { get; }
        public byte[] PublicKey => rsa.ExportSubjectPublicKeyInfo();
        public int ListCalls { get; private set; }

        public StubFeed() => Transport = new Handler(this);

        public void Dispose()
        {
            Transport.Dispose();
            rsa.Dispose();
        }

        private sealed class Handler(StubFeed owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                if (uri.Host == "api.github.com")
                {
                    owner.ListCalls++;
                    return Task.FromResult(Json(new object[] { owner.Release() }));
                }

                var name = uri.Segments.Last();
                if (name == ApplicationUpdateService.ManifestFileName) return Task.FromResult(Bytes(owner.Manifest()));
                if (name == ApplicationUpdateService.SignatureFileName) return Task.FromResult(Bytes(owner.Signature()));
                return Task.FromResult(Bytes(owner.package));
            }

            private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body)),
            };

            private static HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };
        }

        private object Release() => new
        {
            tag_name = "v2.0.0-preview.9",
            draft = false,
            prerelease = true,
            body = "发行说明",
            published_at = "2026-09-20T00:00:00Z",
            assets = new[] { ApplicationUpdateService.ManifestFileName, ApplicationUpdateService.SignatureFileName, "package.zip" }
                .Select(name => new
                {
                    name,
                    browser_download_url = "https://github.com/lilixu3/danmu-api-windows/releases/download/v2.0.0-preview.9/" + name,
                }),
        };

        private byte[] Manifest() => JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            product = "DanmuApi.Windows",
            version = "2.0.0-preview.9",
            channel = "preview",
            architecture = ApplicationArchitecture.Current,
            assets = new object[]
            {
                new
                {
                    name = "package.zip",
                    size = package.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(),
                    kind = ApplicationUpdateHelper.IsInstalled(Environment.ProcessPath!) ? "installer" : "portable",
                },
            },
        });

        private byte[] Signature() => rsa.SignData(Manifest(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    private sealed class Notifications : IDesktopNotificationService
    {
        public Task<DesktopNotificationResult> ShowAsync(string title, string message, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesktopNotificationResult(true, "test"));
    }

    private sealed class Diagnostics : IAppDiagnostics
    {
        public string? LastDiagnostic { get; private set; }
        public void Record(string message, Exception? error = null) => LastDiagnostic = message;
    }
}
