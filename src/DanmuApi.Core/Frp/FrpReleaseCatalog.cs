using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DanmuApi.Core.Frp;

/// <summary>一次可安装的 frp 发行资产（版本 + 本机架构对应的 zip）。</summary>
public sealed record FrpReleaseAsset(string Version, string Architecture, string FileName)
{
    public Uri DownloadUri => new(
        $"https://github.com/{FrpReleaseCatalog.Repository}/releases/download/v{Version}/{FileName}",
        UriKind.Absolute);

    public Uri ChecksumsUri => new(
        $"https://github.com/{FrpReleaseCatalog.Repository}/releases/download/v{Version}/{FrpReleaseCatalog.ChecksumsFileName}",
        UriKind.Absolute);
}

/// <summary>
/// frp 发行资产的命名与取值规则。
///
/// 架构映射只认 frp 真的发布过的两种 Windows 资产：实测 v0.71.0 的资产列表只有
/// <c>frp_0.71.0_windows_amd64.zip</c> 与 <c>frp_0.71.0_windows_arm64.zip</c>；
/// 最后一个带 <c>windows_386</c> 的版本是 v0.51.3，而 v0.51.3 会以
/// <c>section "common" does not exist</c> 拒绝 TOML 配置（v0.52.0 才引入 TOML 并移除 INI）。
/// 因此 32 位宿主**没有**可用的 frp，这里显式拒绝，而不是挑一个旧版本硬塞一份另一种格式的配置。
/// </summary>
public static class FrpReleaseCatalog
{
    public const string Repository = "fatedier/frp";
    public const string ChecksumsFileName = "frp_sha256_checksums.txt";

    private static readonly Regex VersionPattern = new(
        @"^v?(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>把宿主进程架构映射成 frp 资产里的架构名。</summary>
    public static string ResolveArchitecture(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => throw new NotSupportedException(
            "frp 上游自 v0.52.0 起不再发布 32 位 Windows 版本（最后一个 32 位版本 v0.51.3 只支持已移除的 INI 配置），"
            + "因此本机 x86 架构无法安装 frp。请改用 64 位版本的弹幕 API。"),
        _ => throw new NotSupportedException($"暂不支持在当前处理器架构（{architecture}）上运行 frp"),
    };

    /// <summary>规整并校验版本号（接受 <c>v0.71.0</c> 与 <c>0.71.0</c> 两种写法）。</summary>
    public static string NormalizeVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var match = VersionPattern.Match(version.Trim());
        if (!match.Success)
        {
            throw new FormatException($"frp 版本号格式无效: {version}（应形如 0.71.0）");
        }

        return $"{match.Groups["major"].Value}.{match.Groups["minor"].Value}.{match.Groups["patch"].Value}";
    }

    public static FrpReleaseAsset ForVersion(string version, Architecture architecture)
    {
        var normalized = NormalizeVersion(version);
        var arch = ResolveArchitecture(architecture);
        return new FrpReleaseAsset(normalized, arch, $"frp_{normalized}_windows_{arch}.zip");
    }

}

/// <summary>GitHub 上 frp 最新发行版的查询结果（只保留最新版本号与本机架构资产是否齐备）。</summary>
public sealed record FrpLatestRelease(string Version, IReadOnlyList<string> WindowsAssets)
{
    public bool HasAssetFor(string architecture) =>
        WindowsAssets.Any(name => name.EndsWith($"_windows_{architecture}.zip", StringComparison.Ordinal));

    public string DescribeAvailableAssets() => WindowsAssets.Count == 0
        ? "该发行版没有任何 Windows 资产"
        : $"该发行版提供：{string.Join("、", WindowsAssets)}";
}

/// <summary>
/// 查询 frp 最新发行版。走 <c>api.github.com</c>，因此按仓库既有策略挂用户 Token
/// （匿名 60/小时 会先耗尽，与核心更新检查是同一个配额桶）。
/// </summary>
public sealed class FrpReleaseDiscovery
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private static readonly Uri LatestReleaseUri = new(
        $"https://api.github.com/repos/{FrpReleaseCatalog.Repository}/releases/latest",
        UriKind.Absolute);

    private readonly HttpClient _httpClient;
    private readonly IGithubTokenProvider? _tokenProvider;
    private readonly TimeSpan _timeout;

    public FrpReleaseDiscovery(
        HttpClient httpClient,
        IGithubTokenProvider? tokenProvider = null,
        TimeSpan? timeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenProvider = tokenProvider;
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
        if (_timeout <= TimeSpan.Zero || _timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    public async Task<FrpLatestRelease> GetLatestAsync(
        Architecture architecture,
        CancellationToken cancellationToken = default)
    {
        // 先做架构映射：x86 宿主在这里就该拿到明确结论，不必先发一次网络请求。
        var arch = FrpReleaseCatalog.ResolveArchitecture(architecture);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
        request.Headers.UserAgent.ParseAdd("DanmuApiWindows/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        var token = GithubTokenPolicy.Read(_tokenProvider);
        if (token.Length > 0 && GithubTokenPolicy.CanAttach(LatestReleaseUri))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"查询 frp 最新版本超过 {_timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} 秒未响应", error);
        }
        catch (HttpRequestException error)
        {
            throw new IOException($"查询 frp 最新版本失败：{error.Message}", error);
        }

        using (response)
        {
            var body = await ReadBodyAsync(response, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var hint = response.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "（GitHub 接口配额可能已用尽，可在 设置 > 网络 配置 Token 后重试）"
                    : string.Empty;
                throw new IOException($"查询 frp 最新版本失败：HTTP {(int)response.StatusCode}{hint}；响应：{Trim(body)}");
            }

            var release = Parse(body);
            if (!release.HasAssetFor(arch))
            {
                throw new IOException(
                    $"frp {release.Version} 没有适用于本机架构（{arch}）的 Windows 包。{release.DescribeAvailableAssets()}");
            }

            return release;
        }
    }

    /// <summary>严格解析：<c>tag_name</c> 必须是可识别的版本号，<c>assets[].name</c> 必须是字符串。</summary>
    public static FrpLatestRelease Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            throw new IOException("frp 最新版本响应不是 JSON 对象");
        }

        if (!root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            throw new IOException($"frp 最新版本响应缺少 tag_name：{Trim(json)}");
        }

        string version;
        try
        {
            version = FrpReleaseCatalog.NormalizeVersion(tag.GetString() ?? string.Empty);
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        {
            throw new IOException($"frp 最新版本响应里的 tag_name 无法识别：{tag.GetString()}", error);
        }

        var assets = new List<string>();
        if (root.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in assetArray.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !item.TryGetProperty("name", out var name)
                    || name.ValueKind != System.Text.Json.JsonValueKind.String)
                {
                    throw new IOException($"frp 最新版本响应的 assets 元素格式无效：{Trim(item.GetRawText())}");
                }

                var fileName = name.GetString() ?? string.Empty;
                if (fileName.Length == 0)
                {
                    throw new IOException("frp 最新版本响应的 assets 元素 name 为空");
                }

                if (fileName.Contains("_windows_", StringComparison.Ordinal))
                {
                    assets.Add(fileName);
                }
            }
        }

        return new FrpLatestRelease(version, assets);
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[64 * 1024];
        using var memory = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (memory.Length + read > MaxResponseBytes)
            {
                throw new IOException($"frp 最新版本响应超过 {MaxResponseBytes.ToString(CultureInfo.InvariantCulture)} 字节上限");
            }

            memory.Write(buffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    private static string Trim(string value) => value.Length <= 500 ? value : value[..500] + "…";
}
