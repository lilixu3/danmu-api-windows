using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using DanmuApi.Platform;

namespace DanmuApi.App.Services;

public sealed class ThemeService(ISettingsStore settingsStore)
{
    private readonly ISettingsStore _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    public string CurrentTheme { get; private set; } = "system";

    /// <summary>应用当前实际呈现的深浅状态；跟随系统时以系统外观为准。</summary>
    public bool IsDarkActive
    {
        get
        {
            Dispatcher.UIThread.VerifyAccess();
            return GetApplication().ActualThemeVariant == ThemeVariant.Dark;
        }
    }

    /// <summary>主题实际生效后触发（包含跟随系统时的解析结果变化）。</summary>
    public event EventHandler? ThemeChanged;

    public void Initialize()
    {
        var application = GetApplication();
        var values = _settingsStore.Read();
        var theme = values.TryGetValue("theme", out var value) ? value : "system";
        application.RequestedThemeVariant = Parse(theme);
        CurrentTheme = theme;
    }

    public void SetTheme(string theme)
    {
        var variant = Parse(theme);
        var application = GetApplication();
        _settingsStore.Write(new Dictionary<string, string?> { ["theme"] = theme });
        if (!_settingsStore.Read().TryGetValue("theme", out var saved) || saved != theme)
            throw new IOException("主题设置回读校验失败");
        application.RequestedThemeVariant = variant;
        CurrentTheme = theme;
        NotifyThemeChanged();
    }

    /// <summary>在浅色与深色之间立即切换；结果按当前实际呈现状态推导，保存后生效。</summary>
    public string ToggleLightDark()
    {
        var next = IsDarkActive ? "light" : "dark";
        SetTheme(next);
        return next;
    }

    private void NotifyThemeChanged() => ThemeChanged?.Invoke(this, EventArgs.Empty);

    private static Application GetApplication()
    {
        Dispatcher.UIThread.VerifyAccess();
        return Application.Current ?? throw new InvalidOperationException("主题服务需要已初始化的 Avalonia Application");
    }

    private static ThemeVariant Parse(string theme) => theme switch
    {
        "system" => ThemeVariant.Default,
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        _ => throw new FormatException("主题设置非法：仅支持 system、light、dark"),
    };
}
