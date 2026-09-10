using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DanmuApi.Core;

public sealed record BackupWebDavConfiguration(string CollectionUrl, string Username, string Password)
{
    public override string ToString() => "WebDAV configuration (credentials hidden)";
    public Uri Collection()
    {
        var raw = CollectionUrl.Trim();
        if (!raw.Contains("://", StringComparison.Ordinal)) raw = "https://" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            string.IsNullOrWhiteSpace(Username) || Username.Contains(':') || Username.Any(char.IsControl) || string.IsNullOrEmpty(Password))
            throw new InvalidDataException("WebDAV 需要 HTTPS 集合地址、用户名和密码，地址不能含凭据、查询或片段");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
}

public sealed class BackupWebDavException(int statusCode) : IOException($"WebDAV 请求失败（HTTP {statusCode}）；检查集合地址、权限或覆盖授权")
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Use CreateHttpClient in production: redirects must never forward credentials.</summary>
public sealed class BackupWebDavClient(HttpClient http) : IDisposable
{
    public void Dispose() => http.Dispose();
    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(30) };
    public const string FileName = "app-backup.json";

    public async Task<IReadOnlyList<string>> ListAsync(BackupWebDavConfiguration config, CancellationToken ct = default)
    {
        using var deadline = CreateDeadline(ct);
        ct = deadline.Token;
        var collection = config.Collection();
        using var request = Request(config, new HttpMethod("PROPFIND"), collection);
        request.Headers.Add("Depth", "1");
        request.Content = new StringContent("<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/></d:prop></d:propfind>", Encoding.UTF8, "application/xml");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Require(response, HttpStatusCode.MultiStatus);
        var bytes = await ReadAsync(response, ct);
        using var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = BackupBundle.MaximumBytes });
        var xml = XDocument.Load(reader);
        XNamespace dav = "DAV:";
        if (xml.Root?.Name != dav + "multistatus") throw new InvalidDataException("PROPFIND 响应不是 DAV multistatus");
        var entries = new List<string>();
        foreach (var item in xml.Root.Elements(dav + "response"))
        {
            var href = item.Element(dav + "href")?.Value ?? throw new InvalidDataException("DAV 响应缺少 href");
            var uri = new Uri(collection, href);
            if (uri.GetLeftPart(UriPartial.Authority) != collection.GetLeftPart(UriPartial.Authority) || !uri.AbsolutePath.StartsWith(collection.AbsolutePath, StringComparison.Ordinal))
                throw new InvalidDataException("DAV 返回了集合以外的地址");
            var propstats = item.Elements(dav + "propstat").ToArray();
            if (propstats.Length == 0) throw new InvalidDataException("DAV 响应缺少 propstat");
            foreach (var propstat in propstats)
            {
                var status = propstat.Element(dav + "status")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (status is null || status.Length < 2 || status[1] != "200") throw new InvalidDataException("DAV 属性读取失败");
                var resource = propstat.Element(dav + "prop")?.Element(dav + "resourcetype") ?? throw new InvalidDataException("DAV 缺少资源类型");
                if (resource.Element(dav + "collection") is not null) continue;
                var name = uri.AbsolutePath[collection.AbsolutePath.Length..];
                if (name == FileName) entries.Add(FileName);
            }
        }
        return entries.Distinct().ToArray();
    }

    public async Task UploadAsync(BackupWebDavConfiguration config, byte[] backup, bool overwriteConfirmed, CancellationToken ct = default)
    {
        using var deadline = CreateDeadline(ct);
        ct = deadline.Token;
        _ = BackupBundle.Decode(backup);
        using var request = Request(config, HttpMethod.Put, new Uri(config.Collection(), FileName));
        if (!overwriteConfirmed) request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        request.Content = new ByteArrayContent(backup);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Require(response, HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.NoContent);
    }

    public async Task<byte[]> DownloadAsync(BackupWebDavConfiguration config, CancellationToken ct = default)
    {
        using var deadline = CreateDeadline(ct);
        ct = deadline.Token;
        using var request = Request(config, HttpMethod.Get, new Uri(config.Collection(), FileName));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Require(response, HttpStatusCode.OK);
        var bytes = await ReadAsync(response, ct);
        _ = BackupBundle.Decode(bytes);
        return bytes;
    }

    private CancellationTokenSource CreateDeadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = http.Timeout == Timeout.InfiniteTimeSpan || http.Timeout > TimeSpan.FromSeconds(30)
            ? TimeSpan.FromSeconds(30) : http.Timeout;
        deadline.CancelAfter(timeout);
        return deadline;
    }

    private static HttpRequestMessage Request(BackupWebDavConfiguration config, HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(config.Username + ":" + config.Password)));
        return request;
    }
    private static void Require(HttpResponseMessage response, params HttpStatusCode[] allowed)
    {
        if (!allowed.Contains(response.StatusCode)) throw new BackupWebDavException((int)response.StatusCode);
    }
    private static async Task<byte[]> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > BackupBundle.MaximumBytes) throw new InvalidDataException("WebDAV 响应超过 16 MiB");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await BackupBundle.ReadBoundedAsync(stream, ct);
    }
}
