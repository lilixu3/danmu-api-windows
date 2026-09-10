using System.Net;
using System.Text;
using System.Text.Json;
using DanmuApi.Core;
using DanmuApi.Platform;
using DanmuApi.Runtime;
using Xunit;

namespace DanmuApi.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "backup-tests-" + Guid.NewGuid().ToString("N"));
    public BackupTests() => Directory.CreateDirectory(Path.Combine(directory, "config"));
    public void Dispose() => Directory.Delete(directory, true);
    private string Env => Path.Combine(directory, "config", ".env");
    private static byte[] Bundle(string values = "\"PUBLIC_SETTING\":\"new\"", int schema = 2) => Encoding.UTF8.GetBytes(
        $$"""{"format":"danmu-api-app-backup","schemaVersion":{{schema}},"createdAtMs":1,"appVersion":"android","sections":["Environment","AppSettings"],"environment":{ {{values}} },"appPreferences":[]}""");

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public void MobileSchemaFiltersSecretsAndMachineIdentity(int schema)
    {
        var doc = BackupBundle.Decode(Bundle("\"PUBLIC_SETTING\":\"new\",\"TOKEN\":\"secret\",\"DANMU_API_RUNTIME_IDENTITY\":\"other\",\"PROXY_URL\":\"secret-url\"", schema));
        Assert.Single(doc.Environment);
        Assert.Equal(3, doc.ExcludedKeys);
        Assert.Equal(new[] { "AppSettings" }, doc.OmittedSections);
    }
    [Theory]
    [InlineData(0)] [InlineData(3)]
    public void UnknownSchemaFails(int schema) => Assert.Throws<InvalidDataException>(() => BackupBundle.Decode(Bundle(schema: schema)));
    [Fact] public void DuplicateJsonFails() => Assert.Throws<InvalidDataException>(() => BackupBundle.Decode(Bundle("\"A\":\"1\",\"A\":\"2\"")));
    [Fact] public async Task BoundedReadFails() => await Assert.ThrowsAsync<InvalidDataException>(() => BackupBundle.ReadBoundedAsync(new MemoryStream(new byte[BackupBundle.MaximumBytes + 1])));

    [Fact] public async Task RestoreRequiresConfirmationAndStoppedLeaseThenPreservesSecrets()
    {
        File.WriteAllText(Env, "PUBLIC_SETTING=old\nTOKEN=local-secret\nDANMU_API_RUNTIME_IDENTITY=local-id\n");
        var guard = new Guard();
        var service = new BackupLocalService(Env, "test", guard);
        var preview = await service.PreviewAsync(Bundle());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(preview.Id, false));
        guard.Running = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(preview.Id, true));
        Assert.Contains("PUBLIC_SETTING=old", File.ReadAllText(Env));
        guard.Running = false;
        await service.RestoreAsync(preview.Id, true);
        Assert.Equal("new", DotEnvFile.ReadValue(Env, "PUBLIC_SETTING"));
        Assert.Equal("local-secret", DotEnvFile.ReadValue(Env, "TOKEN"));
        Assert.Equal("local-id", DotEnvFile.ReadValue(Env, "DANMU_API_RUNTIME_IDENTITY"));
        Assert.True(guard.Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(preview.Id, true));
    }
    [Fact] public async Task ChangedTargetFailsWithoutOverwriting()
    {
        File.WriteAllText(Env, "PUBLIC_SETTING=old\n");
        var service = new BackupLocalService(Env, "test", new Guard());
        var preview = await service.PreviewAsync(Bundle());
        File.WriteAllText(Env, "PUBLIC_SETTING=external\n");
        await Assert.ThrowsAsync<DotEnvConflictException>(() => service.RestoreAsync(preview.Id, true));
        Assert.Equal("external", DotEnvFile.ReadValue(Env, "PUBLIC_SETTING"));
    }
    [Fact] public async Task BackupAndRestoreIncludeFavoritesAndWhitelistedDesktopPreferencesOnly()
    {
        File.WriteAllText(Env, "PUBLIC_SETTING=old\nTOKEN=local-secret\n");
        var favorites = Path.Combine(directory, ".cache", "favoritesCache");
        Directory.CreateDirectory(Path.GetDirectoryName(favorites)!);
        File.WriteAllText(favorites, "{\"old\":{\"keyword\":\"old\"}}");
        var settings = Path.Combine(directory, "settings.properties");
        new SettingsStore(settings).Write(new Dictionary<string, string?>
        {
            ["theme"] = "dark",
            ["notification_level"] = "updates",
            ["close_action"] = "tray",
            ["runtime_root"] = "private-machine-path",
        });
        var service = new BackupLocalService(Env, "test", new Guard(), settings);

        var encoded = service.Create();
        var document = BackupBundle.Decode(encoded);

        Assert.NotNull(document.Favorites);
        Assert.Equal("dark", document.DesktopPreferences!["theme"]);
        Assert.DoesNotContain("runtime_root", document.DesktopPreferences.Keys);
        Assert.Contains("Favorites", JsonDocument.Parse(encoded).RootElement.GetProperty("sections").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("AppSettings", JsonDocument.Parse(encoded).RootElement.GetProperty("sections").EnumerateArray().Select(x => x.GetString()));

        var imported = BackupBundle.Encode(
            new Dictionary<string, string> { ["PUBLIC_SETTING"] = "new" }, "android",
            "{\"new\":{\"keyword\":\"new\"}}",
            new Dictionary<string, string> { ["theme"] = "light", ["notification_level"] = "all", ["close_action"] = "exit" });
        var preview = await service.PreviewAsync(imported);
        Assert.Equal(1, preview.FavoriteCount);
        Assert.Equal(["close_action", "notification_level", "theme"], preview.DesktopPreferenceKeys);
        await service.RestoreAsync(preview.Id, true);

        Assert.Equal("new", DotEnvFile.ReadValue(Env, "PUBLIC_SETTING"));
        Assert.Equal("local-secret", DotEnvFile.ReadValue(Env, "TOKEN"));
        Assert.Contains("\"new\"", File.ReadAllText(favorites));
        var restoredSettings = new SettingsStore(settings).Read();
        Assert.Equal("light", restoredSettings["theme"]);
        Assert.Equal("all", restoredSettings["notification_level"]);
        Assert.Equal("exit", restoredSettings["close_action"]);
        Assert.Equal("private-machine-path", restoredSettings["runtime_root"]);
    }
    [Fact] public async Task FavoritesOrPreferencesChangedAfterPreviewInvalidateRestore()
    {
        File.WriteAllText(Env, "PUBLIC_SETTING=old\n");
        var favorites = Path.Combine(directory, ".cache", "favoritesCache");
        Directory.CreateDirectory(Path.GetDirectoryName(favorites)!);
        File.WriteAllText(favorites, "{}");
        var settings = Path.Combine(directory, "settings.properties");
        new SettingsStore(settings).Write(new Dictionary<string, string?> { ["theme"] = "dark" });
        var service = new BackupLocalService(Env, "test", new Guard(), settings);
        var bytes = BackupBundle.Encode(new Dictionary<string, string> { ["PUBLIC_SETTING"] = "new" }, "android", "{}",
            new Dictionary<string, string> { ["theme"] = "light" });

        var favoritePreview = await service.PreviewAsync(bytes);
        File.WriteAllText(favorites, "{\"external\":{}}");
        await Assert.ThrowsAsync<DotEnvConflictException>(() => service.RestoreAsync(favoritePreview.Id, true));
        Assert.Equal("old", DotEnvFile.ReadValue(Env, "PUBLIC_SETTING"));

        File.WriteAllText(favorites, "{}");
        var settingsPreview = await service.PreviewAsync(bytes);
        new SettingsStore(settings).Write(new Dictionary<string, string?> { ["theme"] = "system" });
        await Assert.ThrowsAsync<DotEnvConflictException>(() => service.RestoreAsync(settingsPreview.Id, true));
        Assert.Equal("old", DotEnvFile.ReadValue(Env, "PUBLIC_SETTING"));
    }
    [Fact] public async Task LocalExportAuthorizedNewFileOnlyAndMobileReadable()
    {
        File.WriteAllText(Env, "PUBLIC_SETTING=old\nTOKEN=secret\n");
        var service = new BackupLocalService(Env, "test", new Guard());
        var destination = Path.Combine(directory, "backup.json");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAsync(destination, false));
        await service.ExportAsync(destination, true);
        Assert.Single(BackupBundle.Decode(File.ReadAllBytes(destination)).Environment);
        await Assert.ThrowsAsync<IOException>(() => service.ExportAsync(destination, true));
        Assert.DoesNotContain("secret", File.ReadAllText(destination));
    }

    [Fact]
    public async Task PreferenceWriteFailureRollsBackEnvironmentAndFavorites()
    {
        File.WriteAllText(Env, "PUBLIC_SETTING=old\nTOKEN=local-secret\n");
        var favorites = Path.Combine(directory, ".cache", "favoritesCache");
        Directory.CreateDirectory(Path.GetDirectoryName(favorites)!);
        File.WriteAllText(favorites, "{\"original\":{}}");
        var preferences = Path.Combine(directory, "settings.properties");
        File.WriteAllText(preferences, "invalid-settings-line");
        var originals = new[] { Env, favorites, preferences }.ToDictionary(path => path, File.ReadAllBytes);
        var guard = new Guard();
        var service = new BackupLocalService(Env, "test", guard, preferences);
        var bytes = BackupBundle.Encode(new Dictionary<string, string> { ["PUBLIC_SETTING"] = "new" }, "test",
            "{\"replacement\":{}}", new Dictionary<string, string> { ["theme"] = "light" });
        var preview = await service.PreviewAsync(bytes);

        var failure = await Assert.ThrowsAsync<IOException>(() => service.RestoreAsync(preview.Id, true));

        Assert.IsType<FormatException>(failure.InnerException);
        foreach (var (path, original) in originals) Assert.Equal(original, File.ReadAllBytes(path));
        Assert.True(guard.Disposed);
        await Assert.ThrowsAsync<IOException>(() => service.RestoreAsync(preview.Id, true));
    }

    [Fact]
    public async Task WebDavDeadlineIncludesResponseBody()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new StalledBody()) }))) { Timeout = TimeSpan.FromMilliseconds(100) };
        using var client = new BackupWebDavClient(http);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadAsync(Config, stop.Token));
        Assert.False(stop.IsCancellationRequested);
    }

    [Fact]
    public async Task LeavingBackupPageCancelsDownloadAndClearsPassword()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(_ =>
        {
            entered.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledBody()) });
        }));
        using var remote = new BackupWebDavClient(http);
        var model = new DanmuApi.App.ViewModels.BackupPageViewModel(new BackupLocalService(Env, "test", new Guard()),
            remote, new BackupWebDavSettings(new ProtectedStore()), new BackupDialogs())
        { CollectionUrl = Config.CollectionUrl, Username = Config.Username, Password = Config.Password };
        var request = model.DownloadCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await model.DisposeAsync();
        await request.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(model.IsBusy);
        Assert.Empty(model.Password);
        Assert.IsAssignableFrom<OperationCanceledException>(model.LastFailure);
        Assert.Contains("取消", model.Status);
    }

    private sealed class BackupDialogs : DanmuApi.App.Services.IBackupDialogService
    {
        public Task<string?> PickFileAsync(bool save) => throw new NotSupportedException();
        public Task<bool> ConfirmAsync(string title, string message) => throw new NotSupportedException();
    }

    private sealed class StalledBody : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private static BackupWebDavConfiguration Config => new("https://example.invalid/dav/DanmuApi/", "user", "password");
    [Fact] public async Task HttpMethodsAuthOverwriteConditionAndDownloadAreReal()
    {
        var methods = new List<string>();
        using var http = new HttpClient(new Handler(async request =>
        {
            methods.Add(request.Method.Method);
            Assert.Equal("https://example.invalid/dav/DanmuApi/app-backup.json", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            if (request.Method == HttpMethod.Put)
            {
                Assert.Equal("*", request.Headers.GetValues("If-None-Match").Single());
                _ = BackupBundle.Decode(await request.Content!.ReadAsByteArrayAsync());
                return new HttpResponseMessage(HttpStatusCode.Created);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bundle()) };
        }));
        var client = new BackupWebDavClient(http);
        await client.UploadAsync(Config, Bundle(), false);
        _ = await client.DownloadAsync(Config);
        Assert.Equal(new[] { "PUT", "GET" }, methods);
    }
    [Theory]
    [InlineData(301)] [InlineData(401)] [InlineData(404)] [InlineData(412)] [InlineData(500)]
    public async Task HttpFailuresAreExplicitAndDoNotEchoServerBody(int status)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("password secret") })));
        var error = await Assert.ThrowsAsync<BackupWebDavException>(() => new BackupWebDavClient(http).DownloadAsync(Config));
        Assert.Contains(status.ToString(), error.Message);
        Assert.DoesNotContain("secret", error.Message);
    }
    [Fact] public async Task PropfindChecksMultiStatusAndFindsMobileFile()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("PROPFIND", request.Method.Method);
            Assert.Equal("1", request.Headers.GetValues("Depth").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MultiStatus) { Content = new StringContent("<d:multistatus xmlns:d='DAV:'><d:response><d:href>/dav/DanmuApi/app-backup.json</d:href><d:propstat><d:prop><d:resourcetype/></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>") });
        }));
        Assert.Equal(new[] { "app-backup.json" }, await new BackupWebDavClient(http).ListAsync(Config));
    }
    [Fact] public void RejectsHttpAndCredentialAddress()
    {
        Assert.Throws<InvalidDataException>(() => (Config with { CollectionUrl = "http://example.invalid/dav/" }).Collection());
        Assert.Throws<InvalidDataException>(() => (Config with { CollectionUrl = "https://user:pass@example.invalid/dav/" }).Collection());
        Assert.DoesNotContain("password", Config.ToString());
    }
    [Fact] public void CredentialStoreUsesDedicatedProtectedPayload()
    {
        var store = new ProtectedStore();
        var settings = new BackupWebDavSettings(store);
        settings.Save(Config);
        Assert.Equal(Config, settings.Load());
        settings.Clear();
        Assert.Null(settings.Load());
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
    private sealed class Guard : IBackupRestoreGuard, IAsyncDisposable
    {
        public bool Running; public bool Disposed;
        public ValueTask<IAsyncDisposable> AcquireStoppedLeaseAsync(CancellationToken cancellationToken) => Running
            ? throw new InvalidOperationException("核心运行中") : ValueTask.FromResult<IAsyncDisposable>(this);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class ProtectedStore : IProtectedStringStore
    {
        private string? value;
        public string? Load() => value;
        public void Save(string text) => value = text;
        public void Clear() => value = null;
    }
}
