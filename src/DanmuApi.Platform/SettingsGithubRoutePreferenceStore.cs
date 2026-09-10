using DanmuApi.Core;

namespace DanmuApi.Platform;

public sealed class SettingsGithubRoutePreferenceStore : IGithubRoutePreferenceStore
{
    public const string ProxyKey = "github_proxy";
    public const string ConfirmedKey = "github_proxy_confirmed";
    private readonly ISettingsStore _settingsStore;

    public SettingsGithubRoutePreferenceStore(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    public GithubRoutePreference Read()
    {
        var values = _settingsStore.Read();
        var id = values.TryGetValue(ProxyKey, out var rawId) && !string.IsNullOrWhiteSpace(rawId)
            ? rawId.Trim()
            : GithubProxyCatalog.OriginalId;
        _ = GithubProxyCatalog.GetById(id);
        var confirmed = values.TryGetValue(ConfirmedKey, out var rawConfirmed)
            ? rawConfirmed switch
            {
                "true" => true,
                "false" => false,
                _ => throw new FormatException($"设置 {ConfirmedKey} 不是有效布尔值：{rawConfirmed}"),
            }
            : false;
        return new GithubRoutePreference(id, confirmed);
    }

    public void Confirm(string proxyId)
    {
        var option = GithubProxyCatalog.GetById(proxyId);
        _settingsStore.Write(new Dictionary<string, string?>
        {
            [ProxyKey] = option.Id,
            [ConfirmedKey] = "true",
        });
        var roundTrip = Read();
        if (!roundTrip.Confirmed || !string.Equals(roundTrip.ProxyId, option.Id, StringComparison.Ordinal))
        {
            throw new IOException("GitHub 下载线路设置回读校验失败");
        }
    }

    public void Invalidate()
    {
        var current = Read();
        _settingsStore.Write(new Dictionary<string, string?>
        {
            [ProxyKey] = current.ProxyId,
            [ConfirmedKey] = "false",
        });
        if (Read().Confirmed)
        {
            throw new IOException("GitHub 下载线路失效状态回读校验失败");
        }
    }
}
