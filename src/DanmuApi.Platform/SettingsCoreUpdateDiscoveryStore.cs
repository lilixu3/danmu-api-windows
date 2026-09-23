using System.Globalization;
using DanmuApi.Core;

namespace DanmuApi.Platform;

/// <summary>
/// 把「上次检查发现核心可更新」这条结论存进桌面设置（settings.properties）。
/// 存的是**结论**而不是检查时间：只有它能让侧栏卡片在进程重启后原样回来，
/// 且不需要重新联网 —— 后者在有更新时刚好是最不该依赖的东西。
///
/// 键与核心变体一一对应（<c>core_update_found_*_&lt;variant&gt;</c>）。读取是严格的：
/// 记录不完整或写坏时按「没有发现」处理并记一条诊断，绝不猜半个值出来；
/// 代价只是一次照常进行的自动检查，不会让应用启动失败。
/// </summary>
public sealed class SettingsCoreUpdateDiscoveryStore : ICoreUpdateDiscoveryStore
{
    public const string RemoteShaSuffix = "remote_sha";
    public const string LocalShaSuffix = "local_sha";
    public const string TitleSuffix = "title";
    public const string CheckedAtSuffix = "checked_at_ms";

    private readonly ISettingsStore _settingsStore;
    private readonly Action<string>? _diagnosticSink;

    public SettingsCoreUpdateDiscoveryStore(ISettingsStore settingsStore, Action<string>? diagnosticSink = null)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _diagnosticSink = diagnosticSink;
    }

    public static string Key(ManagedCoreVariant variant, string suffix) =>
        $"core_update_found_{suffix}_{variant.ToStorageKey()}";

    public CoreUpdateDiscovery? Read(ManagedCoreVariant variant)
    {
        var values = _settingsStore.Read();
        var remote = ReadValue(values, variant, RemoteShaSuffix);
        var local = ReadValue(values, variant, LocalShaSuffix);
        var title = ReadValue(values, variant, TitleSuffix);
        var rawCheckedAt = ReadValue(values, variant, CheckedAtSuffix);
        if (remote is null && local is null && title is null && rawCheckedAt is null)
        {
            return null;
        }

        // 四个键要么齐、要么什么都没有：残缺的记录是坏缓存，不能拿它拼一张卡片出来。
        if (remote is null || local is null || title is null || rawCheckedAt is null)
        {
            _diagnosticSink?.Invoke($"核心更新发现记录不完整，按未发现更新处理：{variant.ToLabel()}");
            return null;
        }

        if (!long.TryParse(rawCheckedAt, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) ||
            milliseconds < 0)
        {
            _diagnosticSink?.Invoke($"核心更新发现记录的时间无效，按未发现更新处理：{rawCheckedAt}");
            return null;
        }

        DateTimeOffset checkedAt;
        try
        {
            checkedAt = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            _diagnosticSink?.Invoke($"核心更新发现记录的时间超出范围，按未发现更新处理：{rawCheckedAt}");
            return null;
        }

        return new CoreUpdateDiscovery(variant, local, remote, title, checkedAt);
    }

    public void Write(CoreUpdateDiscovery discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        _settingsStore.Write(new Dictionary<string, string?>
        {
            [Key(discovery.Variant, RemoteShaSuffix)] = discovery.RemoteSha,
            [Key(discovery.Variant, LocalShaSuffix)] = discovery.LocalSha,
            [Key(discovery.Variant, TitleSuffix)] = discovery.RemoteTitle,
            [Key(discovery.Variant, CheckedAtSuffix)] =
                discovery.CheckedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        });
    }

    public void Clear(ManagedCoreVariant variant)
    {
        var values = _settingsStore.Read();
        var changes = new Dictionary<string, string?>();
        foreach (var suffix in new[] { RemoteShaSuffix, LocalShaSuffix, TitleSuffix, CheckedAtSuffix })
        {
            var key = Key(variant, suffix);
            if (values.ContainsKey(key))
            {
                changes[key] = null;
            }
        }

        if (changes.Count > 0)
        {
            _settingsStore.Write(changes);
        }
    }

    private static string? ReadValue(
        IReadOnlyDictionary<string, string> values,
        ManagedCoreVariant variant,
        string suffix)
    {
        if (!values.TryGetValue(Key(variant, suffix), out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim();
    }
}
