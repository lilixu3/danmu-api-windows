using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DanmuApi.App.Services;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class OutboundDirectServiceTests
{
    [Fact]
    public async Task ReadyRequiresAuthenticatedHealthAndExposesNoPrivateSession()
    {
        using var fixture = new Fixture();
        fixture.WriteStatus(reason: "TMDB 已配置代理；该来源业务请求不会采用增强直连。");
        var changed = new List<OutboundDirectSnapshot>();
        fixture.Service.Changed += (_, snapshot) => changed.Add(snapshot);
        var snapshot = await fixture.Service.RefreshAsync();
        Assert.Equal("ready", snapshot.Status);
        Assert.True(snapshot.ServiceRunning);
        Assert.True(snapshot.Applied);
        Assert.Equal(1, snapshot.ProtocolVersion);
        Assert.Equal("test-helper-1", snapshot.HelperVersion);
        Assert.Contains("代理", snapshot.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.Token, snapshot.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, fixture.Handler.HealthRequests);
        await fixture.Service.RefreshAsync();
        Assert.Single(changed);
    }

    [Fact]
    public async Task StoppedRuntimeIgnoresStaleReadyFilesAndNeverQueriesHelper()
    {
        using var fixture = new Fixture();
        fixture.Runtime.Set(new(DesktopRuntimeState.Stopped));
        var snapshot = await fixture.Service.RefreshAsync();
        Assert.Equal("off", snapshot.Status);
        Assert.False(snapshot.ServiceRunning);
        Assert.False(snapshot.Applied);
        Assert.Equal(0, fixture.Handler.HealthRequests);
    }

    [Fact]
    public async Task StopEventClearsReadyImmediatelyAndDisposeUnsubscribes()
    {
        using var fixture = new Fixture();
        await fixture.Service.RefreshAsync();
        fixture.Runtime.Set(new(DesktopRuntimeState.Stopping, Pid: Fixture.NodePid, RuntimeIdentity: Fixture.Identity));
        Assert.Equal("off", fixture.Service.Snapshot.Status);
        Assert.False(fixture.Service.Snapshot.ServiceRunning);
        var before = fixture.Service.Snapshot;
        fixture.Service.Dispose();
        fixture.Runtime.Set(Fixture.Running);
        Assert.Equal(before, fixture.Service.Snapshot);
    }

    [Fact]
    public async Task MissingStatusIdentifiesAnOldHostWithoutPretendingApplied()
    {
        using var fixture = new Fixture();
        File.Delete(fixture.StatusPath);
        var snapshot = await fixture.Service.RefreshAsync();
        Assert.Equal("failed", snapshot.Status);
        Assert.True(snapshot.ServiceRunning);
        Assert.False(snapshot.Applied);
        Assert.Contains("当前实例尚未应用增强", snapshot.Reason, StringComparison.Ordinal);
        Assert.Contains("重启服务", snapshot.Reason, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Handler.HealthRequests);
        Assert.NotEmpty(fixture.Diagnostics.Messages);
    }

    [Theory]
    [InlineData("nodePid", "99999")]
    [InlineData("runtimeIdentity", "\"other-instance\"")]
    [InlineData("heartbeatUnixMs", "1")]
    [InlineData("schemaVersion", "2")]
    [InlineData("helperPid", "null")]
    [InlineData("helperPid", "1.5")]
    [InlineData("status", "\"unknown\"")]
    [InlineData("reason", "null")]
    [InlineData("unrecognized", "true")]
    public async Task InvalidStatusCannotBecomeReady(string field, string value)
    {
        using var fixture = new Fixture();
        fixture.Mutate(fixture.StatusPath, field, value);
        var snapshot = await fixture.Service.RefreshAsync();
        Assert.Equal("failed", snapshot.Status);
        Assert.NotEmpty(fixture.Diagnostics.Messages);
        Assert.DoesNotContain(Fixture.Token, snapshot.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigMismatchNeverQueriesTheOldSession()
    {
        using var fixture = new Fixture();
        fixture.Store.Write(fixture.Settings with { HttpVersion = "h2" });
        var snapshot = await fixture.Service.RefreshAsync();
        Assert.Equal("failed", snapshot.Status);
        Assert.False(snapshot.Applied);
        Assert.Contains("尚未应用", snapshot.Reason, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Handler.HealthRequests);
    }

    [Theory]
    [InlineData("http://localhost:19322")]
    [InlineData("http://127.0.0.2:19322")]
    [InlineData("http://user:password@127.0.0.1:19322")]
    [InlineData("https://127.0.0.1:19322")]
    [InlineData("http://127.0.0.1:19322/health")]
    [InlineData("http://127.0.0.1:19322/?token=private")]
    [InlineData("http://127.0.0.1:19322/#private")]
    [InlineData("http://127.0.0.1:0")]
    [InlineData("http://127.0.0.1:65536")]
    [InlineData("http://2130706433:19322")]
    public async Task UnsafeSessionEndpointsAreRejectedBeforeAnyHttp(string endpoint)
    {
        using var fixture = new Fixture();
        fixture.Mutate(fixture.SessionPath, "endpoint", JsonSerializer.Serialize(endpoint));
        var snapshot = await fixture.Service.RefreshAsync();
        Assert.Equal("failed", snapshot.Status);
        Assert.Equal(0, fixture.Handler.HealthRequests);
        Assert.DoesNotContain(endpoint, snapshot.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("protocolVersion", "2")]
    [InlineData("protocolVersion", "\"1\"")]
    [InlineData("pid", "99999")]
    [InlineData("version", "\"\"")]
    [InlineData("success", "false")]
    [InlineData("success", "\"true\"")]
    public async Task HealthProtocolAndPidMustMatch(string field, string value)
    {
        using var fixture = new Fixture();
        fixture.Handler.Health = _ =>
        {
            var json = Fixture.HealthJson();
            json[field] = JsonNode.Parse(value);
            return Fixture.JsonResponse(json);
        };
        var snapshot = await fixture.Service.RefreshAsync();
        Assert.Equal("failed", snapshot.Status);
        Assert.Null(snapshot.ProtocolVersion);
        Assert.NotEmpty(fixture.Diagnostics.Messages);
    }

    [Fact]
    public async Task NodeOrHelperLivenessFailureRejectsPidReuseBeforeHttp()
    {
        using var fixture = new Fixture(processAlive: pid => pid != Fixture.HelperPid);
        var snapshot = await fixture.Service.RefreshAsync();
        Assert.Equal("failed", snapshot.Status);
        Assert.Contains("已退出", snapshot.Reason, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Handler.HealthRequests);
    }

    [Fact]
    public async Task StopDuringHealthResponseCannotPublishReadyAgain()
    {
        using var fixture = new Fixture();
        var seen = new List<string>();
        fixture.Service.Changed += (_, snapshot) => seen.Add(snapshot.Status);
        fixture.Handler.Health = _ =>
        {
            fixture.Runtime.Set(new(DesktopRuntimeState.Stopped));
            return Fixture.JsonResponse(Fixture.HealthJson());
        };
        await fixture.Service.RefreshAsync();
        Assert.Equal("off", fixture.Service.Snapshot.Status);
        Assert.DoesNotContain("ready", seen);
    }

    [Fact]
    public async Task ChangedSessionDuringHealthDoesNotReuseOldToken()
    {
        using var fixture = new Fixture();
        fixture.Handler.Health = _ =>
        {
            fixture.Mutate(fixture.SessionPath, "token", JsonSerializer.Serialize(new string('b', 64)));
            return Fixture.JsonResponse(Fixture.HealthJson());
        };
        var snapshot = await fixture.Service.RefreshAsync();
        Assert.Equal("failed", snapshot.Status);
        Assert.Contains("会话已改变", snapshot.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticationFailureInvalidJsonAndOversizeAreExplicit()
    {
        using var fixture = new Fixture();
        fixture.Handler.Health = _ => new(HttpStatusCode.Unauthorized);
        Assert.Contains("HTTP 401", (await fixture.Service.RefreshAsync()).Reason, StringComparison.Ordinal);
        fixture.Handler.Health = _ => new(HttpStatusCode.OK) { Content = new StringContent("{private-json-value") };
        var invalid = await fixture.Service.RefreshAsync();
        Assert.Equal("failed", invalid.Status);
        Assert.DoesNotContain("private-json-value", invalid.Reason, StringComparison.Ordinal);
        fixture.Handler.Health = _ => new(HttpStatusCode.OK) { Content = new StringContent(new string(' ', 1_048_577)) };
        Assert.Contains("1 MiB", (await fixture.Service.RefreshAsync()).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorruptSettingsNeverExposeDefaultsAndSafeReadThrows()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Store.SettingsPath, "{private-json-value");
        var error = Assert.Throws<IOException>(() => fixture.Service.ReadSettings());
        Assert.DoesNotContain("private-json-value", error.ToString(), StringComparison.Ordinal);
        var snapshot = await fixture.Service.RefreshAsync();
        Assert.Equal("failed", snapshot.Status);
        Assert.Null(snapshot.Settings);
        Assert.Equal(0, fixture.Handler.HealthRequests);
    }

    [Fact]
    public async Task SaveWhileStoppedOnlyPersistsAndDoesNotStartAnything()
    {
        using var fixture = new Fixture();
        fixture.Runtime.Set(new(DesktopRuntimeState.Stopped));
        var expected = fixture.Settings with { HttpVersion = "h2", ConnectTimeoutMs = 4321 };
        var result = await fixture.Service.SaveAsync(expected);
        Assert.True(result.Succeeded);
        Assert.True(OutboundSettings.Equivalent(expected, fixture.Store.Read()));
        Assert.Equal("off", fixture.Service.Snapshot.Status);
        Assert.Equal(0, fixture.Runtime.StartCalls);
        Assert.Equal(0, fixture.Handler.HealthRequests);
    }

    [Fact]
    public async Task SaveInvalidOrCancelledDoesNotTouchFileAndReadbackMismatchFails()
    {
        using var fixture = new Fixture();
        var original = File.ReadAllBytes(fixture.Store.SettingsPath);
        Assert.False((await fixture.Service.SaveAsync(fixture.Settings with { Sources = Array.Empty<string>() })).Succeeded);
        Assert.Equal(original, File.ReadAllBytes(fixture.Store.SettingsPath));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.False((await fixture.Service.SaveAsync(fixture.Settings with { HttpVersion = "h2" }, cancellation.Token)).Succeeded);
        Assert.Equal(original, File.ReadAllBytes(fixture.Store.SettingsPath));
        using var service = new OutboundDirectService(new MismatchedStore(fixture.Store), fixture.Runtime, fixture.Diagnostics, fixture.Http,
            _ => true, fixture.Clock);
        var mismatch = await service.SaveAsync(fixture.Settings with { HttpVersion = "h3" });
        Assert.False(mismatch.Succeeded);
        Assert.Contains("回读", mismatch.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationAfterWriteExplicitlyReportsPersistedButUnverifiedConfiguration()
    {
        using var fixture = new Fixture();
        using var service = new OutboundDirectService(new CancelledReadStore(fixture.Store), fixture.Runtime,
            fixture.Diagnostics, fixture.Http, _ => true, fixture.Clock);
        var expected = fixture.Settings with { HttpVersion = "h3" };
        var result = await service.SaveAsync(expected);
        Assert.False(result.Succeeded);
        Assert.Contains("已写入", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("回读校验已取消", result.Diagnostic, StringComparison.Ordinal);
        Assert.True(OutboundSettings.Equivalent(expected, fixture.Store.Read()));
    }

    [Fact]
    public async Task EveryExactDomainIsIndependentAndRequestsContainOnlyPublicFixedUrls()
    {
        using var fixture = new Fixture();
        fixture.Handler.Query = (target, _) => target.Host == "api.animeko.org"
            ? Fixture.JsonResponse(new { success = false, phase = "dns", errorCode = "dns_failed", message = "HTTPS RR query: NXDOMAIN" }, HttpStatusCode.BadGateway)
            : Fixture.Success(target);
        var run = await fixture.Service.DiagnoseAsync();
        Assert.Equal(OutboundDiagnosticRunStatus.Completed, run.Status);
        var rows = run.Rows;
        Assert.Equal(10, rows.Count);
        Assert.Equal(10, rows.Select(row => row.Host).Distinct().Count());
        Assert.Equal(4, rows.Count(row => row.EchRequired));
        Assert.Equal(5, rows.Count(row => row.Source == "animeko"));
        var failed = Assert.Single(rows, row => row.Host == "api.animeko.org");
        Assert.Equal("dns", failed.FailurePhase);
        Assert.Contains("NXDOMAIN", failed.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("dns_failed", failed.Diagnostic, StringComparison.Ordinal);
        Assert.Contains(rows, row => row.Host == "danmaku-global.myani.org" && row.Succeeded);
        Assert.All(fixture.Handler.Targets, target =>
        {
            Assert.Equal("https", target.Scheme);
            Assert.Equal(443, target.Port);
            Assert.Equal("", target.UserInfo);
            Assert.Equal("", target.Fragment);
            Assert.DoesNotContain("token", target.Query, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("api_key", target.Query, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Tmdb401ShowsConnectedButNotBusinessSuccessAndOrdinaryTlsIsExplicit()
    {
        using var fixture = new Fixture();
        fixture.Select("tmdb");
        fixture.Handler.Query = (target, _) => Fixture.Success(target, status: 401);
        var run = await fixture.Service.DiagnoseAsync();
        Assert.Equal(OutboundDiagnosticRunStatus.Completed, run.Status);
        var rows = run.Rows;
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.False(row.Succeeded);
            Assert.Equal(401, row.HttpStatus);
            Assert.Equal("http", row.FailurePhase);
            Assert.Equal("h2", row.Protocol);
            Assert.False(row.EchAccepted);
            Assert.Contains("连接已建立", row.Diagnostic, StringComparison.Ordinal);
            Assert.Contains("不使用 ECH", row.Diagnostic, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task MissingEchOrWrongForcedProtocolIsNeverSuccessful()
    {
        using var fixture = new Fixture();
        fixture.Select("bahamut");
        fixture.Handler.Query = (target, _) => Fixture.Success(target, ech: false);
        var noEch = Assert.Single((await fixture.Service.DiagnoseAsync()).Rows);
        Assert.Equal("ech", noEch.FailurePhase);
        fixture.Settings = fixture.Settings with { HttpVersion = "h3" };
        fixture.Store.Write(fixture.Settings);
        fixture.WriteStatus();
        fixture.Handler.Query = (target, _) => Fixture.Success(target, protocol: "h2");
        var wrongProtocol = Assert.Single((await fixture.Service.DiagnoseAsync()).Rows);
        Assert.Equal("protocol", wrongProtocol.FailurePhase);
        Assert.Equal("h2", wrongProtocol.Protocol);
    }

    [Theory]
    [InlineData("success", "\"true\"")]
    [InlineData("status", "200.5")]
    [InlineData("protocol", "\"h9\"")]
    [InlineData("ech", "\"true\"")]
    [InlineData("body", "\"not-base64*\"")]
    [InlineData("headers", "[[\"header\",true]]")]
    public async Task MalformedRequestResponsesPreserveOneFailedRow(string field, string replacement)
    {
        using var fixture = new Fixture();
        fixture.Select("bahamut");
        fixture.Handler.Query = (target, _) =>
        {
            var document = Fixture.SuccessJson(target);
            document[field] = JsonNode.Parse(replacement);
            return Fixture.JsonResponse(document);
        };
        var row = Assert.Single((await fixture.Service.DiagnoseAsync()).Rows);
        Assert.False(row.Succeeded);
        Assert.NotNull(row.FailurePhase);
        Assert.NotEmpty(fixture.Diagnostics.Messages);
    }

    [Fact]
    public async Task ErrorsRedactTokensAndUrlCredentialsWhileKeepingErrorChain()
    {
        using var fixture = new Fixture();
        fixture.Select("bahamut");
        fixture.Handler.Query = (_, _) => Fixture.JsonResponse(new
        {
            success = false, phase = "tls", errorCode = "certificate_failed",
            message = $"TLS handshake: certificate expired; https://user:private-password@dns.example/secret-path?api_key=private-key; Bearer {Fixture.Token}",
        }, HttpStatusCode.BadGateway);
        var row = Assert.Single((await fixture.Service.DiagnoseAsync()).Rows);
        Assert.Equal("tls", row.FailurePhase);
        Assert.Contains("certificate expired", row.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("dns.example", row.Diagnostic, StringComparison.Ordinal);
        foreach (var secret in new[] { Fixture.Token, "private-password", "secret-path", "private-key" })
        {
            Assert.DoesNotContain(secret, row.Diagnostic, StringComparison.Ordinal);
            Assert.All(fixture.Diagnostics.Messages, message => Assert.DoesNotContain(secret, message, StringComparison.Ordinal));
        }
    }

    [Theory]
    [MemberData(nameof(SyntheticCredentialDiagnostics))]
    public async Task ServiceRedactsQuotedAndEnvironmentCredentialsBeforeLoggingWithoutAVm(string path, string sensitiveText)
    {
        using var fixture = new Fixture();
        fixture.Select("bahamut");
        var reason = "synthetic transport root cause; " + sensitiveText;
        string publicDiagnostic;
        if (path == "status")
        {
            fixture.WriteStatus("failed", reason);
            var snapshot = await fixture.Service.RefreshAsync();
            Assert.Equal("failed", snapshot.Status);
            publicDiagnostic = snapshot.Reason;
            Assert.Equal(0, fixture.Handler.HealthRequests);
        }
        else if (path == "request")
        {
            fixture.Handler.Query = (_, _) => Fixture.JsonResponse(new
            {
                success = false, phase = "tls", errorCode = "synthetic_test_failure", message = reason,
            }, HttpStatusCode.BadGateway);
            var row = Assert.Single((await fixture.Service.DiagnoseAsync()).Rows);
            Assert.Equal("tls", row.FailurePhase);
            publicDiagnostic = row.Diagnostic;
        }
        else
        {
            fixture.Handler.Health = _ => throw new IOException("synthetic outer failure", new InvalidOperationException(reason));
            var snapshot = await fixture.Service.RefreshAsync();
            Assert.Equal("failed", snapshot.Status);
            publicDiagnostic = snapshot.Reason;
            Assert.Contains("IOException", publicDiagnostic);
            Assert.Contains("InvalidOperationException", publicDiagnostic);
        }
        Assert.Contains("synthetic transport root cause", publicDiagnostic);
        Assert.Contains("已脱敏", publicDiagnostic);
        Assert.DoesNotContain("TEST_ONLY_VALUE_MARKER", publicDiagnostic, StringComparison.Ordinal);
        // The fake is the service's logging sink: there is deliberately no VM/UI sanitization in this test.
        Assert.NotEmpty(fixture.Diagnostics.Messages);
        Assert.All(fixture.Diagnostics.Messages, logged =>
        {
            Assert.DoesNotContain("TEST_ONLY_VALUE_MARKER", logged, StringComparison.Ordinal);
            Assert.DoesNotContain(Fixture.Token, logged, StringComparison.Ordinal);
        });
        Assert.Contains(fixture.Diagnostics.Messages, logged => logged.Contains("synthetic transport root cause", StringComparison.Ordinal));
    }

    public static IEnumerable<object[]> SyntheticCredentialDiagnostics()
    {
        string[] values =
        [
            """{"api_key":"TEST_ONLY_VALUE_MARKER"}""",
            """{"API-KEY": "TEST_ONLY_VALUE_MARKER"}""",
            """{"cookie":"session=TEST_ONLY_VALUE_MARKER; second=TEST_ONLY_VALUE_MARKER"}""",
            """{"cookies":"prefix TEST_ONLY_VALUE_MARKER suffix"}""",
            """{"authorization":"Bearer TEST_ONLY_VALUE_MARKER"}""",
            """{"Proxy-Authorization":"Basic TEST_ONLY_VALUE_MARKER"}""",
            """{"token":"TEST_ONLY_VALUE_MARKER"}""",
            """{"admin_token":"TEST_ONLY_VALUE_MARKER"}""",
            """{"DANMU_API_TOKEN":"TEST_ONLY_VALUE_MARKER"}""",
            """{"TMDB_API_KEY":"TEST_ONLY_VALUE_MARKER"}""",
            """{"SERVICE_KEY":"TEST_ONLY_VALUE_MARKER"}""",
            """{"key":"TEST_ONLY_VALUE_MARKER"}""",
            """{'cookie':'prefix TEST_ONLY_VALUE_MARKER suffix'}""",
            """{"api_key":"prefix\" TEST_ONLY_VALUE_MARKER suffix"}""",
            """{"cookie":"prefix\\path TEST_ONLY_VALUE_MARKER suffix"}""",
            """{ "password" : "TEST_ONLY_VALUE_MARKER", "secret": "TEST_ONLY_VALUE_MARKER" }""",
            "TOKEN=TEST_ONLY_VALUE_MARKER",
            "ADMIN_TOKEN='prefix TEST_ONLY_VALUE_MARKER suffix'",
            "DANMU_API_COOKIE=session=TEST_ONLY_VALUE_MARKER; second=TEST_ONLY_VALUE_MARKER",
            "TMDB_API_KEY=TEST_ONLY_VALUE_MARKER",
            "SERVICE_KEY=TEST_ONLY_VALUE_MARKER",
            "Cookie: session=TEST_ONLY_VALUE_MARKER; second=TEST_ONLY_VALUE_MARKER",
            "Set-Cookie: session=TEST_ONLY_VALUE_MARKER; second=TEST_ONLY_VALUE_MARKER; HttpOnly",
            "Authorization: Bearer TEST_ONLY_VALUE_MARKER",
            "Proxy-Authorization: Basic TEST_ONLY_VALUE_MARKER",
            "Bearer TEST_ONLY_VALUE_MARKER",
            "PASSWORD=TEST_ONLY_VALUE_MARKER; PASSWD=TEST_ONLY_VALUE_MARKER; SECRET=TEST_ONLY_VALUE_MARKER",
        ];
        foreach (var path in new[] { "status", "request", "exception" })
            foreach (var value in values) yield return [path, value];
    }

    [Fact]
    public async Task CancellationReturnsCancelledRunWithOnlyActualRequestRowsAndNoCompletionTime()
    {
        using var fixture = new Fixture();
        fixture.Select("bahamut");
        using var cancellation = new CancellationTokenSource();
        fixture.Handler.Query = (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return new(HttpStatusCode.OK);
        };
        var run = await fixture.Service.DiagnoseAsync(cancellation.Token);
        Assert.Equal(OutboundDiagnosticRunStatus.Cancelled, run.Status);
        Assert.Null(run.CompletedAt);
        var row = Assert.Single(run.Rows);
        Assert.Equal("cancelled", row.FailurePhase);
        Assert.False(row.Succeeded);
        Assert.Contains("取消", row.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreflightFailureHasNoInventedRowsAndNoCompletionTime()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.StatusPath, "{bad-json");
        var run = await fixture.Service.DiagnoseAsync();
        Assert.Equal(OutboundDiagnosticRunStatus.Failed, run.Status);
        Assert.Empty(run.Rows);
        Assert.Null(run.Settings);
        Assert.Null(run.CompletedAt);
        Assert.Empty(fixture.Handler.Targets);
        Assert.Contains("JSON", run.Diagnostic);
    }

    [Fact]
    public async Task DiagnoseUsesOneActuallyVerifiedConfigurationAndRealCompletionTime()
    {
        using var fixture = new Fixture();
        fixture.Select("tmdb");
        fixture.Settings = fixture.Settings with { HttpVersion = "h3" };
        fixture.Store.Write(fixture.Settings);
        fixture.WriteStatus();
        var completedAt = fixture.Clock.Now.AddMinutes(1);
        fixture.Handler.Query = (target, _) =>
        {
            fixture.Clock.Now = completedAt;
            fixture.WriteStatus();
            return Fixture.Success(target, protocol: "h3", status: 401);
        };
        var run = await fixture.Service.DiagnoseAsync();
        Assert.Equal(OutboundDiagnosticRunStatus.Completed, run.Status);
        Assert.Equal("h3", run.Settings!.HttpVersion);
        Assert.Equal(completedAt, run.CompletedAt);
        Assert.Equal(2, run.Rows.Count);
        Assert.All(run.Rows, row => Assert.Equal("http", row.FailurePhase));
        var list = Assert.IsAssignableFrom<IList<string>>(run.Settings.Sources);
        Assert.Throws<NotSupportedException>(() => list[0] = "bahamut");
        fixture.Settings = fixture.Settings with { HttpVersion = "h2" };
        Assert.Equal("h3", run.Settings.HttpVersion);
    }

    [Theory]
    [InlineData("config")]
    [InlineData("session")]
    [InlineData("stop")]
    public async Task PostvalidationRejectsChangedConfigurationSessionOrRuntime(string changed)
    {
        using var fixture = new Fixture();
        fixture.Select("bahamut");
        fixture.Handler.Query = (target, _) =>
        {
            if (changed == "config")
            {
                fixture.Settings = fixture.Settings with { HttpVersion = "h3" };
                fixture.Store.Write(fixture.Settings);
                fixture.WriteStatus();
            }
            else if (changed == "session") fixture.Mutate(fixture.SessionPath, "token", JsonSerializer.Serialize(new string('b', 64)));
            else fixture.Runtime.Set(new(DesktopRuntimeState.Stopped));
            return Fixture.Success(target);
        };
        var run = await fixture.Service.DiagnoseAsync();
        Assert.Equal(OutboundDiagnosticRunStatus.Failed, run.Status);
        Assert.Null(run.CompletedAt);
        Assert.Equal("auto", run.Settings!.HttpVersion);
        Assert.Single(run.Rows);
        Assert.NotEmpty(run.Diagnostic);
    }

    [Fact]
    public async Task FinalLivenessStopReturnsTheActualPublishedSnapshotNotTheCandidate()
    {
        Fixture? fixture = null;
        var helperChecks = 0;
        using var owned = fixture = new Fixture(pid =>
        {
            if (pid == Fixture.HelperPid && ++helperChecks == 2) fixture!.Runtime.Set(new(DesktopRuntimeState.Stopped));
            return true;
        });
        var returned = await fixture.Service.RefreshAsync();
        Assert.Same(fixture.Service.Snapshot, returned);
        Assert.Equal("off", returned.Status);
        Assert.False(returned.ServiceRunning);
    }

    [Fact]
    public async Task StopAndSameIdentityRestartInvalidatesThePreviousVerificationEpoch()
    {
        using var fixture = new Fixture();
        var healthCount = 0;
        var seen = new List<OutboundDirectSnapshot>();
        fixture.Service.Changed += (_, snapshot) => seen.Add(snapshot);
        fixture.Handler.Health = _ =>
        {
            if (++healthCount == 1)
            {
                fixture.Runtime.Set(new(DesktopRuntimeState.Stopped));
                fixture.Runtime.Set(Fixture.Running);
            }
            return Fixture.JsonResponse(Fixture.HealthJson());
        };
        var returned = await fixture.Service.RefreshAsync();
        Assert.DoesNotContain(seen, snapshot => snapshot.Status == "ready" && snapshot.RuntimeEpoch == 0);
        Assert.True(returned.RuntimeEpoch >= 2);
        Assert.Equal(fixture.Service.Snapshot.RuntimeEpoch, returned.RuntimeEpoch);
        Assert.NotEqual(0, returned.Revision);
    }

    private sealed class Fixture : IDisposable
    {
        public const int NodePid = 22222;
        public const int HelperPid = 33333;
        public const string Identity = "desktop-isolated-outbound-test";
        public static readonly string Token = new('a', 64);
        public static RuntimeSnapshot Running => new(DesktopRuntimeState.Running, 9321, NodePid, Identity);
        private readonly TemporaryDirectory _directory = new();
        public readonly OutboundSettingsStore Store;
        public readonly FakeRuntime Runtime = new();
        public readonly FakeDiagnostics Diagnostics = new();
        public readonly FixedClock Clock = new();
        public readonly FakeHandler Handler = new();
        public readonly HttpClient Http;
        public readonly OutboundDirectService Service;
        public OutboundSettings Settings = OutboundSettings.Default with { Enabled = true };
        public string StatusPath => Path.Combine(Store.DirectoryPath, "status.json");
        public string SessionPath => Path.Combine(Store.DirectoryPath, "session.json");

        public Fixture(Func<int, bool>? processAlive = null)
        {
            Store = new(new AppPaths(_directory.Path, Path.Combine(_directory.Path, "appdata")));
            Store.Write(Settings);
            WriteStatus();
            File.WriteAllText(SessionPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, endpoint = "http://127.0.0.1:19322", token = Token,
                nodePid = NodePid, helperPid = HelperPid, runtimeIdentity = Identity,
            }));
            Http = new(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            Service = new(Store, Runtime, Diagnostics, Http, processAlive ?? (_ => true), Clock);
        }

        public void Select(params string[] sources)
        {
            Settings = Settings with { Sources = sources };
            Store.Write(Settings);
            WriteStatus();
        }

        public void WriteStatus(string status = "ready", string reason = "") => File.WriteAllText(StatusPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, status, reason, nodePid = NodePid, helperPid = (int?)HelperPid,
            runtimeIdentity = Identity, heartbeatUnixMs = Clock.GetUtcNow().ToUnixTimeMilliseconds(), config = Settings,
        }));

        public void Mutate(string path, string field, string value)
        {
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            json[field] = JsonNode.Parse(value);
            File.WriteAllText(path, json.ToJsonString());
        }

        public static JsonObject HealthJson() => JsonNode.Parse(JsonSerializer.Serialize(new
        { success = true, protocolVersion = 1, pid = HelperPid, version = "test-helper-1" }))!.AsObject();

        public static JsonObject SuccessJson(Uri target, string protocol = "h2", bool? ech = null, int status = 200)
        {
            var body = target.Host == "api.gamer.com.tw" ? "{\"anime\":[]}" :
                target.Host == "api.danmaku.weeblify.app" ? "{\"animes\":[]}" :
                target.Host is "api.tmdb.org" or "api.themoviedb.org" ? "{\"images\":{}}" : "{\"id\":400602}";
            var required = target.Host is "api.gamer.com.tw" or "api.danmaku.weeblify.app" or "danmaku-global.myani.org" or "api.bangumi.vip";
            return JsonNode.Parse(JsonSerializer.Serialize(new
            {
                success = true, status, protocol, ech = ech ?? required, host = target.Host,
                headers = Array.Empty<string[]>(), body = Convert.ToBase64String(Encoding.UTF8.GetBytes(body)),
            }))!.AsObject();
        }
        public static HttpResponseMessage Success(Uri target, string protocol = "h2", bool? ech = null, int status = 200) =>
            JsonResponse(SuccessJson(target, protocol, ech, status));
        public static HttpResponseMessage JsonResponse(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

        public void Dispose() { Service.Dispose(); Http.Dispose(); _directory.Dispose(); }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Health = _ => Fixture.JsonResponse(Fixture.HealthJson());
        public Func<Uri, CancellationToken, HttpResponseMessage> Query = (target, _) => Fixture.Success(target);
        public readonly ConcurrentBag<Uri> Targets = new();
        public int HealthRequests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("127.0.0.1", request.RequestUri!.Host);
            Assert.Equal(19322, request.RequestUri.Port);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(Fixture.Token, request.Headers.Authorization?.Parameter);
            if (request.RequestUri.AbsolutePath == "/health")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Interlocked.Increment(ref HealthRequests);
                return Health(request);
            }
            Assert.Equal("/request", request.RequestUri.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            var root = document.RootElement;
            Assert.Equal("GET", root.GetProperty("method").GetString());
            Assert.Equal("", root.GetProperty("body").GetString());
            Assert.InRange(root.GetProperty("timeoutMs").GetInt32(), 8000, 30000);
            Assert.DoesNotContain(Fixture.Token, root.GetRawText(), StringComparison.Ordinal);
            var target = new Uri(root.GetProperty("url").GetString()!);
            Targets.Add(target);
            return Query(target, cancellationToken);
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeDiagnostics : IAppDiagnostics
    {
        public string? LastDiagnostic { get; private set; }
        public readonly ConcurrentBag<string> Messages = new();
        public void Record(string message, Exception? error = null)
        { LastDiagnostic = error is null ? message : message + error.Message; Messages.Add(LastDiagnostic); }
    }

    private sealed class FakeRuntime : IRuntimeController
    {
        public RuntimeSnapshot Snapshot { get; private set; } = Fixture.Running;
        public int StartCalls;
        public event EventHandler<RuntimeSnapshot>? SnapshotChanged;
        public void Set(RuntimeSnapshot value) { Snapshot = value; SnapshotChanged?.Invoke(this, value); }
        public Task StartAsync(CancellationToken cancellationToken = default) { StartCalls++; return Task.CompletedTask; }
        public Task<AdoptionResult> AdoptAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public string? ReconcileLiveness() => null;
        public Task StopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RefreshCoreSetupRequiredAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RestartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CancelledReadStore(IOutboundSettingsStore inner) : IOutboundSettingsStore
    {
        public string DirectoryPath => inner.DirectoryPath;
        public string SettingsPath => inner.SettingsPath;
        public OutboundSettings Read() => throw new OperationCanceledException();
        public void Write(OutboundSettings settings) => inner.Write(settings);
    }

    private sealed class MismatchedStore(IOutboundSettingsStore inner) : IOutboundSettingsStore
    {
        public string DirectoryPath => inner.DirectoryPath;
        public string SettingsPath => inner.SettingsPath;
        public OutboundSettings Read() => inner.Read() with { ConnectTimeoutMs = 1 };
        public void Write(OutboundSettings settings) => inner.Write(settings);
    }
}
