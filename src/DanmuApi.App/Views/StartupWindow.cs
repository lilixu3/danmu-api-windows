using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DanmuApi.App.Views;

public sealed class StartupWindow : Window
{
    private readonly TextBlock _message;
    private readonly ProgressBar _progress;
    private readonly Button _repair;
    public event Action? RepairRequested;
    public bool AllowClose { get; set; }
    public StartupWindow()
    {
        Title = "弹幕API"; Width = 560; Height = 270;
        CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _message = new TextBlock { Text = "正在校验本地运行环境。首次准备可能需要稍候，不需要下载核心。", TextWrapping = TextWrapping.Wrap };
        _progress = new ProgressBar { IsIndeterminate = true, Height = 4 };
        var cancel = new Button { Content = "取消启动", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        cancel.Click += (_, _) => Close();
        _repair = new Button { Content = "修复运行环境…", IsVisible = false };
        _repair.Click += async (_, _) =>
        {
            var confirm = new Window { Title = "确认修复运行环境", Width = 520, Height = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var yes = new Button { Content = "修复应用依赖" };
            var no = new Button { Content = "取消" };
            yes.Click += (_, _) => confirm.Close(true);
            no.Click += (_, _) => confirm.Close(false);
            confirm.Content = new StackPanel { Margin = new Thickness(24), Spacing = 20,
                Children = { new TextBlock { Text = "将使用当前安装包替换应用管理的 Node、启动脚本和生产依赖。不会删除核心、配置或日志。请先停止使用此运行目录的服务。", TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Children = { no, yes } } } };
            if (await confirm.ShowDialog<bool>(this)) { _repair.IsVisible = false; _progress.IsVisible = true; RepairRequested?.Invoke(); }
        };
        Content = new StackPanel
        {
            Margin = new Thickness(28), Spacing = 22,
            Children = { new TextBlock { Text = "弹幕API", FontSize = 26, FontWeight = FontWeight.SemiBold }, _message, _progress,
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Children = { _repair, cancel } } },
        };
    }
    public void ShowProgress(DanmuApi.Platform.BundledRuntimeProgress progress)
    {
        var (phase, next) = progress.Phase switch
        {
            "ReadingManifest" => ("读取运行环境清单", "接下来检查已有环境"),
            "ReadingState" => ("读取已有运行环境记录", "接下来检查依赖"),
            "CheckingTargetMetadata" => ("检查已有文件信息", "接下来校验已有依赖"),
            "VerifyingTarget" => ("校验已有依赖", "接下来确认是否需要更新"),
            "VerifyingSource" => ("校验内置运行环境", "接下来检查冲突并备份原文件"),
            "CheckingConflicts" => ("检查依赖冲突", "接下来备份或登记已有文件"),
            "BackingUp" => ("备份并校验原文件", "接下来暂存新依赖"),
            "Staging" => ("暂存并校验新依赖", "接下来记录事务并替换文件"),
            "WritingJournal" => ("保存恢复记录", "接下来替换依赖"),
            "Replacing" => ("替换依赖文件", "接下来记录文件信息、提交并清理备份"),
            "Snapshotting" => ("记录文件信息", "接下来提交并清理备份"),
            "Committing" => ("提交运行环境记录", "提交开始后将完成清理，取消不再回滚"),
            "InspectingCleanup" => ("检查待清理的备份", "更新已提交，正在统计文件和目录"),
            "CleaningUp" => ("清理备份文件和目录", "更新已提交，清理完成后继续启动"),
            "Recovering" => ("恢复运行环境或完成清理", "请等待恢复结束；失败原因将明确显示"),
            "Completed" => ("运行环境准备完成", "接下来继续应用启动"),
            _ => ($"正在处理运行环境（{progress.Phase}）", "等待当前操作完成"),
        };
        var counter = progress.Total > 0 ? $"当前阶段 {progress.Completed} / {progress.Total}" : "正在处理…";
        _message.Text = $"{phase} · {counter}\n{next}";
        _progress.IsVisible = true;
        _progress.IsIndeterminate = progress.Total == 0;
        _progress.Value = progress.Total > 0 ? progress.Completed * 100d / progress.Total : 0;
    }
    public void ShowFailure(string message, bool allowRepair = true)
    {
        Height = 360;
        _repair.IsVisible = allowRepair;
        _progress.IsVisible = false;
        _message.Text = "启动准备失败，未启动服务。\n" + message;
    }
}
