using System.Security.Cryptography;
using DanmuApi.Core.ApplicationUpdates;

namespace DanmuApi.App.Services;

internal static class ApplicationReleaseVerifier
{
    /// <summary>
    /// 发布目录里可能出现的签名清单。x64 沿用不带后缀的文件名（早于多架构支持的客户端要找它），
    /// 其它架构带后缀，所以一次发布可以并存三份。自检要把目录里**每一份**清单都验掉——
    /// 原来写死 update-manifest.json，x86 / arm64 的发布目录必然验不过。
    /// </summary>
    private static readonly (string FileName, string Architecture)[] KnownManifests =
    [
        ("update-manifest.json", "win-x64"),
        ("update-manifest-win-x64.json", "win-x64"),
        ("update-manifest-win-x86.json", "win-x86"),
        ("update-manifest-win-arm64.json", "win-arm64"),
    ];

    public static int Run(string directory)
    {
        var verified = new List<string>();
        try
        {
            var present = KnownManifests
                .Where(entry => File.Exists(Path.Combine(directory, entry.FileName)))
                .ToArray();
            if (present.Length == 0)
            {
                throw new FileNotFoundException(
                    "发布目录里没有可校验的签名清单（" + string.Join(" / ", KnownManifests.Select(entry => entry.FileName)) + "）",
                    directory);
            }

            using var service = new ApplicationUpdateService(AppUpdateTrust.PublicKey());
            foreach (var (fileName, architecture) in present)
            {
                var manifestPath = Path.Combine(directory, fileName);
                var signaturePath = manifestPath + ".sig";
                if (!File.Exists(signaturePath))
                {
                    throw new FileNotFoundException($"{fileName} 缺少配套的 {fileName}.sig", signaturePath);
                }

                var manifest = service.VerifyManifest(
                    File.ReadAllBytes(manifestPath),
                    File.ReadAllBytes(signaturePath),
                    architecture);
                foreach (var asset in manifest.Assets)
                {
                    var file = Path.Combine(directory, asset.Name);
                    using var stream = File.OpenRead(file);
                    if (stream.Length != asset.Size ||
                        !Convert.ToHexString(SHA256.HashData(stream)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("发行资产与认证清单不符：" + asset.Name);
                    }

                    if (asset.Kind == "installer")
                    {
                        AppUpdateTrust.VerifyExecutable(file);
                    }
                }

                verified.Add($"{architecture} {manifest.Version}（{fileName}）");
            }

            File.WriteAllText(
                Path.Combine(directory, "release-verification.txt"),
                "PASS · " + string.Join(" · ", verified) +
                " · RSA清单签名、全部发行资产大小/SHA256和安装器Authenticode验证通过。自签发布者固定，UntrustedRoot为预期信任限制。");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(directory, "release-verification.txt"), "FAIL · " + error);
            return 1;
        }
    }
}
