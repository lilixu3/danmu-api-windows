namespace DanmuApi.Runtime;

public sealed record RuntimeSettings(
    int? PortOverride = null,
    string? ListenHostOverride = null,
    string? VariantOverride = null,
    bool Ipv6Enabled = false);

public static class RuntimeConfigResolver
{
    public static RuntimeConfig Resolve(RuntimeSettings settings, string scriptDir)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var values = DotEnvFile.ReadValues(Path.Combine(scriptDir, "config", ".env"));

        var port = settings.PortOverride
            ?? ParsePort(values.GetValueOrDefault("DANMU_API_PORT"))
            ?? RuntimeDefaults.Port;
        RuntimeValidation.ValidatePort(port);

        var configuredHost = settings.ListenHostOverride
            ?? values.GetValueOrDefault("DANMU_API_HOST")
            ?? RuntimeDefaults.ListenHost;
        RuntimeValidation.ValidateHost(configuredHost);
        var host = settings.Ipv6Enabled ? "::" : configuredHost == "::" ? RuntimeDefaults.ListenHost : configuredHost;

        var variant = settings.VariantOverride
            ?? values.GetValueOrDefault("DANMU_API_VARIANT")
            ?? RuntimeDefaults.Variant;
        if (settings.VariantOverride is not null)
        {
            RuntimeValidation.ValidateVariant(variant);
        }
        else if (!IsKnownVariant(variant))
        {
            variant = RuntimeDefaults.Variant;
        }

        return new RuntimeConfig(port, host, variant.ToLowerInvariant());
    }

    private static bool IsKnownVariant(string variant) =>
        string.Equals(variant, "stable", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(variant, "dev", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(variant, "custom", StringComparison.OrdinalIgnoreCase);

    private static int? ParsePort(string? value) =>
        int.TryParse(value, out var port) && port is >= 1 and <= 65_535 ? port : null;
}
