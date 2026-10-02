using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using DanmuApi.Core;
using DanmuApi.Core.Frp;

namespace DanmuApi.Platform.Frp;

/// <summary>一次安装/更新尝试的结果。失败时 <see cref="Diagnostic"/> 必须能说明停在哪一步。</summary>
public sealed record FrpInstallResult(bool Succeeded, string Version, string BinaryDirectory, string Diagnostic)
{
    public static FrpInstallResult Failure(string diagnostic) => new(false, string.Empty, string.Empty, diagnostic);
}

public interface IFrpBinaryInstaller
{
    /// <summary>已安装的版本（读自 <c>frp\bin</c> 下真实存在的目录），未安装时返回 null。</summary>
    string? InstalledVersion { get; }

    /// <summary>二进制安装根目录（界面用来显示/打开）。</summary>
    string BinaryDirectory { get; }

    /// <summary>指定版本的 frpc.exe / frps.exe 是否已经就位且架构正确。</summary>
    bool IsInstalled(string version);

    string ExecutablePath(string version, bool serverRole);

    Task<FrpInstallResult> InstallAsync(
        string version,
        IProgress<GithubDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>删除除 <paramref name="keepVersion"/> 以外的旧版本目录；返回被删除的版本。</summary>
    IReadOnlyList<string> RemoveOtherVersions(string keepVersion);
}

/// <summary>
/// 安装 frp 发行包。整条链路每一步都有独立证据，任一步不过就停：
///
///  1. 从 <b>官方</b> <c>frp_sha256_checksums.txt</c> 取该 zip 的期望 SHA256，清单里没有这个文件就失败；
///  2. 下载 zip（走用户已确认的 GitHub 线路），对下载结果做 SHA256 比对，不匹配直接删掉重下/失败；
///  3. 解压到暂存目录，用 PE 头确认 frpc.exe / frps.exe 的机器类型与本机进程架构一致；
///  4. 原子换入 <c>frp\bin\&lt;version&gt;</c>，旧版本目录保留（可回退），由调用方决定何时清理。
///
/// 第 3 步不是多余的：zip 内容与架构不一致时（例如用户手工放了个别的包），
/// 错误要到"进程起不来"才暴露，而那时用户拿到的是 Windows 的 0xc000007b 之类的信息。
/// </summary>
public sealed class FrpBinaryInstaller : IFrpBinaryInstaller
{
    private readonly AppPaths _paths;
    private readonly IGithubFileDownloader _downloader;
    private readonly HttpClient _httpClient;
    private readonly Action<string>? _report;
    private readonly TimeSpan _checksumTimeout;

    public FrpBinaryInstaller(
        AppPaths paths,
        IGithubFileDownloader downloader,
        HttpClient httpClient,
        Action<string>? diagnosticSink = null,
        TimeSpan? checksumTimeout = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _report = diagnosticSink;
        _checksumTimeout = checksumTimeout ?? TimeSpan.FromSeconds(30);
    }

    public string? InstalledVersion
    {
        get
        {
            var directory = _paths.FrpBinaryDirectory;
            string[] candidates;
            try
            {
                if (!Directory.Exists(directory))
                {
                    return null;
                }

                candidates = Directory.EnumerateDirectories(directory).ToArray();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // 这是个会被界面反复读的属性，不能抛（绑定时抛异常等于整块状态消失）。
                // 但也不能悄悄当成"没装"：把原因记进诊断，启动路径随后会再如实报一次。
                _report?.Invoke($"读取 frp 安装目录失败（{directory}）：{error.Message}");
                return null;
            }

            var versions = candidates
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Where(version => !version.StartsWith(".", StringComparison.Ordinal))
                .OrderByDescending(version => version, Comparer<string>.Create(CompareVersions))
                .ToArray();
            return versions.FirstOrDefault(IsInstalled);
        }
    }

    public string BinaryDirectory => _paths.FrpBinaryDirectory;

    public bool IsInstalled(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var directory = VersionDirectory(version);
        if (!Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            VerifyExecutableArchitecture(Path.Combine(directory, "frpc.exe"));
            VerifyExecutableArchitecture(Path.Combine(directory, "frps.exe"));
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _report?.Invoke($"frp {version} 的二进制未通过架构校验：{error.Message}");
            return false;
        }
    }

    public string ExecutablePath(string version, bool serverRole)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return Path.Combine(VersionDirectory(version), serverRole ? "frps.exe" : "frpc.exe");
    }

    public async Task<FrpInstallResult> InstallAsync(
        string version,
        IProgress<GithubDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string normalized;
        FrpReleaseAsset asset;
        try
        {
            normalized = FrpReleaseCatalog.NormalizeVersion(version);
            asset = FrpReleaseCatalog.ForVersion(normalized, RuntimeInformation.ProcessArchitecture);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or NotSupportedException)
        {
            return FrpInstallResult.Failure(error.Message);
        }

        if (IsInstalled(normalized))
        {
            return new(true, normalized, VersionDirectory(normalized), $"frp {normalized} 已安装，无需重复下载");
        }

        Directory.CreateDirectory(_paths.FrpCacheDirectory);
        var archivePath = Path.Combine(_paths.FrpCacheDirectory, asset.FileName);

        string expectedHash;
        try
        {
            var checksums = await ReadChecksumsAsync(asset, cancellationToken).ConfigureAwait(false);
            expectedHash = checksums.Require(asset.FileName, asset.ChecksumsUri.ToString());
        }
        catch (Exception error) when (error is IOException or FormatException or HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return FrpInstallResult.Failure($"获取官方校验和失败：{error.Message}");
        }

        // 缓存命中也要逐次校验：缓存文件可能是上一次中断留下的残片，长度对得上不代表内容对。
        string? mismatch = File.Exists(archivePath)
            ? await FrpSha256Checksums.VerifyFileAsync(archivePath, expectedHash, cancellationToken).ConfigureAwait(false)
            : "本地没有缓存";
        if (mismatch is not null)
        {
            if (File.Exists(archivePath))
            {
                _report?.Invoke($"frp 缓存包校验未通过（{mismatch}），将重新下载：{archivePath}");
                File.Delete(archivePath);
            }

            try
            {
                await _downloader.DownloadAsync(asset.DownloadUri, string.Empty, archivePath, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or HttpRequestException or TaskCanceledException)
            {
                if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return FrpInstallResult.Failure($"下载 frp {normalized} 失败：{error.Message}");
            }

            mismatch = await FrpSha256Checksums.VerifyFileAsync(archivePath, expectedHash, cancellationToken)
                .ConfigureAwait(false);
            if (mismatch is not null)
            {
                TryDelete(archivePath);
                return FrpInstallResult.Failure(
                    $"下载到的 frp {normalized} 与官方校验和不一致（{mismatch}），已删除该文件。"
                    + "请检查网络是否被劫持或代理线路是否篡改了内容。");
            }
        }

        var staging = Path.Combine(_paths.FrpBinaryDirectory, $".staging-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);
            ExtractBinaries(archivePath, staging);
            var target = VersionDirectory(normalized);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.Move(staging, target);
            VerifyExecutableArchitecture(Path.Combine(target, "frpc.exe"));
            VerifyExecutableArchitecture(Path.Combine(target, "frps.exe"));
            return new(true, normalized, target, $"frp {normalized} 已安装（{asset.Architecture}）");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            return FrpInstallResult.Failure($"安装 frp {normalized} 失败：{error.Message}");
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                TryDeleteDirectory(staging);
            }
        }
    }

    public IReadOnlyList<string> RemoveOtherVersions(string keepVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keepVersion);
        var directory = _paths.FrpBinaryDirectory;
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var removed = new List<string>();
        foreach (var candidate in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(candidate);
            if (string.Equals(name, keepVersion, StringComparison.Ordinal)
                || name.StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                Directory.Delete(candidate, recursive: true);
                removed.Add(name);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // 删不掉只留诊断：旧版本目录不是运行依赖。
                _report?.Invoke($"清理旧版 frp 目录 {candidate} 失败: {error.Message}");
            }
        }

        return removed;
    }

    private string VersionDirectory(string version) =>
        Path.Combine(_paths.FrpBinaryDirectory, FrpReleaseCatalog.NormalizeVersion(version));

    private async Task<FrpSha256Checksums> ReadChecksumsAsync(FrpReleaseAsset asset, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_checksumTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.ChecksumsUri);
        request.Headers.UserAgent.ParseAdd("DanmuApiWindows/1.0");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new IOException($"HTTP {(int)response.StatusCode}（{asset.ChecksumsUri}）");
        }

        var content = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return FrpSha256Checksums.Parse(content, asset.ChecksumsUri.ToString());
    }

    private static void ExtractBinaries(string archivePath, string destination)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var name in new[] { "frpc.exe", "frps.exe" })
        {
            var entry = archive.Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                throw new InvalidDataException($"frp 压缩包里没有 {name}（{archivePath}）");
            }

            entry.ExtractToFile(Path.Combine(destination, name), overwrite: true);
        }
    }

    private static void VerifyExecutableArchitecture(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("frp 可执行文件不存在", path);
        }

        var expected = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 0x8664,
            Architecture.Arm64 => 0xAA64,
            Architecture.X86 => 0x014C,
            var other => throw new NotSupportedException($"不支持的处理器架构：{other}"),
        };
        var machine = ExecutableImage.Machine(path);
        if (machine != expected)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} 的 PE 架构是 {ExecutableImage.ArchitectureName(machine)}，"
                + $"与当前宿主（{RuntimeInformation.ProcessArchitecture}）不一致");
        }
    }

    private static int CompareVersions(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        for (var index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            var leftValue = index < leftParts.Length && int.TryParse(leftParts[index], out var leftNumber) ? leftNumber : 0;
            var rightValue = index < rightParts.Length && int.TryParse(rightParts[index], out var rightNumber) ? rightNumber : 0;
            if (leftValue != rightValue)
            {
                return leftValue.CompareTo(rightValue);
            }
        }

        return 0;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _report?.Invoke($"删除 frp 缓存文件失败（不影响本次结论）：{path}：{error.Message}");
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _report?.Invoke($"清理 frp 暂存目录失败（可手工删除）：{path}：{error.Message}");
        }
    }
}
