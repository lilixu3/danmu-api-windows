using DanmuApi.Core;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class CoreEnvRepositoryTests
{
    [Fact]
    public void AppliesProcessEnvDotEnvAndDefaultPrecedence()
    {
        using var directory = new TemporaryDirectory();
        var stable = Path.Combine(directory.Path, "danmu_api_stable", "configs");
        Directory.CreateDirectory(stable);
        File.WriteAllText(Path.Combine(stable, "envs.js"), """
            const envVarConfig = {
              'FROM_PROCESS': { category: 'x', type: 'text', description: 'process' },
              'FROM_DOTENV': { category: 'x', type: 'text', description: 'dotenv' },
              'FROM_DEFAULT': { category: 'x', type: 'text', description: 'default' },
              'EMPTY': { category: 'x', type: 'text', description: 'empty' },
            };
            this.get('FROM_PROCESS', 'default-process', 'string');
            this.get('FROM_DOTENV', 'default-dotenv', 'string');
            this.get('FROM_DEFAULT', 'default-value', 'string');
            this.get('EMPTY', '', 'string');
            """);
        Directory.CreateDirectory(Path.Combine(directory.Path, "config"));
        File.WriteAllText(Path.Combine(directory.Path, "config", ".env"), "FROM_PROCESS=dotenv-value\nFROM_DOTENV=dotenv-value\nEMPTY=\n");

        var repository = new CoreEnvRepository(directory.Path, new Dictionary<string, string>
        {
            ["FROM_PROCESS"] = "process-value",
        });
        var snapshot = repository.ReadSnapshot(ManagedCoreVariant.Stable);

        Assert.Equal(CoreEnvValueSource.ProcessEnvironment, snapshot.Values["FROM_PROCESS"].Source);
        Assert.Equal("process-value", snapshot.Values["FROM_PROCESS"].EffectiveValue);
        Assert.Equal(CoreEnvValueSource.DotEnv, snapshot.Values["FROM_DOTENV"].Source);
        Assert.Equal(CoreEnvValueSource.CoreDefault, snapshot.Values["FROM_DEFAULT"].Source);
        Assert.Equal(CoreEnvValueSource.DotEnv, snapshot.Values["EMPTY"].Source);
        Assert.True(snapshot.Values["EMPTY"].IsConfigured);
        Assert.Equal(string.Empty, snapshot.Values["EMPTY"].EffectiveValue);
    }

    [Fact]
    public void RejectsAdminAndUnknownWritesAndValidatesTypedValues()
    {
        using var directory = new TemporaryDirectory();
        var stable = Path.Combine(directory.Path, "danmu_api_stable", "configs");
        Directory.CreateDirectory(stable);
        File.WriteAllText(Path.Combine(stable, "envs.js"), """
            const envVarConfig = {
              'ADMIN_TOKEN': { category: 'x', type: 'text', description: 'admin' },
              'COUNT': { category: 'x', type: 'number', description: 'count', min: 1, max: 3 },
            };
            this.get('ADMIN_TOKEN', '', 'string', true);
            this.get('COUNT', 2, 'number');
            """);
        Directory.CreateDirectory(Path.Combine(directory.Path, "config"));
        var repository = new CoreEnvRepository(directory.Path);
        var snapshot = repository.ReadSnapshot(ManagedCoreVariant.Stable);

        Assert.Throws<InvalidOperationException>(() => repository.Apply(snapshot, [DotEnvMutation.Set("ADMIN_TOKEN", "secret")]));
        Assert.Throws<ArgumentOutOfRangeException>(() => repository.Apply(snapshot, [DotEnvMutation.Set("COUNT", "4")]));
        Assert.Throws<KeyNotFoundException>(() => repository.Apply(snapshot, [DotEnvMutation.Set("MISSING", "value")]));
    }

    [Fact]
    public void PublicValueValidationRejectsUnknownSelectAndMultiSelectOptions()
    {
        var select = new CoreEnvDefinition(
            "MODE", "x", CoreEnvType.Select, "mode", ["fast", "safe"], [], null, null, null, false, false);
        var multi = new CoreEnvDefinition(
            "SOURCES", "x", CoreEnvType.MultiSelect, "sources", ["a", "b"], [], null, null, null, false, false);

        Assert.Throws<FormatException>(() => CoreEnvRepository.ValidateValue(select, "unknown"));
        Assert.Throws<FormatException>(() => CoreEnvRepository.ValidateValue(multi, "a,unknown"));
        CoreEnvRepository.ValidateValue(select, "fast");
        CoreEnvRepository.ValidateValue(multi, "a,b");
    }
}
