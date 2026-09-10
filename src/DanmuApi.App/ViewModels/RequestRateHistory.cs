using System.Globalization;
using DanmuApi.Runtime;

namespace DanmuApi.App.ViewModels;

public sealed record RequestRatePoint(double Seconds, double? Rate);

/// <summary>Bounded, monotonic health-counter history. Null points are discontinuities, never zero traffic.</summary>
public sealed class RequestRateHistory : ViewModelBase
{
    public const int Capacity = 60;
    private RuntimeHealthSnapshot? _baseline;
    private double _baselineTime;
    private readonly List<RequestRatePoint> _points = [];
    public IReadOnlyList<RequestRatePoint> Points { get; private set; } = [];
    public double? CurrentRate { get; private set; }
    public string RateText => CurrentRate?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—";
    public string Status { get; private set; } = "服务停止 · 尚无采样";

    public void Sample(RuntimeHealthSnapshot health, double seconds)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (health.RequestCount is null or < 0 || health.Pid is null || string.IsNullOrWhiteSpace(health.RuntimeIdentity))
        {
            Disconnect(seconds, "健康数据缺少有效计数或实例身份 · 无数据");
            return;
        }
        var previous = _baseline;
        CurrentRate = null;
        Status = "等待下一次采样 · 建立基线";
        if (previous is not null && previous.RuntimeIdentity == health.RuntimeIdentity && previous.Pid == health.Pid
            && health.RequestCount >= previous.RequestCount && seconds > _baselineTime && seconds - _baselineTime <= 15)
        {
            CurrentRate = (health.RequestCount.Value - previous.RequestCount!.Value) / (seconds - _baselineTime);
            Status = "每 5 秒采样 · 最近 60 个样本";
        }
        else if (previous is not null) Status = seconds - _baselineTime > 15
            ? "采样间隔过长 · 重新建立基线"
            : "实例、计数或采样时序已变化 · 重新建立基线";
        _baseline = health;
        _baselineTime = seconds;
        Add(seconds);
    }

    public void Disconnect(double seconds, string diagnostic = "健康检查中断 · 等待恢复")
    {
        _baseline = null;
        CurrentRate = null;
        Status = diagnostic;
        Add(seconds);
    }

    public void Reset(bool running = false)
    {
        _baseline = null;
        _points.Clear();
        CurrentRate = null;
        Status = running ? "等待首次健康采样" : "服务停止 · 尚无采样";
        Publish();
    }

    private void Add(double seconds)
    {
        _points.Add(new(seconds, CurrentRate));
        if (_points.Count > Capacity) _points.RemoveAt(0);
        Publish();
    }

    private void Publish()
    {
        Points = _points.ToArray();
        OnPropertyChanged(nameof(Points));
        OnPropertyChanged(nameof(CurrentRate));
        OnPropertyChanged(nameof(RateText));
        OnPropertyChanged(nameof(Status));
    }
}
