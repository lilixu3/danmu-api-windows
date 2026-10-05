using DanmuApi.Core.Frp;
using DanmuApi.Runtime.Frp;

namespace DanmuApi.Tests;

public sealed class FrpNativeVerifierDiagnosticTests
{
    [SkippableFact]
    public async Task NativeUnknownAuthFieldDoesNotExposeAUserSuppliedKeyInDiagnostics()
    {
        var directory = Environment.GetEnvironmentVariable("DANMU_TEST_FRP_DIR");
        Skip.If(string.IsNullOrWhiteSpace(directory), "DANMU_TEST_FRP_DIR 未设置；跳过不算通过");
        var executable = Path.Combine(directory!, "frpc.exe");
        Skip.IfNot(File.Exists(executable), "需要本地官方 frpc.exe；跳过不算通过");
        using var temporary = new TemporaryDirectory();
        var native = FrpNativeConfig.Parse("""
            {"serverAddr":"127.0.0.1","auth":{"private-source-key":true},
             "proxies":[{"name":"api","type":"tcp","localPort":9321,"remotePort":19321}]}
            """);
        var path = Path.Combine(temporary.Path, "frpc.json");
        File.WriteAllText(path, native.CreateRuntimeConfig("admin", "local-test-secret"));
        var plan = new FrpRunPlan(FrpRole.Client, executable, path, temporary.Path, temporary.Path, native.AdminPort,
            "admin", "local-test-secret", "api", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5))
        { Secrets = native.Secrets };

        var result = await new FrpNativeVerifier().VerifyAsync(plan);

        Assert.False(result.Succeeded);
        Assert.Contains("exitCode=1", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("不支持", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-source-key", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("local-test-secret", result.Diagnostic, StringComparison.Ordinal);
    }
}
