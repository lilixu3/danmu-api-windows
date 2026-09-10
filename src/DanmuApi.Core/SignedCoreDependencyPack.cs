using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DanmuApi.Core;

internal sealed record DependencyPackPackage(string Name, string Version, string Path);
internal sealed record DependencyPackManifest(int Schema, long Serial, int RuntimeProtocol, int NodeMajor,
    string ArtifactUrl, string ArtifactSha256, long ArtifactSize, long ArtifactExtractedSize, int ArtifactFileCount,
    string RuntimeLockSha256, string DependencyFingerprint, Dictionary<string, string> Dependencies,
    DependencyPackPackage[] Packages);

internal sealed class SignedCoreDependencyPack(HttpClient http, string publicKey)
{
    internal const string Repository = "lilixu3/danmu-api-runtime-packs";
    internal const string ManifestUrl = "https://raw.githubusercontent.com/" + Repository + "/main/manifest.json";
    internal const string SignatureUrl = "https://raw.githubusercontent.com/" + Repository + "/main/manifest.sig";
    private const string ArtifactPrefix = "https://github.com/" + Repository + "/releases/download/";
    internal const long MaxArchiveBytes = 64L * 1024 * 1024;
    internal const long MaxExtractedBytes = 128L * 1024 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    internal const string TrustedPublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAw23l6/+FdYKWvwIVuczi
        ZPmPRLDXCqKjWzarqQhwjORb6/NneAYfqkzN1TnqBRZcuxESpQhdbLWfZaoUhqjX
        xCEC2J77zzchdDi+5P5RZ0HD+vLNMmDmH8ut+zBD/77dzzMYHe99AoPkUJs8Zd9W
        MbEdt4J/jmIPky7abnQi0snnMpJWZ1tZcdUqBisHj/5k30vWVTMlk/RQlvDZergf
        DzD3/dkAT847chGNIO3QFBa5DXOogJOIfeBtCwahkpEnCoNoB1NotuJPd4Ye05G6
        qN4+0HJxeUU7siHd4OsXGuDxtm6Ay/HqSSqSZx+ow/x8qhEdtQDSEhNUamblR8qL
        x5FeWN8B08rml+8AFQSBWvO7y7VFChu6t37fGuxjXqdgdqUjJwA1zy5toj5MRjSq
        VR4s8t3BGZrBEUc5WgerO9t26NlTIq6qpptdCPqh9TlanBVh0HGiV0/oNM0TU/N/
        VUsmyyO7hViS/U7pwIdYiXT0+rvwwcyLhWyzUJjI+2clAgMBAAE=
        -----END PUBLIC KEY-----
        """;

    internal async Task<(DependencyPackManifest Manifest, byte[] Bytes)> FetchManifestAsync(long highestSerial, CancellationToken ct)
    {
        var bytes = await GetAsync(new Uri(ManifestUrl), 1024 * 1024, null, ct).ConfigureAwait(false);
        var signature = await GetAsync(new Uri(SignatureUrl), 16 * 1024, null, ct).ConfigureAwait(false);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKey);
        if (!rsa.VerifyData(bytes, Convert.FromBase64String(Encoding.UTF8.GetString(signature).Trim()), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("运行时依赖清单签名校验失败");
        RejectDuplicateProperties(bytes);
        var manifest = JsonSerializer.Deserialize<DependencyPackManifest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("签名依赖清单为空");
        if (manifest.Schema != 3 || manifest.RuntimeProtocol != 2 || manifest.NodeMajor != 24)
            throw new InvalidDataException("签名依赖包 schema/运行协议/Node major 不兼容");
        if (manifest.Serial < Math.Max(26, highestSerial)) throw new InvalidDataException("签名依赖清单 serial 回退或低于受信最低版本 26");
        if (manifest.ArtifactSize is <= 0 or > MaxArchiveBytes || manifest.ArtifactExtractedSize is <= 0 or > MaxExtractedBytes || manifest.ArtifactFileCount is <= 0 or > 20_000)
            throw new InvalidDataException("签名依赖包大小或文件数超过配额");
        ValidateHash(manifest.ArtifactSha256); ValidateHash(manifest.RuntimeLockSha256); ValidateHash(manifest.DependencyFingerprint);
        if (manifest.Dependencies is null || manifest.Packages is null || manifest.Packages.Length is 0 or > 2000)
            throw new InvalidDataException("签名依赖包缺少依赖及包清单");
        var canonical = "{" + string.Join(",", manifest.Dependencies.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x =>
            JsonSerializer.Serialize(x.Key, CanonicalJson) + ":" + JsonSerializer.Serialize(x.Value, CanonicalJson))) + "}";
        if (Hash(Encoding.UTF8.GetBytes(canonical)) != manifest.DependencyFingerprint)
            throw new InvalidDataException("依赖清单 fingerprint 不一致");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in manifest.Packages)
        {
            ValidatePackageName(package.Name);
            ValidateArchivePath(package.Path, false);
            if (!package.Path.EndsWith("node_modules/" + package.Name, StringComparison.Ordinal) || !paths.Add(package.Path) ||
                !Regex.IsMatch(package.Version ?? "", @"^\d+\.\d+\.\d+(?:\+[0-9A-Za-z.-]+)?$"))
                throw new InvalidDataException("签名依赖包名称/版本/路径重复或无效");
        }
        ValidateArtifactUri(manifest.ArtifactUrl);
        return (manifest, bytes);
    }

    private static readonly JsonSerializerOptions CanonicalJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal async Task ExtractAsync(DependencyPackManifest manifest, string destination,
        IProgress<CoreDependencyRepairProgress>? progress, CancellationToken ct)
    {
        var bytes = await GetAsync(new Uri(manifest.ArtifactUrl), manifest.ArtifactSize, progress, ct).ConfigureAwait(false);
        if (bytes.LongLength != manifest.ArtifactSize || Hash(bytes) != manifest.ArtifactSha256)
            throw new InvalidDataException("依赖 ZIP hash/size 校验失败");
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (archive.Entries.Count is 0 or > 20_000) throw new InvalidDataException("依赖 ZIP 条目数量超过配额");
        // Validate the complete archive before the first extraction write.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var casing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long size = 0; var count = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var directory = entry.FullName.EndsWith('/');
            ValidateArchivePath(entry.FullName, directory);
            var mode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (mode is not (0 or 0x4000 or 0x8000) || (entry.ExternalAttributes & 0x400) != 0)
                throw new InvalidDataException("依赖 ZIP 禁止链接/重解析点/特殊文件");
            var name = entry.FullName.TrimEnd('/');
            if (!names.Add(name)) throw new InvalidDataException("依赖 ZIP 存在重复路径或大小写冲突");
            var segments = name.Split('/');
            for (var i = 1; i <= segments.Length; i++)
            {
                var prefix = string.Join('/', segments.Take(i));
                if (casing.TryGetValue(prefix, out var existing) && existing != prefix)
                    throw new InvalidDataException("依赖 ZIP 目录大小写冲突");
                casing[prefix] = prefix;
            }
            if (directory) continue;
            if (!manifest.Packages.Any(p => name.StartsWith(p.Path + "/", StringComparison.Ordinal)))
                throw new InvalidDataException("依赖 ZIP 文件不属于签名包清单");
            if (IsNative(name)) throw new InvalidDataException("依赖 ZIP 包含原生模块或安装构建文件");
            size = checked(size + entry.Length); count++;
            if (size > MaxExtractedBytes) throw new InvalidDataException("依赖 ZIP 解压大小超过配额");
        }
        if (size != manifest.ArtifactExtractedSize || count != manifest.ArtifactFileCount)
            throw new InvalidDataException("依赖 ZIP 解压大小/文件数不符签名清单");
        foreach (var entry in archive.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920]; long written = 0; int read;
            while ((read = source.Read(buffer)) != 0)
            {
                ct.ThrowIfCancellationRequested();
                written += read;
                if (written > entry.Length) throw new InvalidDataException("依赖 ZIP 实际解压大小超限");
                if (written == read && read >= 2 && buffer[0] == 'M' && buffer[1] == 'Z')
                    throw new InvalidDataException("依赖 ZIP 包含 PE 可执行内容");
                if (written == read && read >= 4 && ((buffer[0] == 0x7f && buffer[1] == 'E' && buffer[2] == 'L' && buffer[3] == 'F') ||
                    (buffer[0] == 0xfe && buffer[1] == 0xed && buffer[2] == 0xfa) ||
                    (buffer[0] == 0xcf && buffer[1] == 0xfa && buffer[2] == 0xed && buffer[3] == 0xfe) ||
                    (buffer[0] == 0xca && buffer[1] == 0xfe && buffer[2] == 0xba && buffer[3] == 0xbe)))
                    throw new InvalidDataException("依赖 ZIP 包含 ELF/Mach-O 原生内容");
                output.Write(buffer, 0, read);
            }
            if (written != entry.Length) throw new InvalidDataException("依赖 ZIP 条目被截断");
        }
        foreach (var package in manifest.Packages)
        {
            var file = Path.Combine(destination, package.Path.Replace('/', Path.DirectorySeparatorChar), "package.json");
            using var meta = JsonDocument.Parse(File.ReadAllBytes(file));
            if (meta.RootElement.GetProperty("name").GetString() != package.Name || meta.RootElement.GetProperty("version").GetString() != package.Version)
                throw new InvalidDataException("解压包名称/版本与签名清单不符");
            ValidatePlatform(meta.RootElement, "os", "win32");
            ValidatePlatform(meta.RootElement, "cpu", "x64");
            if (meta.RootElement.TryGetProperty("scripts", out var scripts))
            {
                if (scripts.ValueKind != JsonValueKind.Object) throw new InvalidDataException("依赖包 scripts 类型无效");
                foreach (var lifecycle in new[] { "preinstall", "install", "postinstall" })
                    if (scripts.TryGetProperty(lifecycle, out _)) throw new InvalidDataException("依赖包需要安装脚本，不能安全应用裁剪签名包");
            }
        }
    }

    private static void ValidatePlatform(JsonElement package, string key, string current)
    {
        if (!package.TryGetProperty(key, out var value)) return;
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("依赖包平台限制类型无效");
        var platforms = value.EnumerateArray().Select(v => v.GetString() ?? throw new InvalidDataException("依赖包平台值无效")).ToArray();
        if (platforms.Contains("!" + current) || (platforms.Any(v => !v.StartsWith('!')) && !platforms.Contains(current) && !platforms.Contains("any")))
            throw new InvalidDataException("依赖包不支持 Windows x64");
    }

    internal static void ValidatePackageName(string name)
    {
        if (!Regex.IsMatch(name ?? "", @"^(?:@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*$"))
            throw new InvalidDataException("非法依赖包名称");
    }
    internal static void ValidateArchivePath(string path, bool directory)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || (!path.StartsWith("node_modules/", StringComparison.Ordinal) && path != "node_modules/"))
            throw new InvalidDataException("依赖 ZIP 路径必须位于 node_modules");
        foreach (var segment in (directory ? path[..^1] : path).Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(c => c < 32 || "<>:\"|?*".Contains(c)) ||
                Regex.IsMatch(segment, @"^(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase))
                throw new InvalidDataException("依赖 ZIP 包含 Windows 非法路径/ADS/设备名");
        }
        if (path.Length > 1024) throw new InvalidDataException("依赖 ZIP 路径过长");
    }
    private static bool IsNative(string p) => Regex.IsMatch(p, @"(?:\.(?:node|so|dll|dylib|exe)$|/binding\.gyp$|/prebuilds/)", RegexOptions.IgnoreCase);
    private static void ValidateHash(string hash)
    {
        if (!Regex.IsMatch(hash ?? "", "^[0-9a-f]{64}$")) throw new InvalidDataException("签名依赖包 SHA256 字段无效");
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void ValidateArtifactUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !value.StartsWith(ArtifactPrefix, StringComparison.Ordinal) ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 || value.Contains('%') ||
            !Regex.IsMatch(value[ArtifactPrefix.Length..], @"^[A-Za-z0-9._-]+/node_modules\.zip$"))
            throw new InvalidDataException("签名包下载地址不属于固定受信仓库");
    }
    private async Task<byte[]> GetAsync(Uri uri, long maximum, IProgress<CoreDependencyRepairProgress>? progress, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(150));
        var current = uri;
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("DanmuApi-Windows");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var next = response.Headers.Location is { } location ? new Uri(current, location) : throw new IOException("签名包重定向缺少 Location");
                if (uri.Host != "github.com" || next.Scheme != "https" || next.Host != "release-assets.githubusercontent.com" || next.UserInfo.Length != 0 || !next.IsDefaultPort)
                    throw new IOException("签名包下载重定向至未授权来源");
                current = next; continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("签名依赖包响应超过大小上限");
            using var output = new MemoryStream();
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[81920]; int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > maximum) throw new InvalidDataException("签名依赖包响应超过大小上限");
                output.Write(buffer, 0, count);
                progress?.Report(new("Downloading", "正在下载受信签名依赖包", output.Length, maximum));
            }
            if (output.Length == 0 || (response.Content.Headers.ContentLength is { } length && output.Length != length))
                throw new InvalidDataException("签名依赖包响应为空或被截断");
            return output.ToArray();
        }
        throw new IOException("签名依赖包重定向次数超过上限");
    }
    private static void RejectDuplicateProperties(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes);
        var stack = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) stack.Push(new(StringComparer.OrdinalIgnoreCase));
            else if (reader.TokenType == JsonTokenType.EndObject) stack.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !stack.Peek().Add(reader.GetString()!))
                throw new InvalidDataException("签名清单包含重复字段");
        }
    }
}
