using System.Globalization;
using DanmuApi.Core;

namespace DanmuApi.Platform;

public sealed class SettingsCoreUpdateTimestampStore : ICoreUpdateTimestampStore
{
    private readonly ISettingsStore _settingsStore;

    public SettingsCoreUpdateTimestampStore(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    public DateTimeOffset? ReadLastCheck(ManagedCoreVariant variant)
    {
        var values = _settingsStore.Read();
        if (!values.TryGetValue(Key(variant), out var raw))
        {
            return null;
        }

        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) || milliseconds < 0)
        {
            throw new FormatException($"核心更新检查时间无效：{raw}");
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    public void WriteLastCheck(ManagedCoreVariant variant, DateTimeOffset checkedAt)
    {
        _settingsStore.Write(new Dictionary<string, string?>
        {
            [Key(variant)] = checkedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        });
    }

    private static string Key(ManagedCoreVariant variant) =>
        $"core_update_last_check_{variant.ToStorageKey()}";
}
