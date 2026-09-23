using DanmuApi.Core;

namespace DanmuApi.Tests;

/// <summary>
/// 核心更新发现记录的测试替身：与 settings 实现同语义（每变体一条，Clear 即删除），
/// 好让协调器测试能直接断言"结论有没有落盘"。
/// </summary>
internal sealed class MemoryCoreUpdateDiscoveryStore : ICoreUpdateDiscoveryStore
{
    private readonly Dictionary<ManagedCoreVariant, CoreUpdateDiscovery> _values = [];

    public int Writes { get; private set; }
    public int Clears { get; private set; }

    public CoreUpdateDiscovery? Read(ManagedCoreVariant variant) =>
        _values.TryGetValue(variant, out var value) ? value : null;

    public void Write(CoreUpdateDiscovery discovery)
    {
        Writes++;
        _values[discovery.Variant] = discovery;
    }

    public void Clear(ManagedCoreVariant variant)
    {
        Clears++;
        _values.Remove(variant);
    }

    /// <summary>直接放一条记录进去，用来模拟"上个进程留下的结论"。</summary>
    public void Seed(CoreUpdateDiscovery discovery) => _values[discovery.Variant] = discovery;
}
