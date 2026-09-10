using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class RuntimeConfigTests
{
    [Fact]
    public void SettingsOverrideEnvAndDefaultsAreApplied()
    {
        using var directory = new TemporaryDirectory();
        var env = Path.Combine(directory.Path, "config", ".env");
        Directory.CreateDirectory(Path.GetDirectoryName(env)!);
        File.WriteAllText(env, "DANMU_API_PORT=8123\nDANMU_API_HOST=127.0.0.1\nDANMU_API_VARIANT=dev\n", new System.Text.UTF8Encoding(false));

        var fromEnv = RuntimeConfigResolver.Resolve(new RuntimeSettings(), directory.Path);
        var fromSettings = RuntimeConfigResolver.Resolve(new RuntimeSettings(9322, "0.0.0.0", "custom"), directory.Path);
        var defaults = RuntimeConfigResolver.Resolve(new RuntimeSettings(), Path.Combine(directory.Path, "missing"));

        Assert.Equal(new RuntimeConfig(8123, "127.0.0.1", "dev"), fromEnv);
        Assert.Equal(new RuntimeConfig(9322, "0.0.0.0", "custom"), fromSettings);
        Assert.Equal(new RuntimeConfig(9321, "0.0.0.0", "stable"), defaults);
    }

    [Fact]
    public void Ipv6SettingForcesIpv6WildcardHost()
    {
        using var directory = new TemporaryDirectory();
        var env = Path.Combine(directory.Path, "config", ".env");
        Directory.CreateDirectory(Path.GetDirectoryName(env)!);
        File.WriteAllText(env, "DANMU_API_HOST=127.0.0.1\n", new System.Text.UTF8Encoding(false));

        var resolved = RuntimeConfigResolver.Resolve(new RuntimeSettings(Ipv6Enabled: true), directory.Path);

        Assert.Equal("::", resolved.ListenHost);
    }

    [Fact]
    public void InvalidEnvValuesFallBackButInvalidExplicitSettingsFail()
    {
        using var directory = new TemporaryDirectory();
        var env = Path.Combine(directory.Path, "config", ".env");
        Directory.CreateDirectory(Path.GetDirectoryName(env)!);
        File.WriteAllText(env, "DANMU_API_PORT=bad\nDANMU_API_VARIANT=unknown\n", new System.Text.UTF8Encoding(false));

        var resolved = RuntimeConfigResolver.Resolve(new RuntimeSettings(), directory.Path);
        Assert.Equal(9321, resolved.Port);
        Assert.Equal("stable", resolved.Variant);
        Assert.Throws<ArgumentOutOfRangeException>(() => RuntimeConfigResolver.Resolve(new RuntimeSettings(0), directory.Path));
    }
}
