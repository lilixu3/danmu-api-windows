using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuApi.App.Services;
using DanmuApi.Core;
using DanmuApi.Platform;

namespace DanmuApi.App.ViewModels;

public partial class BackupPageViewModel(BackupLocalService local, BackupWebDavClient remote,
    BackupWebDavSettings settings, IBackupDialogService dialogs) : ObservableObject, IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            lifetime.Cancel();
            Password = "";
        }
        return ValueTask.CompletedTask;
    }

    [ObservableProperty] private string collectionUrl = "https://";
    [ObservableProperty] private string username = "";
    [ObservableProperty] private string password = "";
    [ObservableProperty] private string status = "支持 Android schema 1–2 配置迁移；本机身份、秘密、移动端设置及其他类别不恢复。";
    [ObservableProperty] private string previewText = "先选择本地备份或从 WebDAV 下载，检查预览后再恢复。";
    [ObservableProperty] private bool isBusy;
    public bool IsIdle => !IsBusy;
    public string PrivacyNotice => BackupBundle.Warning;
    private BackupRestorePreview? preview;
    /// <summary>Diagnostic failure retained for the host's explicitly redacted diagnostics pipeline; never bind or log raw.</summary>
    public Exception? LastFailure { get; private set; }
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsIdle));

    private BackupWebDavConfiguration Config() => new(CollectionUrl, Username, Password);
    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy || disposed) return;
        IsBusy = true;
        LastFailure = null;
        try { await action(); }
        catch (Exception error)
        {
            LastFailure = error;
            // Do not render exception messages: XML, paths, HTTP implementations and parsers can echo secrets.
            Status = error switch
            {
                DanmuApi.Runtime.DotEnvConflictException => "恢复失败：配置、收藏或偏好已改变，请重新读取备份并预览。",
                BackupWebDavException dav => dav.Message,
                AggregateException => "恢复失败且回滚不完整，需人工检查文件；完整异常已保留在诊断状态。",
                HttpRequestException network => $"WebDAV 连接失败：{network.HttpRequestError}。",
                OperationCanceledException => "操作已取消或超时。",
                _ => $"操作失败（{error.GetType().Name}）。请检查备份格式、文件权限、停止状态与 WebDAV 权限；未报告成功。"
            };
        }
        finally { IsBusy = false; }
    }

    [RelayCommand] private Task ExportAsync() => RunAsync(async () =>
    {
        var file = await dialogs.PickFileAsync(true);
        if (file is null) return;
        if (!await dialogs.ConfirmAsync("授权本地备份", PrivacyNotice + "\n\n保存位置：" + file)) return;
        await local.ExportAsync(file, true, lifetime.Token);
        Status = "本地备份已保存并通过回读校验。";
    });
    [RelayCommand] private Task OpenAsync() => RunAsync(async () =>
    {
        preview = null;
        PreviewText = "正在读取备份。";
        var file = await dialogs.PickFileAsync(false);
        if (file is null) { PreviewText = "未选择备份。"; return; }
        ShowPreview(await local.PreviewFileAsync(file, lifetime.Token));
    });
    [RelayCommand] private Task RestoreAsync() => RunAsync(async () =>
    {
        if (preview is null) throw new InvalidOperationException("需要预览");
        if (!await dialogs.ConfirmAsync("覆盖现有配置", PreviewText + "\n\n覆盖列出的配置键和桌面偏好；若预览含收藏，将整体替换本地收藏。必须先停止核心。确认恢复？")) return;
        await local.RestoreAsync(preview.Id, true, lifetime.Token);
        preview = null;
        PreviewText = "恢复完成；再次恢复需重新读取备份。";
        Status = "配置已恢复，原子写入及回读校验通过。";
    });
    [RelayCommand] private Task LoadCredentialsAsync() => RunAsync(() =>
    {
        var config = settings.Load();
        if (config is null) { Status = "尚未保存 WebDAV 配置。"; return Task.CompletedTask; }
        CollectionUrl = config.CollectionUrl; Username = config.Username; Password = config.Password;
        Status = "已读取此 Windows 用户的 DPAPI 保护配置。";
        return Task.CompletedTask;
    });
    [RelayCommand] private Task SaveCredentialsAsync() => RunAsync(() =>
    {
        settings.Save(Config()); Status = "WebDAV 配置已由 DPAPI 保护保存，仅当前 Windows 用户可读。";
        return Task.CompletedTask;
    });
    [RelayCommand] private Task ClearCredentialsAsync() => RunAsync(async () =>
    {
        if (!await dialogs.ConfirmAsync("删除保存的凭据", "确认删除此设备保存的 WebDAV 配置？")) return;
        settings.Clear(); Password = ""; Status = "保存的 WebDAV 凭据已删除。";
    });
    [RelayCommand] private Task ListRemoteAsync() => RunAsync(async () =>
    {
        var files = await remote.ListAsync(Config(), lifetime.Token);
        Status = files.Count == 0 ? "集合可访问，未找到 app-backup.json。请先在服务器创建目标集合。" : "集合中存在 app-backup.json，可下载预览。";
    });
    [RelayCommand] private Task UploadAsync() => RunAsync(async () =>
    {
        var config = Config();
        var target = new Uri(config.Collection(), BackupWebDavClient.FileName);
        if (!await dialogs.ConfirmAsync("授权上传及远程覆盖", PrivacyNotice + "\n\n目标：" + target + "\n将覆盖此位置的 app-backup.json，确认上传？")) return;
        await remote.UploadAsync(config, local.Create(), true, lifetime.Token);
        Status = "备份已上传至授权 WebDAV 目标。";
    });
    [RelayCommand] private Task DownloadAsync() => RunAsync(async () =>
    {
        preview = null;
        PreviewText = "正在下载备份。";
        ShowPreview(await local.PreviewAsync(await remote.DownloadAsync(Config(), lifetime.Token), lifetime.Token));
    });
    private void ShowPreview(BackupRestorePreview value)
    {
        preview = value;
        PreviewText = $"Schema {value.SchemaVersion} · {value.CreatedAt.LocalDateTime:g}\n目标：{value.Target}\n将覆盖/新增 {value.Keys.Count} 个配置键：\n{string.Join(", ", value.Keys)}\n收藏：{(value.FavoriteCount is { } count ? $"整体替换为 {count} 项" : "不变")}\n桌面偏好：{string.Join(", ", value.DesktopPreferenceKeys)}（重启应用后生效）\n排除 {value.ExcludedKeys} 个秘密/宿主键。\n不恢复的移动端类别：{string.Join(", ", value.OmittedSections)}";
        Status = "预览就绪，未写入配置。确认内容后停止核心，再点击恢复。";
    }
}
