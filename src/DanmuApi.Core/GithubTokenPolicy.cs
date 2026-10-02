namespace DanmuApi.Core;

/// <summary>
/// 用户保存的 GitHub Token 什么时候可以挂到请求上。
///
/// 只有 <c>api.github.com</c> 的 HTTPS 请求会消耗 API 配额、也只有它接受 Bearer 认证：
/// 统一的策略入口放在这里，避免"核心检查用 Token、软件更新检查却还在用匿名额度"这类分叉
/// （那正是用户报过的现象：存了 Token 额度也刷新了，检查更新仍报超限）。
///
/// 刻意**不**挂 Token 的目标，以及理由：
///  - 第三方代理线路（gh-proxy 等）：会把用户凭据交给第三方；
///  - raw.githubusercontent.com（测速探针）与发行资产 CDN：不消耗 API 配额、无需认证；
///  - Git 命令行（PR 合并、核心 zipball 之外的 git 操作）：凭据不得进入 Git URL 或环境。
/// 这些请求本来就不受那 60/小时 的匿名配额限制，排除它们不影响可用性。
///
/// 跨主机重定向由 .NET 自己剥掉 Authorization（实测：逐请求头 + AllowAutoRedirect 时，
/// 第二跳收不到凭据；同主机重定向同样剥离），因此 api.github.com 的请求即使 302 到
/// codeload.github.com 或发行资产 CDN，Token 也不会被带出去。
/// </summary>
public static class GithubTokenPolicy
{
    public const string ApiHost = "api.github.com";

    /// <summary>该请求地址是否允许附带 Token。</summary>
    public static bool CanAttach(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        uri.UserInfo.Length == 0 &&
        uri.Port == 443 &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.IdnHost, ApiHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把用户输入规整成可直接放进 <c>Authorization: Bearer</c> 的值。
    /// 用户常把 "<c>Bearer xxx</c>" / "<c>token xxx</c>" 整串粘进来，这里统一剥掉前缀。
    /// </summary>
    public static string Normalize(string? token)
    {
        var value = token?.Trim() ?? string.Empty;
        if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return value[7..].Trim();
        }

        return value.StartsWith("token ", StringComparison.OrdinalIgnoreCase)
            ? value[6..].Trim()
            : value;
    }

    /// <summary>从提供方取一个可直接使用的 Token（未配置或读取失败时由调用方决定怎么报）。</summary>
    public static string Read(IGithubTokenProvider? provider) =>
        provider is null ? string.Empty : Normalize(provider.GetToken());
}
