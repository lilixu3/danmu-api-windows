namespace DanmuApi.Core;

public enum CoreEnvType
{
    Text,
    Number,
    Boolean,
    Select,
    MultiSelect,
    Map,
}

public enum CoreEnvApplyMode
{
    HotReload,
    RestartService,
}

public enum CoreEnvValueSource
{
    ProcessEnvironment,
    DotEnv,
    CoreDefault,
    Unconfigured,
}

public sealed record CoreEnvDefinition(
    string Key,
    string Category,
    CoreEnvType Type,
    string Description,
    IReadOnlyList<string> Options,
    IReadOnlyList<string> Sources,
    decimal? Minimum,
    decimal? Maximum,
    string? DefaultValue,
    bool IsSensitive,
    bool IsAdministratorManaged,
    CoreEnvApplyMode ApplyMode = CoreEnvApplyMode.HotReload);

public sealed record CoreEnvValueState(
    CoreEnvDefinition Definition,
    bool IsConfigured,
    string? ConfiguredValue,
    bool HasProcessOverride,
    string? ProcessOverrideValue,
    string? EffectiveValue,
    CoreEnvValueSource Source);

public sealed record CoreEnvSnapshot(
    ManagedCoreVariant Variant,
    string CoreDirectory,
    string CatalogPath,
    string EnvPath,
    string EnvFingerprint,
    IReadOnlyList<CoreEnvDefinition> Definitions,
    IReadOnlyDictionary<string, CoreEnvValueState> Values)
{
    public int ConfiguredCount => Values.Values.Count(value => value.IsConfigured);

    public int ProcessOverrideCount => Values.Values.Count(value => value.HasProcessOverride);
}

public static class CoreEnvDefinitionRules
{
    private static readonly HashSet<string> SensitiveUrlKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "CUSTOM_SOURCE_API_URL",
        "PROXY_URL",
        "LOCAL_REDIS_URL",
        "UPSTASH_REDIS_REST_URL",
    };

    public static bool IsHostOwned(string key) =>
        key.StartsWith("DANMU_API_", StringComparison.OrdinalIgnoreCase);

    public static bool IsSensitiveName(string key)
    {
        var normalized = key.ToUpperInvariant();
        return normalized.Contains("TOKEN", StringComparison.Ordinal) ||
               normalized.Contains("COOKIE", StringComparison.Ordinal) ||
               normalized.Contains("PASSWORD", StringComparison.Ordinal) ||
               normalized.Contains("SECRET", StringComparison.Ordinal) ||
               normalized.Contains("API_KEY", StringComparison.Ordinal) ||
               normalized.EndsWith("_KEY", StringComparison.Ordinal) ||
               SensitiveUrlKeys.Contains(normalized);
    }
}
