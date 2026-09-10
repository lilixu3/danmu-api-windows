using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace DanmuApi.App.Services;

public interface IBackupDialogService
{
    Task<string?> PickFileAsync(bool save);
    Task<bool> ConfirmAsync(string title, string message);
}

public sealed class BackupDialogService(Func<Window> owner) : IBackupDialogService
{
    public async Task<string?> PickFileAsync(bool save)
    {
        var storage = owner().StorageProvider;
        if (save)
        {
            var selected = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存配置备份（请选择新文件）", SuggestedFileName = $"danmu-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                DefaultExtension = "json", FileTypeChoices = [new FilePickerFileType("JSON 备份") { Patterns = ["*.json"] }]
            });
            return selected?.TryGetLocalPath();
        }
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "读取备份并预览", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON 备份") { Patterns = ["*.json"] }]
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath() ?? throw new IOException("需要本地文件路径");
    }

    public Task<bool> ConfirmAsync(string title, string message)
    {
        var window = new Window { Title = title, Width = 560, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var cancel = new Button { Content = "取消" };
        var confirm = new Button { Content = "确认继续" };
        cancel.Click += (_, _) => window.Close(false);
        confirm.Click += (_, _) => window.Close(true);
        window.Content = new StackPanel { Margin = new Thickness(24), Spacing = 20, Children =
        {
            new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 12, Children = { cancel, confirm } }
        }};
        return window.ShowDialog<bool>(owner());
    }
}
