using System.Net;
using System.Text;
using System.Text.Json;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class RuntimeManagementClientTests
{
    private const string RuntimeToken = "runtime-private-token";
    private const string AdminToken = "admin-private-token";

    [Fact]
    public async Task ReadAccessUsesLoopbackBearerAndParsesBundledHostContract()
    {
        HttpRequestMessage? captured = null;
        using var http = new HttpClient(new Handler(request =>
        {
            captured = CloneMetadata(request);
            return Json(HttpStatusCode.OK, AccessPayload());
        }));
        var client = new RuntimeManagementClient(http);

        var snapshot = await client.ReadAccessAsync("127.0.0.1", 9321, RuntimeToken);

        Assert.Equal("blacklist", snapshot.Mode);
        Assert.Equal(["192.168.1.20", "fd00::20"], snapshot.Blacklist);
        var device = Assert.Single(snapshot.Devices);
        Assert.Equal("192.168.1.20", device.Ip);
        Assert.Equal(7, device.TotalRequests);
        Assert.Equal(5, device.AllowedRequests);
        Assert.Equal(2, device.BlockedRequests);
        Assert.True(device.InBlacklist);
        Assert.True(device.EffectiveBlocked);
        Assert.Equal(12, snapshot.TotalAllowedRequests);
        Assert.Equal(3, snapshot.TotalBlockedRequests);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Equal("http://127.0.0.1:9321/__access-control", captured.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal(RuntimeToken, captured.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task SaveAccessNormalizesRulesAndRequiresMatchingConfirmedSnapshot()
    {
        string? body = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, AccessPayload("off", ["192.168.1.20", "fd00::20"], []));
        }));
        var client = new RuntimeManagementClient(http);

        var result = await client.SaveAccessAsync("127.0.0.1", 9321, RuntimeToken, "off",
            ["fd00:0:0:0:0:0:0:20", "192.168.1.20", "192.168.1.20"], clearDevices: true);

        Assert.Empty(result.Devices);
        using var json = JsonDocument.Parse(body!);
        Assert.Equal("off", json.RootElement.GetProperty("mode").GetString());
        Assert.True(json.RootElement.GetProperty("clearDevices").GetBoolean());
        Assert.Equal(["192.168.1.20", "fd00::20"],
            json.RootElement.GetProperty("blacklist").EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    [Fact]
    public async Task SaveAccessRejectsServerMismatchAsUnconfirmedMutation()
    {
        using var http = new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, AccessPayload("off", [], []))));
        var client = new RuntimeManagementClient(http);

        var error = await Assert.ThrowsAsync<FormatException>(() => client.SaveAccessAsync(
            "127.0.0.1", 9321, RuntimeToken, "blacklist", ["192.168.1.20"]));

        Assert.Contains("部分修改", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearLogsUsesAdminPathAndRequiresExactCoreConfirmation()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            captured = CloneMetadata(request);
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, "{\"success\":true,\"message\":\"Logs cleared\"}");
        }));
        var client = new RuntimeManagementClient(http);

        await client.ClearLogsAsync("127.0.0.1", 9321, RuntimeToken, AdminToken);

        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal($"http://127.0.0.1:9321/{AdminToken}/api/logs/clear", captured.RequestUri!.AbsoluteUri);
        Assert.Equal(RuntimeToken, captured.Headers.Authorization!.Parameter);
        Assert.Equal("{}", body);

        using var invalidHttp = new HttpClient(new Handler(_ => Json(HttpStatusCode.OK,
            "{\"success\":true,\"message\":\"different\"}")));
        var invalid = new RuntimeManagementClient(invalidHttp);
        await Assert.ThrowsAsync<FormatException>(() => invalid.ClearLogsAsync(
            "127.0.0.1", 9321, RuntimeToken, AdminToken));
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("192.168.1.8")]
    public async Task ManagementRequestsRejectNonLoopbackHostsBeforeNetwork(string host)
    {
        var called = false;
        using var http = new HttpClient(new Handler(_ =>
        {
            called = true;
            return Json(HttpStatusCode.OK, AccessPayload());
        }));
        var client = new RuntimeManagementClient(http);

        await Assert.ThrowsAsync<ArgumentException>(() => client.ReadAccessAsync(host, 9321, RuntimeToken));

        Assert.False(called);
    }

    [Fact]
    public async Task FailuresAreExplicitBoundedAndDoNotExposeCredentials()
    {
        using var unauthorizedHttp = new HttpClient(new Handler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent($"server echoed {RuntimeToken} {AdminToken}")
            }));
        var unauthorized = new RuntimeManagementClient(unauthorizedHttp);
        var httpError = await Assert.ThrowsAsync<HttpRequestException>(() => unauthorized.ClearLogsAsync(
            "127.0.0.1", 9321, RuntimeToken, AdminToken));
        Assert.Equal(HttpStatusCode.Forbidden, httpError.StatusCode);
        Assert.DoesNotContain(RuntimeToken, httpError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(AdminToken, httpError.Message, StringComparison.Ordinal);

        using var malformedHttp = new HttpClient(new Handler(_ => Json(HttpStatusCode.OK,
            "{\"success\":true,\"success\":true}")));
        var malformed = new RuntimeManagementClient(malformedHttp);
        await Assert.ThrowsAsync<FormatException>(() => malformed.ReadAccessAsync("127.0.0.1", 9321, RuntimeToken));

        using var oversizedHttp = new HttpClient(new Handler(_ =>
        {
            var response = Json(HttpStatusCode.OK, "{}");
            response.Content.Headers.ContentLength = 1024 * 1024 + 1;
            return response;
        }));
        var oversized = new RuntimeManagementClient(oversizedHttp);
        await Assert.ThrowsAsync<IOException>(() => oversized.ReadAccessAsync("127.0.0.1", 9321, RuntimeToken));
    }

    [Theory]
    [InlineData("127.0.0.1/24")]
    [InlineData("fe80::1%12")]
    [InlineData("127.1")]
    [InlineData("not-an-ip")]
    public void InvalidAccessRulesFailInsteadOfBeingDropped(string value) =>
        Assert.Throws<FormatException>(() => RuntimeManagementClient.ValidateIp(value));

    private static string AccessPayload(string mode = "blacklist", string[]? blacklist = null, string[]? devices = null)
    {
        blacklist ??= ["192.168.1.20", "fd00::20"];
        devices ??=
        [
            "{\"ip\":\"192.168.1.20\",\"firstSeenAtMs\":1,\"lastSeenAtMs\":9," +
            "\"totalRequests\":7,\"allowedRequests\":5,\"blockedRequests\":2," +
            "\"inBlacklist\":true,\"effectiveBlocked\":true}"
        ];
        return $$"""
        {
          "success": true,
          "config": { "mode": "{{mode}}", "blacklist": {{JsonSerializer.Serialize(blacklist)}} },
          "stats": { "totalAllowedRequests": 12, "totalBlockedRequests": 3 },
          "devices": [{{string.Join(',', devices)}}]
        }
        """;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpRequestMessage CloneMetadata(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        clone.Headers.Authorization = request.Headers.Authorization;
        return clone;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> send)
            : this(request => Task.FromResult(send(request)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
