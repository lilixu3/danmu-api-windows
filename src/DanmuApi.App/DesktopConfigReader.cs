using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.App;

internal static class DesktopConfigReader
{
    public static RuntimeConfig Read(ISettingsStore settingsStore, string scriptDirectory)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptDirectory);

        var values = settingsStore.Read();
        var settings = new RuntimeSettings(
            PortOverride: ReadOptionalPort(values, "port_override"),
            ListenHostOverride: ReadOptional(values, "listen_host_override"),
            VariantOverride: ReadOptional(values, "variant_override"),
            Ipv6Enabled: ReadBoolean(values, "ipv6_enabled"));
        return RuntimeConfigResolver.Resolve(settings, scriptDirectory);
    }

    private static string? ReadOptional(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static int? ReadOptionalPort(IReadOnlyDictionary<string, string> values, string key)
    {
        var value = ReadOptional(values, key);
        if (value is null)
        {
            return null;
        }

        if (!int.TryParse(value, out var port))
        {
            throw new FormatException($"设置 {key} 必须是整数");
        }

        RuntimeValidation.ValidatePort(port);
        return port;
    }

    private static bool ReadBoolean(IReadOnlyDictionary<string, string> values, string key)
    {
        var value = ReadOptional(values, key);
        if (value is null)
        {
            return false;
        }

        if (!bool.TryParse(value, out var result))
        {
            throw new FormatException($"设置 {key} 必须是 true 或 false");
        }

        return result;
    }
}
