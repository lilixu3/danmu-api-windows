using System.Globalization;
using DanmuApi.Core;

namespace DanmuApi.Runtime;

public sealed class CoreEnvRepository
{
    private readonly string _scriptDirectory;
    private readonly IReadOnlyDictionary<string, string> _systemEnvironment;

    public CoreEnvRepository(
        string scriptDirectory,
        IReadOnlyDictionary<string, string>? systemEnvironment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptDirectory);
        _scriptDirectory = Path.GetFullPath(scriptDirectory);
        _systemEnvironment = systemEnvironment ?? Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(entry => entry.Key is string && entry.Value is string)
            .ToDictionary(
                entry => ((string)entry.Key).ToUpperInvariant(),
                entry => (string)entry.Value!,
                StringComparer.OrdinalIgnoreCase);
    }

    public CoreEnvSnapshot ReadSnapshot(ManagedCoreVariant variant)
    {
        var coreDirectory = Path.Combine(_scriptDirectory, variant.ToDirectoryName());
        if (!Directory.Exists(coreDirectory))
        {
            throw new DirectoryNotFoundException($"当前核心变体尚未安装：{coreDirectory}");
        }

        var catalogPath = Path.Combine(coreDirectory, "configs", "envs.js");
        var definitions = CoreEnvCatalog.ParseFile(catalogPath);
        if (definitions.Count == 0)
        {
            throw new FormatException($"当前核心没有可编辑的环境变量定义：{catalogPath}");
        }

        var envPath = Path.Combine(_scriptDirectory, "config", ".env");
        var envValues = DotEnvFile.ReadValuesStrict(envPath);
        var values = new Dictionary<string, CoreEnvValueState>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            var hasProcessOverride = _systemEnvironment.TryGetValue(definition.Key, out var processValue);
            var isConfigured = envValues.TryGetValue(definition.Key, out var configuredValue);
            var source = hasProcessOverride
                ? CoreEnvValueSource.ProcessEnvironment
                : isConfigured
                    ? CoreEnvValueSource.DotEnv
                    : definition.DefaultValue is not null
                        ? CoreEnvValueSource.CoreDefault
                        : CoreEnvValueSource.Unconfigured;
            values[definition.Key] = new CoreEnvValueState(
                definition,
                isConfigured,
                configuredValue,
                hasProcessOverride,
                processValue,
                hasProcessOverride ? processValue : isConfigured ? configuredValue : definition.DefaultValue,
                source);
        }

        return new CoreEnvSnapshot(
            variant,
            coreDirectory,
            catalogPath,
            envPath,
            DotEnvFile.GetFingerprint(envPath).Sha256,
            definitions,
            values);
    }

    public DotEnvTransactionResult Apply(
        CoreEnvSnapshot snapshot,
        IReadOnlyList<DotEnvMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(mutations);
        ValidateMutations(snapshot, mutations);
        return DotEnvFile.ApplyMutations(snapshot.EnvPath, new DotEnvFileFingerprint(
            File.Exists(snapshot.EnvPath),
            snapshot.EnvFingerprint), mutations);
    }

    public static void ValidateMutations(CoreEnvSnapshot snapshot, IReadOnlyList<DotEnvMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(mutations);
        var definitions = snapshot.Definitions.ToDictionary(definition => definition.Key, StringComparer.Ordinal);
        foreach (var mutation in mutations)
        {
            var key = mutation.Key.Trim().ToUpperInvariant();
            if (CoreEnvDefinitionRules.IsHostOwned(key))
            {
                throw new InvalidOperationException($"不允许通过配置工作台编辑 Desktop 宿主变量：{key}");
            }

            if (key.Equals("ADMIN_TOKEN", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("ADMIN_TOKEN 必须通过 设置 > 安全 的管理员密码流程修改");
            }

            if (!definitions.TryGetValue(key, out var definition))
            {
                throw new KeyNotFoundException($"当前核心未声明环境变量：{key}");
            }

            if (mutation.Kind == DotEnvMutationKind.Delete)
            {
                continue;
            }

            if (mutation.Kind != DotEnvMutationKind.Set || mutation.Value is null)
            {
                throw new ArgumentException($"环境变量变更非法：{key}", nameof(mutations));
            }

            ValidateValue(definition, mutation.Value);
        }
    }

    public static void ValidateValue(CoreEnvDefinition definition, string value)
    {
        var normalized = value.TrimEnd('\r', '\n');
        CoreEnvStructuredValidation.Validate(definition, normalized);
        switch (definition.Type)
        {
            case CoreEnvType.Number:
                if (!decimal.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                    decimal.IsNegative(number) && definition.Minimum is >= 0)
                {
                    throw new FormatException($"{definition.Key} 必须是数字");
                }

                if (definition.Minimum is not null && number < definition.Minimum)
                {
                    throw new ArgumentOutOfRangeException(definition.Key, number, $"{definition.Key} 不能小于 {definition.Minimum}");
                }

                if (definition.Maximum is not null && number > definition.Maximum)
                {
                    throw new ArgumentOutOfRangeException(definition.Key, number, $"{definition.Key} 不能大于 {definition.Maximum}");
                }

                break;
            case CoreEnvType.Boolean:
                if (normalized is not ("true" or "false"))
                {
                    throw new FormatException($"{definition.Key} 必须是 true 或 false");
                }

                break;
            case CoreEnvType.Select:
                if (!definition.Options.Contains(normalized, StringComparer.Ordinal))
                {
                    throw new FormatException($"{definition.Key} 必须从已声明选项中选择");
                }

                break;
            case CoreEnvType.MultiSelect:
                ValidateMultiSelect(definition, normalized);
                break;
            case CoreEnvType.Text:
            case CoreEnvType.Map:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(definition.Type), definition.Type, "未知核心环境变量类型");
        }
    }

    private static void ValidateMultiSelect(CoreEnvDefinition definition, string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        var groups = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (groups.Length == 0 || groups.Any(group => group.Split('&', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(item => !definition.Options.Contains(item, StringComparer.Ordinal))))
        {
            throw new FormatException($"{definition.Key} 包含未声明的选项");
        }

        if (groups.Any(group => group.Contains('&', StringComparison.Ordinal) &&
                                group.Split('&', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length < 2))
        {
            throw new FormatException($"{definition.Key} 的组合选项格式非法");
        }
    }
}
