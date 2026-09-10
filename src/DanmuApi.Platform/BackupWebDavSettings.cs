using System.Text.Json;
using DanmuApi.Core;

namespace DanmuApi.Platform;

/// <summary>Requires a dedicated protected store, never the GitHub or admin-session store.</summary>
public sealed class BackupWebDavSettings(IProtectedStringStore protectedStore)
{
    public BackupWebDavConfiguration? Load()
    {
        var raw = protectedStore.Load();
        if (raw is null) return null;
        var config = JsonSerializer.Deserialize<BackupWebDavConfiguration>(raw) ?? throw new InvalidDataException("WebDAV 保护配置无效");
        _ = config.Collection();
        return config;
    }
    public void Save(BackupWebDavConfiguration config)
    {
        _ = config.Collection();
        protectedStore.Save(JsonSerializer.Serialize(config));
    }
    public void Clear() => protectedStore.Clear();
}
