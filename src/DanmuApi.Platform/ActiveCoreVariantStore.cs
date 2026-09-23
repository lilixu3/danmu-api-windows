using DanmuApi.Core;

namespace DanmuApi.Platform;

/// <summary>
/// 运行核心变体的选择（settings.properties 的 <c>variant_override</c>）。
/// 核心进程启动时由宿主把三键写进核心 config\.env，本键决定用的是哪个变体目录；
/// 写它并重启服务就是「切换运行核心」。未设置时跟随 .env 里已有的 DANMU_API_VARIANT。
/// </summary>
public interface IActiveCoreVariantStore
{
    /// <summary>返回 null 表示没有覆盖，运行变体由核心 .env 决定。</summary>
    ManagedCoreVariant? Read();
    void Write(ManagedCoreVariant variant);
}

public sealed class SettingsActiveCoreVariantStore(ISettingsStore settingsStore) : IActiveCoreVariantStore
{
    public const string Key = "variant_override";

    private readonly ISettingsStore _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));

    public ManagedCoreVariant? Read()
    {
        var values = _settingsStore.Read();
        if (!values.TryGetValue(Key, out var configured) || string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        try
        {
            return ManagedCoreVariantExtensions.ParseManagedVariant(configured.Trim().ToLowerInvariant());
        }
        catch (FormatException error)
        {
            // 读不懂的覆盖值不能悄悄当成"没设置"：那会让运行变体和界面显示不一致。
            throw new IOException($"设置 {Key} 的取值不受支持：{configured}", error);
        }
    }

    public void Write(ManagedCoreVariant variant)
    {
        _settingsStore.Write(new Dictionary<string, string?>
        {
            [Key] = variant.ToStorageKey(),
        });
        var applied = Read();
        if (applied != variant)
        {
            throw new IOException($"写入 {Key} 后回读校验失败：期望 {variant.ToStorageKey()}，实际 {applied?.ToStorageKey() ?? "未设置"}");
        }
    }
}
