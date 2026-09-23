namespace DanmuApi.Core.ApplicationUpdates;

/// <summary>
/// 「上一次检查到现在是否已经隔够久」的统一口径，供核心与应用两侧的自动检查共用。
/// 冷却只约束**自动**触发：用户手动点「检查更新」由调用方直接跳过本判定。
/// 时钟被调回过去时一律判到期 —— 宁可多查一次，也不要因为系统时间倒退就永远不再检查。
/// </summary>
public static class UpdateCheckCadence
{
    public static bool IsDue(TimeProvider timeProvider, DateTimeOffset? lastCheckedAt, TimeSpan cooldown)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (cooldown <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cooldown), "冷却时间必须大于零");
        }

        if (lastCheckedAt is null)
        {
            return true;
        }

        var elapsed = timeProvider.GetUtcNow() - lastCheckedAt.Value;
        return elapsed >= cooldown || elapsed < TimeSpan.Zero;
    }
}
