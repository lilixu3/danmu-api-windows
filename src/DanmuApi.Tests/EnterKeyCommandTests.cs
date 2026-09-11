using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using DanmuApi.App.Controls;

namespace DanmuApi.Tests;

/// <summary>
/// 「输入框回车提交」行为：以前每个页面都只能用鼠标点旁边那个按钮，
/// 键盘流用户敲完关键词按回车没反应。
/// </summary>
public sealed class EnterKeyCommandTests
{
    [AvaloniaFact]
    public void EnterExecutesTheAttachedCommand()
    {
        var executed = 0;
        var box = new TextBox();
        EnterKeyCommand.SetCommand(box, new RecordingCommand(() => executed++));

        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

        Assert.Equal(1, executed);
    }

    [AvaloniaFact]
    public void OtherKeysDoNotExecute()
    {
        var executed = 0;
        var box = new TextBox();
        EnterKeyCommand.SetCommand(box, new RecordingCommand(() => executed++));

        foreach (var key in new[] { Key.Space, Key.Tab, Key.Escape, Key.A })
        {
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        }

        Assert.Equal(0, executed);
    }

    /// <summary>
    /// 多行输入框里回车是换行，不能被当成提交（否则 JSON 请求体没法换行）。
    /// </summary>
    [AvaloniaFact]
    public void MultilineTextBoxKeepsEnterAsNewline()
    {
        var executed = 0;
        var box = new TextBox { AcceptsReturn = true };
        EnterKeyCommand.SetCommand(box, new RecordingCommand(() => executed++));

        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

        Assert.Equal(0, executed);
    }

    /// <summary>
    /// 回车提交后必须把事件标记为已处理，否则同一次回车还会冒泡去触发
    /// 所在对话框的默认按钮，一个动作执行两遍。
    /// </summary>
    [AvaloniaFact]
    public void EnterMarksEventHandledSoDefaultButtonDoesNotAlsoFire()
    {
        var submitted = 0;
        var defaultButtonFired = 0;
        var box = new TextBox();
        EnterKeyCommand.SetCommand(box, new RecordingCommand(() => submitted++));

        var panel = new StackPanel();
        panel.Children.Add(box);
        var defaultButton = new Button { IsDefault = true, Content = "确定" };
        defaultButton.Click += (_, _) => defaultButtonFired++;
        panel.Children.Add(defaultButton);

        var window = new Window { Width = 300, Height = 200, Content = panel };
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            box.Focus();
            var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
            box.RaiseEvent(args);

            Assert.Equal(1, submitted);
            Assert.True(args.Handled);
            Assert.Equal(0, defaultButtonFired);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 命令不可执行时不得执行；把命令换成 null 后回车必须彻底失效（订阅要真的摘掉）。
    /// </summary>
    [AvaloniaFact]
    public void UnavailableOrClearedCommandDoesNothing()
    {
        var executed = 0;
        var box = new TextBox();
        EnterKeyCommand.SetCommand(box, new RecordingCommand(() => executed++, canExecute: false));
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(0, executed);

        EnterKeyCommand.SetCommand(box, new RecordingCommand(() => executed++));
        EnterKeyCommand.SetCommand(box, null);
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(0, executed);
    }

    /// <summary>
    /// 各页面的搜索/输入框必须真的挂上回车命令，且挂的是这一页该执行的那个命令。
    ///
    /// 这里查的是 XAML 源码而不是运行期属性值：附加属性是通过 Binding 赋的，
    /// 视图没有真实 DataContext 时绑定不产出值，`GetCommand` 会读回 null，
    /// 运行期断言只能得出「null」，证明不了有没有挂。命令名逐一写死，
    /// 页面命令改名或漏挂都会在这里失败。
    /// </summary>
    [Theory]
    [InlineData("ManualSearchView.axaml", "EnterKeyCommand.Command=\"{Binding SearchCommand}\"")]
    [InlineData("AutoMatchView.axaml", "EnterKeyCommand.Command=\"{Binding MatchCommand}\"")]
    [InlineData("BangumiDetailsView.axaml", "EnterKeyCommand.Command=\"{Binding JumpToEpisodeCommand}\"")]
    [InlineData("DanmuDownloadView.axaml", "EnterKeyCommand.Command=\"{Binding SearchCommand}\"")]
    [InlineData("SettingsView.axaml", "EnterKeyCommand.Command=\"{Binding AdminLoginCommand}\"")]
    [InlineData("BackupView.axaml", "EnterKeyCommand.Command=\"{Binding ListRemoteCommand}\"")]
    [InlineData(
        "ApiDebugView.axaml",
        "EnterKeyCommand.Command=\"{Binding $parent[ItemsControl].((vm:ApiDebugPageViewModel)DataContext).ExecuteCommand}\"")]
    public void ViewsWireEnterToTheirSubmitCommand(string fileName, string expectedBinding)
    {
        var path = Path.Combine(RepositoryRoot, "src", "DanmuApi.App", "Views", fileName);
        Assert.True(File.Exists(path), $"找不到视图文件：{path}");
        var xaml = File.ReadAllText(path);

        Assert.Contains(expectedBinding, xaml, StringComparison.Ordinal);
        // 命名空间前缀也要声明，否则 XAML 编译不过（这里只是给出更清楚的失败原因）。
        Assert.Contains("using:DanmuApi.App.Controls", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// 日志、请求记录、配置、收藏、弹幕详情这几页的输入框是「边输入边过滤」，
    /// 没有需要回车的提交动作，属于有意不挂。这条把它们列出来，
    /// 避免以后有人以为漏了而误加一个语义不清的回车行为。
    /// </summary>
    [Theory]
    [InlineData("LogsView.axaml")]
    [InlineData("RequestRecordsView.axaml")]
    [InlineData("ConfigurationView.axaml")]
    [InlineData("FavoritesView.axaml")]
    [InlineData("DanmuDetailsView.axaml")]
    public void LiveFilteredViewsIntentionallyHaveNoSubmitCommand(string fileName)
    {
        var path = Path.Combine(RepositoryRoot, "src", "DanmuApi.App", "Views", fileName);
        Assert.True(File.Exists(path), $"找不到视图文件：{path}");
        var xaml = File.ReadAllText(path);

        Assert.DoesNotContain("EnterKeyCommand.Command", xaml, StringComparison.Ordinal);
    }

    /// <summary>从测试输出目录往上找到含 src 的仓库根目录。</summary>
    private static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "src", "DanmuApi.App")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("找不到仓库根目录（含 src/DanmuApi.App 的目录）");
        }
    }

    private sealed class RecordingCommand(Action execute, bool canExecute = true) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute;

        public void Execute(object? parameter) => execute();

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
