using Avalonia;
using Avalonia.Controls;
using DanmuApi.App.Services;
using DanmuApi.Core;

namespace DanmuApi.App.Views;

/// <summary>
/// 编辑器弹窗的元信息。对应核心自带前端 #env-modal 顶部那三个只读字段
/// （变量类别 / 变量名 / 值类型）与最底部的描述。
/// </summary>
public sealed record ConfigEditorMetadata(
    string Category,
    string Key,
    string TypeLabel,
    string Description);

public sealed partial class ConfigurationEditorWindow : Window
{
    /// <summary>窗口宽度下限（核心弹窗就是 560，这里允许更窄一点以免挤坏窄屏）。</summary>
    private const double MinimumWidth = 520;

    /// <summary>窗口宽度上限。</summary>
    private const double MaximumWidth = 1080;

    private readonly double? _ownerWidth;

    public ConfigurationEditorWindow()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => Close();
        CloseButton.Click += (_, _) => Close();
        // 尺寸约束尽量在显示前定下来（CenterOwner 是按显示那一刻的尺寸定位的）；
        // 高度用 SizeToContent=Height 让窗口自己量准，显示后再切成 Manual 冻结。
        ConstrainToWorkingArea();
        Opened += (_, _) => FreezeHeight();
    }

    public ConfigurationEditorWindow(
        CoreEnvDefinition definition,
        string description,
        Control editor)
        : this(ConfigEditorMetadataFactory.From(definition, description), editor)
    {
        ArgumentNullException.ThrowIfNull(definition);
    }

    /// <summary>兼容旧调用：只有标题与描述时，元信息字段留空。</summary>
    public ConfigurationEditorWindow(
        string title,
        string description,
        Control editor)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(editor);
        Title = title;
        TitleBlock.Text = title;
        KeyBlock.Text = title;
        DescriptionBlock.Text = description;
        EditorHost.Content = editor;
    }

    public ConfigurationEditorWindow(ConfigEditorMetadata metadata, Control editor)
        : this()
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(editor);
        Title = "编辑配置项";
        TitleBlock.Text = "编辑配置项";
        CategoryBlock.Text = metadata.Category;
        KeyBlock.Text = metadata.Key;
        TypeBlock.Text = metadata.TypeLabel;
        DescriptionBlock.Text = metadata.Description;
        EditorHost.Content = editor;
    }

    /// <summary>
    /// 带上宿主窗口宽度的构造。宽度必须在**显示之前**定下来：
    /// <c>WindowStartupLocation=CenterOwner</c> 是按显示那一刻的尺寸居中定位的，
    /// 显示后再改宽度会让窗口偏到左上（用户实测到的现象）。
    /// </summary>
    public ConfigurationEditorWindow(ConfigEditorMetadata metadata, Control editor, double? ownerWidth)
        : this(metadata, editor)
    {
        _ownerWidth = ownerWidth;
        Width = ComputePreferredWidth(ownerWidth, double.PositiveInfinity);
    }

    private void ConstrainToWorkingArea()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null || screen.Scaling <= 0)
        {
            return;
        }

        // 只作为"别超出屏幕"的安全网，不压低高度：内容高就让它高，超出部分由中间滚动区承担。
        var workingWidth = screen.WorkingArea.Width / screen.Scaling;
        var workingHeight = screen.WorkingArea.Height / screen.Scaling;
        MaxWidth = Math.Min(MaximumWidth, Math.Max(MinimumWidth, workingWidth - 48));
        MaxHeight = Math.Max(320, workingHeight - 48);

        if (double.IsNaN(Width))
        {
            Width = ComputePreferredWidth(_ownerWidth, workingWidth);
        }
    }

    /// <summary>
    /// 显示后立刻把 <c>SizeToContent</c> 从 Height 切成 Manual：先让 Height 模式把初始高度
    /// 量准（=内容高度），再冻结，否则用户手动拉伸会被这个模式弹回，
    /// 中间内容区也就没法跟着变（用户实测到的"内容割裂、上下留白"）。
    /// </summary>
    private void FreezeHeight() => SizeToContent = SizeToContent.Manual;

    /// <summary>弹窗宽度的计算规则（抽出来是为了能单测，不依赖真实屏幕）。</summary>
    public static double ComputePreferredWidth(double? ownerWidth, double workingWidth)
    {
        var preferred = ownerWidth is > 0 ? ownerWidth.Value * 0.55 : 560;
        var available = double.IsPositiveInfinity(workingWidth)
            ? MaximumWidth
            : Math.Max(MinimumWidth, workingWidth - 48);
        return Math.Clamp(Math.Min(preferred, MaximumWidth), MinimumWidth, available);
    }

    public Button SaveActionButton => SaveButton;

    public CoreEnvEditResult Result { get; set; } = CoreEnvEditResult.Cancel();

    public string? ErrorMessage
    {
        get => ErrorBlock.Text;
        set
        {
            ErrorBlock.Text = value ?? string.Empty;
            ErrorBlock.IsVisible = !string.IsNullOrWhiteSpace(value);
        }
    }
}

/// <summary>把核心 envs.js 的元数据翻译成弹窗顶部三个只读字段的文案。</summary>
public static class ConfigEditorMetadataFactory
{
    public static ConfigEditorMetadata From(CoreEnvDefinition definition, string description)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new ConfigEditorMetadata(
            CategoryLabel(definition.Category),
            definition.Key,
            TypeLabel(definition.Type),
            description);
    }

    /// <summary>与核心 getEnvTypeLabel 的用词保持一致。</summary>
    public static string TypeLabel(CoreEnvType type) => type switch
    {
        CoreEnvType.Boolean => "布尔",
        CoreEnvType.Number => "数字",
        CoreEnvType.Select => "单选",
        CoreEnvType.Map => "映射",
        CoreEnvType.MultiSelect => "多选",
        _ => "文本",
    };

    public static string CategoryLabel(string category) => category switch
    {
        "api" => "API 配置",
        "source" => "数据源配置",
        "match" => "匹配配置",
        "danmu" => "弹幕配置",
        "cache" => "缓存配置",
        "system" => "系统配置",
        _ => category,
    };
}
