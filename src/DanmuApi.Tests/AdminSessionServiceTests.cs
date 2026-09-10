using DanmuApi.App.Services;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class AdminSessionServiceTests : IDisposable
{
    private const string Password = "adm1n-pass-42";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"danmu-admin-{Guid.NewGuid():N}");

    [Fact]
    public void MissingEnvFileStartsUnconfigured()
    {
        var service = CreateService();

        Assert.False(service.State.IsAdminMode);
        Assert.False(service.State.HasAdminTokenConfigured);
        Assert.Equal("未配置", service.State.TokenHint);
        Assert.Null(service.CurrentAdminTokenOrNull());
    }

    [Fact]
    public void SetAdminTokenAndLoginWritesEnvReadBackAndEntersAdminMode()
    {
        var service = CreateService();

        var result = service.SetAdminTokenAndLogin(Password);

        Assert.True(result.Succeeded);
        Assert.Equal(Password, DotEnvFile.ReadValue(EnvPath, "ADMIN_TOKEN"));
        Assert.True(service.State.IsAdminMode);
        Assert.True(service.State.HasAdminTokenConfigured);
        Assert.Equal("ad***42", service.State.TokenHint);
        Assert.Equal(Password, service.CurrentAdminTokenOrNull());
        Assert.DoesNotContain(Password, service.State.TokenHint, StringComparison.Ordinal);
    }

    [Fact]
    public void LoginRequiresExactConfiguredToken()
    {
        var service = CreateService();
        service.SetAdminTokenAndLogin(Password);
        service.Logout();

        Assert.False(service.Login("wrong").Succeeded);
        Assert.False(service.State.IsAdminMode);
        Assert.Null(service.CurrentAdminTokenOrNull());
        Assert.True(service.Login(Password).Succeeded);
        Assert.True(service.State.IsAdminMode);
    }

    [Fact]
    public void FailureMessagesNeverLeakThePassword()
    {
        var service = CreateService();

        Assert.DoesNotContain(Password, service.Login(string.Empty).Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, service.Login(Password).Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, service.SetAdminTokenAndLogin("   ").Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionSurvivesRestartAndStaysBoundToEnvToken()
    {
        WriteEnv(_root, "TOKEN=87654321\n");
        var store = CreateStore();
        var first = new AdminSessionService(store, () => EnvPath);
        first.SetAdminTokenAndLogin(Password);

        var second = new AdminSessionService(store, () => EnvPath);

        Assert.True(second.State.IsAdminMode);
        Assert.Equal(Password, second.CurrentAdminTokenOrNull());
    }

    [Fact]
    public void RotatedOrClearedEnvTokenInvalidatesPersistedSession()
    {
        var store = CreateStore();
        var service = new AdminSessionService(store, () => EnvPath);
        service.SetAdminTokenAndLogin(Password);

        DotEnvFile.UpdateValues(EnvPath, new Dictionary<string, string?> { ["ADMIN_TOKEN"] = "rotated-99" });
        service.Refresh();
        Assert.False(service.State.IsAdminMode);
        Assert.Null(service.CurrentAdminTokenOrNull());
        Assert.Null(store.Load());

        DotEnvFile.UpdateValues(EnvPath, new Dictionary<string, string?> { ["ADMIN_TOKEN"] = Password });
        service.Login(Password);
        Assert.True(service.State.IsAdminMode);

        DotEnvFile.UpdateValues(EnvPath, new Dictionary<string, string?> { ["ADMIN_TOKEN"] = null });
        service.Refresh();
        Assert.False(service.State.IsAdminMode);
        Assert.False(service.State.HasAdminTokenConfigured);
    }

    [Fact]
    public void LogoutKeepsEnvTokenButExitsAdminMode()
    {
        var service = CreateService();
        service.SetAdminTokenAndLogin(Password);

        var result = service.Logout();

        Assert.True(result.Succeeded);
        Assert.False(service.State.IsAdminMode);
        Assert.True(service.State.HasAdminTokenConfigured);
        Assert.Equal(Password, DotEnvFile.ReadValue(EnvPath, "ADMIN_TOKEN"));
    }

    [Fact]
    public void ShortTokenHintMasksAggressively()
    {
        Assert.Equal("未配置", AdminSessionService.MaskToken(""));
        Assert.Equal("a***", AdminSessionService.MaskToken("abc"));
        Assert.Equal("ab***yz", AdminSessionService.MaskToken("abcdefyz"));
    }

    [Fact]
    public void SessionStoreFailureRestoresPreviousAdminToken()
    {
        WriteEnv(_root, "TOKEN=87654321\nADMIN_TOKEN=previous-admin\n");
        var store = new FailingProtectedStringStore();
        var service = new AdminSessionService(store, () => EnvPath);

        var error = Assert.Throws<IOException>(() => service.SetAdminTokenAndLogin(Password));

        Assert.Contains("已恢复原 ADMIN_TOKEN", error.Message, StringComparison.Ordinal);
        Assert.Equal("previous-admin", DotEnvFile.ReadValue(EnvPath, "ADMIN_TOKEN"));
        Assert.False(service.State.IsAdminMode);
        Assert.True(service.State.HasAdminTokenConfigured);
        Assert.Null(service.CurrentAdminTokenOrNull());
    }

    [Fact]
    public void SessionStoreAndRollbackFailureReportsBothFailures()
    {
        WriteEnv(_root, "TOKEN=87654321\nADMIN_TOKEN=previous-admin\n");
        var store = new FailingProtectedStringStore(() =>
        {
            File.Delete(EnvPath);
            Directory.CreateDirectory(EnvPath);
        });
        var service = new AdminSessionService(store, () => EnvPath);

        var error = Assert.Throws<IOException>(() => service.SetAdminTokenAndLogin(Password));

        Assert.Contains("管理员会话保存失败", error.Message, StringComparison.Ordinal);
        Assert.Contains("恢复原 ADMIN_TOKEN 失败", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProtectedStringStoreRoundTripsThroughDpapi()
    {
        var store = CreateStore();

        Assert.Null(store.Load());
        store.Save("plain-value");
        Assert.Equal("plain-value", store.Load());
        store.Clear();
        Assert.Null(store.Load());
        Assert.Throws<ArgumentException>(() => store.Save("   "));
    }

    private sealed class FailingProtectedStringStore(Action? onSave = null) : IProtectedStringStore
    {
        public string? Load() => null;
        public void Save(string value)
        {
            onSave?.Invoke();
            throw new IOException("DPAPI test failure");
        }
        public void Clear() { }
    }

    private string EnvPath => Path.Combine(_root, "config", ".env");

    private AdminSessionService CreateService()
    {
        WriteEnv(_root, "TOKEN=87654321\n");
        return new AdminSessionService(CreateStore(), () => EnvPath);
    }

    private WindowsProtectedStringStore CreateStore() =>
        new(Path.Combine(_root, "admin-session.dat"), "DanmuApi.Tests.AdminSession");

    private static void WriteEnv(string root, string content)
    {
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, ".env"), content);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
