using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DanmuApi.App.ViewModels;

/// <summary>
/// 「弹幕文件详情」弹窗的状态。以前这些内容放在列表右侧的详情卡里，窄窗口叠成上下两栏后
/// 既显示不全又要来回滚动；改成弹窗后列表独占整列高度，详情也不再受窗口高度挤压。
///
/// 数据由页面 VM 填（页面持有客户端与对话框），本类只负责弹窗内的展示与「预览 / 删除 / 关闭」动作。
/// </summary>
public sealed partial class LocalDanmuDetailDialogViewModel : ObservableObject
{
    private readonly Func<Task> _loadPreview;
    private readonly Func<Task> _delete;

    public LocalDanmuDetailDialogViewModel(Func<Task> loadPreview, Func<Task> delete)
    {
        _loadPreview = loadPreview ?? throw new ArgumentNullException(nameof(loadPreview));
        _delete = delete ?? throw new ArgumentNullException(nameof(delete));
    }

    /// <summary>弹窗请求关闭（删除成功或用户点关闭）。</summary>
    public event EventHandler? CloseRequested;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _episodeLabel = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _metaText = string.Empty;

    [ObservableProperty]
    private string _resourceKey = string.Empty;

    [ObservableProperty]
    private bool _canWrite;

    [ObservableProperty]
    private bool _isPreviewOpen;

    [ObservableProperty]
    private bool _isPreviewBusy;

    [ObservableProperty]
    private IReadOnlyList<LocalDanmuPreviewLine> _previewLines = [];

    [ObservableProperty]
    private string _previewSummaryText = string.Empty;

    [ObservableProperty]
    private string _diagnostic = string.Empty;

    public string PreviewToggleText => IsPreviewOpen ? "收起预览" : "预览弹幕";
    public bool HasDiagnostic => Diagnostic.Length > 0;

    public void Show(LocalDanmuEpisodeRow row, bool canWrite)
    {
        ArgumentNullException.ThrowIfNull(row);
        Title = row.Title;
        EpisodeLabel = row.EpisodeLabel;
        FileName = row.FileName;
        MetaText = row.MetaText;
        ResourceKey = row.ResourceKey;
        CanWrite = canWrite;
        IsPreviewOpen = false;
        IsPreviewBusy = false;
        PreviewLines = [];
        PreviewSummaryText = string.Empty;
        Diagnostic = string.Empty;
    }

    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task TogglePreviewAsync()
    {
        if (IsPreviewOpen)
        {
            IsPreviewOpen = false;
            PreviewLines = [];
            PreviewSummaryText = string.Empty;
            return;
        }

        IsPreviewBusy = true;
        try
        {
            await _loadPreview().ConfigureAwait(true);
        }
        finally
        {
            IsPreviewBusy = false;
        }
    }

    [RelayCommand]
    private Task DeleteAsync() => _delete();

    [RelayCommand]
    private void Close() => RequestClose();

    partial void OnIsPreviewOpenChanged(bool value) => OnPropertyChanged(nameof(PreviewToggleText));

    partial void OnDiagnosticChanged(string value) => OnPropertyChanged(nameof(HasDiagnostic));
}
