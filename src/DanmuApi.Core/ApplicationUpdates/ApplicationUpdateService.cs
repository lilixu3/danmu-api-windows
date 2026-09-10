using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace DanmuApi.Core.ApplicationUpdates;

public sealed record ApplicationUpdateAsset(string Name, long Size, string Sha256, string Kind);
public sealed record ApplicationUpdateManifest(int SchemaVersion, string Product, string Version, string Channel, string Architecture, IReadOnlyList<ApplicationUpdateAsset> Assets);
public sealed record ApplicationUpdateProgress(long BytesReceived, long TotalBytes);

/// <summary>Created only after signature verification; download URLs are bound to this release.</summary>
public sealed class ApplicationUpdate
{
    public ApplicationUpdateManifest Manifest { get; }
    internal IReadOnlyDictionary<string, Uri> Urls { get; }
    private readonly byte[] manifestBytes;
    private readonly byte[] signatureBytes;
    public string ReleaseNotes { get; }
    public DateTimeOffset PublishedAt { get; }
    /// <summary>A defensive copy of the authenticated, unmodified manifest bytes.</summary>
    public byte[] ManifestBytes => manifestBytes.ToArray();
    /// <summary>A defensive copy of the raw detached RSA signature.</summary>
    public byte[] SignatureBytes => signatureBytes.ToArray();
    internal ApplicationUpdate(ApplicationUpdateManifest manifest, IReadOnlyDictionary<string, Uri> urls, string releaseNotes, DateTimeOffset publishedAt, byte[] manifestBytes, byte[] signatureBytes)
    {
        Manifest = manifest; Urls = urls; ReleaseNotes = releaseNotes; PublishedAt = publishedAt;
        this.manifestBytes = manifestBytes.ToArray(); this.signatureBytes = signatureBytes.ToArray();
    }
}

/// <summary>Public preview feed. Signature asset is raw RSA signature over exact manifest bytes.
/// DER key is SubjectPublicKeyInfo. Injected handlers must disable automatic redirects (intended for tests).
/// Owns the handler. No credentials, cookies, token, or ambient HttpClient headers are used.</summary>
public sealed class ApplicationUpdateService : IDisposable
{
    public const string ManifestFileName = "update-manifest.json";
    public const string SignatureFileName = "update-manifest.json.sig";
    public const int MaximumManifestBytes = 1024 * 1024;
    public const long MaximumPackageBytes = 512L * 1024 * 1024;
    private const string Feed = "https://api.github.com/repos/lilixu3/danmu-api-windows/releases?per_page=100&page=";
    private readonly HttpClient client;
    private readonly byte[] key;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, (string? ETag, byte[] Body)> cache = new();
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase) { "api.github.com", "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com" };

    public ApplicationUpdateService(byte[] publicKeyDer, HttpMessageHandler? handler = null, TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(publicKeyDer);
        key = publicKeyDer.ToArray();
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(key, out var read);
        if (read != key.Length || rsa.KeySize < 2048) throw new CryptographicException("Expected a complete RSA public key of at least 2048 bits.");
        timeout = operationTimeout ?? TimeSpan.FromMinutes(10);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        if (handler is HttpClientHandler http && http.AllowAutoRedirect) throw new ArgumentException("Automatic redirects must be disabled.", nameof(handler));
        if (handler is SocketsHttpHandler sockets && sockets.AllowAutoRedirect) throw new ArgumentException("Automatic redirects must be disabled.", nameof(handler));
        client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, Credentials = null }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<ApplicationUpdate?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default) => TimedAsync(async ct =>
    {
        var current = SemanticVersion.Parse(currentVersion);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            JsonElement? selected = null;
            SemanticVersion? best = null;
            for (var page = 1; ; page++)
            {
                if (page > 100) throw new InvalidDataException("Release list exceeds 100 pages.");
                var url = Feed + page;
                cache.TryGetValue(url, out var old);
                using var response = await SendAsync(new Uri(url), old.ETag, ct).ConfigureAwait(false);
                byte[] body;
                if (response.StatusCode == HttpStatusCode.NotModified)
                    body = old.Body ?? throw new InvalidDataException("Received 304 without cached release list.");
                else
                {
                    response.EnsureSuccessStatusCode();
                    body = await ReadBoundedAsync(response.Content, 4 * MaximumManifestBytes, ct).ConfigureAwait(false);
                }
                using var doc = ParseJson(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Release list must be an array.");
                foreach (var release in root.EnumerateArray())
                {
                    if (release.GetProperty("draft").GetBoolean()) continue;
                    _ = release.GetProperty("prerelease").GetBoolean();
                    var tag = Text(release, "tag_name");
                    var version = SemanticVersion.Parse(tag.StartsWith('v') ? tag[1..] : tag);
                    if (version.CompareTo(current) > 0 && (best is null || version.CompareTo(best) > 0)) { selected = release.Clone(); best = version; }
                }
                cache[url] = (response.StatusCode == HttpStatusCode.NotModified ? response.Headers.ETag?.ToString() ?? old.ETag : response.Headers.ETag?.ToString(), body);
                if (root.GetArrayLength() < 100) break;
            }
            if (selected is null) return null;
            var urls = new Dictionary<string, Uri>(StringComparer.Ordinal);
            foreach (var asset in selected.Value.GetProperty("assets").EnumerateArray())
            {
                var uri = new Uri(Text(asset, "browser_download_url"), UriKind.Absolute);
                ValidateUri(uri);
                if (!urls.TryAdd(Text(asset, "name"), uri)) throw new InvalidDataException("Duplicate release asset name.");
            }
            if (!urls.TryGetValue(ManifestFileName, out var manifestUrl) || !urls.TryGetValue(SignatureFileName, out var signatureUrl)) throw new InvalidDataException("Release is missing signed manifest assets.");
            var bytes = await GetBytesAsync(manifestUrl, MaximumManifestBytes, ct).ConfigureAwait(false);
            var signature = await GetBytesAsync(signatureUrl, 16384, ct).ConfigureAwait(false);
            var manifest = VerifyManifest(bytes, signature);
            if (!string.Equals(manifest.Version, best!.Value, StringComparison.Ordinal)) throw new InvalidDataException("Manifest version differs from release tag.");
            foreach (var asset in manifest.Assets) if (!urls.ContainsKey(asset.Name)) throw new InvalidDataException($"Missing authenticated asset: {asset.Name}");
            var notes = selected.Value.GetProperty("body");
            var releaseNotes = notes.ValueKind == JsonValueKind.Null ? string.Empty : notes.GetString()!;
            var publishedAt = selected.Value.GetProperty("published_at").GetDateTimeOffset();
            return new ApplicationUpdate(manifest, urls, releaseNotes, publishedAt, bytes, signature);
        }
        finally { gate.Release(); }
    }, cancellationToken);

    public ApplicationUpdateManifest VerifyManifest(byte[] manifestBytes, byte[] detachedSignature)
    {
        if (manifestBytes.Length > MaximumManifestBytes) throw new InvalidDataException("Manifest exceeds 1 MiB.");
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(key, out _);
        if (!rsa.VerifyData(manifestBytes, detachedSignature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new CryptographicException("Manifest signature is invalid.");
        using var doc = ParseJson(manifestBytes);
        var root = doc.RootElement;
        ExactProperties(root, "schemaVersion", "product", "version", "channel", "architecture", "assets");
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || Text(root, "product") != "DanmuApi.Windows" || Text(root, "channel") != "preview" || Text(root, "architecture") != "win-x64") throw new InvalidDataException("Unsupported manifest schema, product, channel, or architecture.");
        var version = SemanticVersion.Parse(Text(root, "version"));
        var assets = new List<ApplicationUpdateAsset>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in root.GetProperty("assets").EnumerateArray())
        {
            ExactProperties(item, "name", "size", "sha256", "kind");
            var name = Text(item, "name"); var kind = Text(item, "kind"); var hash = Text(item, "sha256"); var size = item.GetProperty("size").GetInt64();
            if (name.Length is 0 or > 200 || name is "." or ".." || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) || !names.Add(name)) throw new InvalidDataException("Invalid or duplicate asset name.");
            if (kind is not ("installer" or "portable") || size <= 0 || size > MaximumPackageBytes || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid asset kind, size, or SHA256.");
            assets.Add(new(name, size, hash.ToLowerInvariant(), kind));
        }
        if (assets.Count == 0) throw new InvalidDataException("Manifest has no assets.");
        return new(1, "DanmuApi.Windows", version.Value, "preview", "win-x64", assets.AsReadOnly());
    }

    public Task<string> DownloadAsync(ApplicationUpdate update, string assetName, string destinationPath, IProgress<ApplicationUpdateProgress>? progress = null, CancellationToken cancellationToken = default) => TimedAsync(async ct =>
    {
        ArgumentNullException.ThrowIfNull(update);
        var asset = update.Manifest.Assets.Single(a => a.Name == assetName);
        var destination = Path.GetFullPath(destinationPath);
        var part = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var response = await SendAsync(update.Urls[assetName], null, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != asset.Size) throw new InvalidDataException("Package Content-Length differs from signed size.");
            await using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920]; long total = 0;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (count == 0) break;
                    total += count;
                    if (total > asset.Size || total > MaximumPackageBytes) throw new InvalidDataException("Package exceeds signed size.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    progress?.Report(new(total, asset.Size));
                }
                if (total != asset.Size) throw new InvalidDataException("Package is truncated.");
                if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(asset.Sha256))) throw new CryptographicException("Package SHA256 differs from signed manifest.");
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(part, destination, false);
            return destination;
        }
        catch (Exception failure)
        {
            try { File.Delete(part); }
            catch (Exception cleanup) { throw new AggregateException("Download failed and partial file cleanup failed.", failure, cleanup); }
            throw;
        }
    }, cancellationToken);

    private async Task<T> TimedAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try { return await operation(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested) { throw new TimeoutException($"Application update operation exceeded {timeout}.", ex); }
    }
    private static void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !Hosts.Contains(uri.IdnHost)) throw new InvalidDataException("Update URL must use HTTPS on an allowed GitHub host.");
    }
    private async Task<HttpResponseMessage> SendAsync(Uri uri, string? etag, CancellationToken ct)
    {
        for (var hop = 0; hop <= 5; hop++)
        {
            ValidateUri(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("DanmuApi.Windows/1.0");
            if (etag is not null) request.Headers.IfNoneMatch.ParseAdd(etag);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("Redirect has no Location header.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                etag = null;
                continue;
            }
            return response;
        }
        throw new HttpRequestException("Update redirect limit exceeded.");
    }
    private async Task<byte[]> GetBytesAsync(Uri uri, int maximum, CancellationToken ct)
    {
        using var response = await SendAsync(uri, null, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadBoundedAsync(response.Content, maximum, ct).ConfigureAwait(false);
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximum) throw new InvalidDataException("Response exceeds allowed size.");
        await using var input = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximum) throw new InvalidDataException("Response exceeds allowed size.");
            output.Write(buffer, 0, count);
        }
    }
    private static JsonDocument ParseJson(byte[] bytes)
    {
        var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
        try { CheckDuplicates(doc.RootElement); return doc; }
        catch { doc.Dispose(); throw; }
    }
    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in element.EnumerateObject()) { if (!seen.Add(p.Name)) throw new InvalidDataException($"Duplicate JSON property: {p.Name}"); CheckDuplicates(p.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) CheckDuplicates(item);
    }
    private static void ExactProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(names)) throw new InvalidDataException("Unexpected or missing manifest properties.");
    }
    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString() ?? throw new InvalidDataException($"Null property: {name}");
    public void Dispose() { client.Dispose(); gate.Dispose(); }
}
