using System.Security.Cryptography;
using DanmuApi.Core.ApplicationUpdates;

namespace DanmuApi.App.Services;

internal static class ApplicationReleaseVerifier
{
    public static int Run(string directory)
    {
        try
        {
            using var service = new ApplicationUpdateService(AppUpdateTrust.PublicKey());
            var manifest = service.VerifyManifest(File.ReadAllBytes(Path.Combine(directory,"update-manifest.json")), File.ReadAllBytes(Path.Combine(directory,"update-manifest.json.sig")));
            foreach (var asset in manifest.Assets)
            {
                var file = Path.Combine(directory,asset.Name);
                using var stream = File.OpenRead(file);
                if (stream.Length != asset.Size || !Convert.ToHexString(SHA256.HashData(stream)).Equals(asset.Sha256,StringComparison.OrdinalIgnoreCase)) throw new IOException("发行资产与认证清单不符："+asset.Name);
                if (asset.Kind == "installer") AppUpdateTrust.VerifyExecutable(file);
            }
            File.WriteAllText(Path.Combine(directory,"release-verification.txt"), $"PASS · {manifest.Version} · RSA清单签名、全部发行资产大小/SHA256和安装器Authenticode验证通过。自签发布者固定，UntrustedRoot为预期信任限制。");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(directory,"release-verification.txt"),"FAIL · "+error);
            return 1;
        }
    }
}
