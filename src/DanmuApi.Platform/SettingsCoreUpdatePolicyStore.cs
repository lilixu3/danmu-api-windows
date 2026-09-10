using System.Globalization;
using DanmuApi.Core;

namespace DanmuApi.Platform;

public sealed class SettingsCoreUpdatePolicyStore : ICoreUpdatePolicyStore
{
    public const string ForegroundIntervalKey = "core_foreground_check_interval_minutes";
    public const string BackgroundIntervalKey = "core_background_check_interval_minutes";
    public const string BackgroundEnabledKey = "core_background_check_enabled";
    public const string UpdateActionKey = "core_update_action";
    private readonly ISettingsStore _settingsStore;

    public SettingsCoreUpdatePolicyStore(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    public CoreUpdateScheduleOptions Read()
    {
        var values = _settingsStore.Read();
        var defaults = CoreUpdateScheduleOptions.Default;
        return new CoreUpdateScheduleOptions(
            TimeSpan.FromMinutes(ReadInt(values, ForegroundIntervalKey, (int)defaults.ForegroundInterval.TotalMinutes)),
            TimeSpan.FromMinutes(ReadInt(values, BackgroundIntervalKey, (int)defaults.BackgroundInterval.TotalMinutes)),
            ReadBool(values, BackgroundEnabledKey, defaults.BackgroundEnabled),
            ReadAction(values, UpdateActionKey, defaults.UpdateAction)).Validate();
    }

    public void Write(CoreUpdateScheduleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _settingsStore.Write(new Dictionary<string, string?>
        {
            [ForegroundIntervalKey] = ((int)options.ForegroundInterval.TotalMinutes).ToString(CultureInfo.InvariantCulture),
            [BackgroundIntervalKey] = ((int)options.BackgroundInterval.TotalMinutes).ToString(CultureInfo.InvariantCulture),
            [BackgroundEnabledKey] = options.BackgroundEnabled ? "true" : "false",
            [UpdateActionKey] = options.UpdateAction switch
            {
                CoreUpdateAction.Notify => "notify",
                CoreUpdateAction.Automatic => "automatic",
                _ => throw new ArgumentOutOfRangeException(nameof(options)),
            },
        });

        var roundTrip = Read();
        if (roundTrip != options)
        {
            throw new IOException("核心更新调度设置回读校验失败");
        }
    }

    private static int ReadInt(
        IReadOnlyDictionary<string, string> values,
        string key,
        int defaultValue)
    {
        if (!values.TryGetValue(key, out var raw))
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"设置 {key} 不是有效整数：{raw}");
        }

        return value;
    }

    private static bool ReadBool(
        IReadOnlyDictionary<string, string> values,
        string key,
        bool defaultValue)
    {
        if (!values.TryGetValue(key, out var raw))
        {
            return defaultValue;
        }

        return raw switch
        {
            "true" => true,
            "false" => false,
            _ => throw new FormatException($"设置 {key} 不是有效布尔值：{raw}"),
        };
    }

    private static CoreUpdateAction ReadAction(
        IReadOnlyDictionary<string, string> values,
        string key,
        CoreUpdateAction defaultValue)
    {
        if (!values.TryGetValue(key, out var raw))
        {
            return defaultValue;
        }

        return raw switch
        {
            "notify" => CoreUpdateAction.Notify,
            "automatic" => CoreUpdateAction.Automatic,
            _ => throw new FormatException($"设置 {key} 无效：{raw}"),
        };
    }
}
