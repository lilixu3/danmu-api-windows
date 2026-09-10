using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using DanmuApi.Core;

namespace DanmuApi.App.Views;

/// <summary>
/// 核心详情弹窗的共享构件：逐文件代码变动列表（对齐移动端「提交变动详情」
/// 的展开式 diff 渲染）与摘要卡片。diff 行配色固定，不随主题漂移。
/// </summary>
internal static class CoreDetailControls
{
    internal static readonly IBrush AddedForeground = new SolidColorBrush(Color.Parse("#4BC887"));
    internal static readonly IBrush AddedBackground = new SolidColorBrush(Color.Parse("#2EA04329"));
    internal static readonly IBrush RemovedForeground = new SolidColorBrush(Color.Parse("#F06B78"));
    internal static readonly IBrush RemovedBackground = new SolidColorBrush(Color.Parse("#E0525C24"));
    internal static readonly IBrush HunkForeground = new SolidColorBrush(Color.Parse("#7D8899"));

    internal static TextBlock Muted(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        block.Classes.Add("body-muted");
        return block;
    }

    internal static TextBlock Mono(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        block.Classes.Add("mono-text");
        return block;
    }

    internal static Border Card(Thickness padding, params Control[] children)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var child in children)
        {
            panel.Children.Add(child);
        }

        var border = new Border
        {
            Padding = padding,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = panel,
        };
        border.Classes.Add("muted-row");
        return border;
    }

    internal static Control MetricsRow(int additions, int deletions, int files)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
        };
        row.Children.Add(Metric($"+{additions.ToString(CultureInfo.InvariantCulture)}", AddedForeground));
        row.Children.Add(Metric($"−{deletions.ToString(CultureInfo.InvariantCulture)}", RemovedForeground));
        row.Children.Add(Metric($"{files.ToString(CultureInfo.InvariantCulture)} 个文件", HunkForeground));
        return row;
    }

    private static TextBlock Metric(string text, IBrush brush) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeight.SemiBold,
        Foreground = brush,
    };

    /// <summary>文件变动列表：每行可展开查看 unified diff；GitHub 未提供 patch 时显式说明原因。</summary>
    internal static Control BuildFileDiffList(IReadOnlyList<GithubFileChange> files)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var file in files)
        {
            panel.Children.Add(BuildFileEntry(file));
        }

        return panel;
    }

    private static Control BuildFileEntry(GithubFileChange file)
    {
        var pathText = new TextBlock
        {
            Text = file.Path,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(pathText, file.PreviousPath is null ? file.Path : $"{file.PreviousPath} → {file.Path}");
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            ColumnSpacing = 10,
        };
        Grid.SetColumn(pathText, 0);
        header.Children.Add(pathText);
        var status = Muted(StatusText(file.Status));
        Grid.SetColumn(status, 1);
        header.Children.Add(status);
        var additions = Metric($"+{file.Additions.ToString(CultureInfo.InvariantCulture)}", AddedForeground);
        Grid.SetColumn(additions, 2);
        header.Children.Add(additions);
        var deletions = Metric($"−{file.Deletions.ToString(CultureInfo.InvariantCulture)}", RemovedForeground);
        Grid.SetColumn(deletions, 3);
        header.Children.Add(deletions);

        Control content = file.Patch is null
            ? Muted(file.PatchUnavailableReason ?? "GitHub 未提供该文件的文本变动")
            : BuildDiffLines(file.Patch);
        var diffBorder = new Border
        {
            Padding = new Thickness(0, 6, 0, 0),
            Child = content,
        };
        return new Expander
        {
            Header = header,
            Content = diffBorder,
            IsExpanded = false,
        };
    }

    private static Control BuildDiffLines(string patch)
    {
        var panel = new StackPanel { Spacing = 0 };
        foreach (var line in UnifiedDiffParser.Parse(patch))
        {
            if (line.Kind == DiffLineKind.Meta)
            {
                continue;
            }

            var block = new TextBlock
            {
                Text = line.Kind switch
                {
                    DiffLineKind.Added => "+ " + line.Text,
                    DiffLineKind.Removed => "- " + line.Text,
                    _ => line.Text,
                },
                FontFamily = FontFamily.Parse("Consolas, Cascadia Mono, monospace"),
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
                Padding = new Thickness(8, 1, 8, 1),
            };
            switch (line.Kind)
            {
                case DiffLineKind.Added:
                    block.Foreground = AddedForeground;
                    block.Background = AddedBackground;
                    break;
                case DiffLineKind.Removed:
                    block.Foreground = RemovedForeground;
                    block.Background = RemovedBackground;
                    break;
                case DiffLineKind.Hunk:
                    block.Foreground = HunkForeground;
                    break;
            }

            panel.Children.Add(block);
        }

        return new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 420,
        };
    }

    internal static string StatusText(string status) => status switch
    {
        "added" => "新增",
        "removed" => "删除",
        "modified" => "修改",
        "renamed" => "重命名",
        "changed" => "变更",
        "unchanged" => "未变",
        _ => status,
    };

    internal static string FormatTime(DateTimeOffset? value) => value is null
        ? "时间未知"
        : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>提交变动详情：提交说明 + 统计 + 逐文件 diff；「回退到此版本」返回 true。</summary>
public sealed class CommitDetailsWindow : Window
{
    public CommitDetailsWindow(GithubCommitDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var commit = details.Commit;
        Title = $"提交变动详情 · {commit.ShortSha}";
        Width = 980;
        Height = 640;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var body = commit.Message.Split('\n', 2);
        var description = body.Length > 1 ? body[1].Trim() : string.Empty;

        var summary = CoreDetailControls.Card(
            new Thickness(16),
            new TextBlock { Text = commit.Title, FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
            CoreDetailControls.Muted($"{commit.Author ?? "未知作者"} · {CoreDetailControls.FormatTime(commit.CommittedAt)} · {commit.Sha}"),
            CoreDetailControls.MetricsRow(details.Additions, details.Deletions, details.ChangedFiles));
        var descriptionCard = description.Length == 0
            ? null
            : CoreDetailControls.Card(
                new Thickness(16),
                new TextBlock { Text = "提交说明", FontWeight = FontWeight.SemiBold },
                CoreDetailControls.Muted(description));
        var filesHeading = new TextBlock
        {
            Text = details.Files.Count == 0 ? "变更文件（GitHub 未返回文件级变动）" : "变更文件",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
        };

        var close = new Button { Content = "关闭", MinWidth = 88, IsCancel = true };
        close.Classes.Add("secondary-action");
        close.Click += (_, _) => Close(false);
        var rollback = new Button { Content = "回退到此版本", MinWidth = 120, IsDefault = true };
        rollback.Classes.Add("primary-action");
        rollback.Click += (_, _) => Close(true);
        Content = CoreDetailWindowShell.Build(
            "提交变动详情",
            "逐文件查看增加与删除的代码",
            new Control?[] { summary, descriptionCard, filesHeading, CoreDetailControls.BuildFileDiffList(details.Files) },
            close,
            rollback);
    }
}

/// <summary>PR 详情：实验版本警示 + 元数据 + 文件变动；「安装此 PR」返回 true。</summary>
public sealed class PullRequestDetailsWindow : Window
{
    public PullRequestDetailsWindow(GithubPullRequest pullRequest, IReadOnlyList<GithubFileChange> files)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        Title = $"PR #{pullRequest.Number.ToString(CultureInfo.InvariantCulture)} 详情（实验版本）";
        Width = 980;
        Height = 660;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var warning = new TextBlock
        {
            Text = "实验版本：来自贡献者的 head 仓库，不经过稳定版校验，安装后可随时回退。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#D97706")),
        };
        var summary = CoreDetailControls.Card(
            new Thickness(16),
            new TextBlock { Text = pullRequest.Title, FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
            CoreDetailControls.Muted($"#{pullRequest.Number.ToString(CultureInfo.InvariantCulture)} · {pullRequest.Author ?? "未知作者"} · {pullRequest.State} · 更新于 {CoreDetailControls.FormatTime(pullRequest.UpdatedAt)}"),
            CoreDetailControls.Mono($"{pullRequest.HeadRepository}@{pullRequest.HeadBranch} → {pullRequest.BaseBranch}"),
            CoreDetailControls.Mono($"head {pullRequest.HeadSha}"),
            pullRequest.Additions is int additions && pullRequest.Deletions is int deletions && pullRequest.ChangedFiles is int changed
                ? CoreDetailControls.MetricsRow(additions, deletions, changed)
                : CoreDetailControls.Muted("GitHub 未返回该 PR 的统计信息"));
        var body = string.IsNullOrWhiteSpace(pullRequest.Body)
            ? null
            : CoreDetailControls.Card(
                new Thickness(16),
                new TextBlock { Text = "PR 描述", FontWeight = FontWeight.SemiBold },
                CoreDetailControls.Muted(pullRequest.Body));
        var filesHeading = new TextBlock
        {
            Text = files.Count == 0 ? "文件变动（GitHub 未返回）" : "文件变动",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
        };

        var close = new Button { Content = "关闭", MinWidth = 88, IsCancel = true };
        close.Classes.Add("secondary-action");
        close.Click += (_, _) => Close(false);
        var install = new Button { Content = "安装此 PR（实验版本）", MinWidth = 160, IsDefault = true };
        install.Classes.Add("primary-action");
        install.Click += (_, _) => Close(true);
        Content = CoreDetailWindowShell.Build(
            $"PR #{pullRequest.Number.ToString(CultureInfo.InvariantCulture)}（实验版本）",
            "从 head 仓库安装到自定义核心，随时可回退",
            new Control?[] { warning, summary, body, filesHeading, CoreDetailControls.BuildFileDiffList(files) },
            close,
            install);
    }
}

/// <summary>更新详情：变更总结 + 提交记录 + 逐文件 diff；「立即更新」返回 true。</summary>
public sealed class UpdateDetailsWindow : Window
{
    public UpdateDetailsWindow(GithubCompareResult comparison, string localDisplay, string remoteDisplay)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        Title = "核心更新详情";
        Width = 980;
        Height = 680;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var summary = CoreDetailControls.Card(
            new Thickness(16),
            new TextBlock { Text = "变更总结", FontSize = 15, FontWeight = FontWeight.SemiBold },
            new TextBlock
            {
                Text = $"{localDisplay} → {remoteDisplay}",
                FontSize = 16,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            },
            CoreDetailControls.Muted(ComparisonHeadline(comparison)),
            CoreDetailControls.MetricsRow(comparison.Additions, comparison.Deletions, comparison.Files.Count));
        var truncation = comparison.CommitsTruncated || comparison.FilesTruncated
            ? CoreDetailControls.Muted("变更数量较多，GitHub 仅返回了本次可展示的部分详情。")
            : null;
        var commitsHeading = new TextBlock
        {
            Text = $"提交记录（{comparison.TotalCommits.ToString(CultureInfo.InvariantCulture)}）",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
        };
        var commitList = new StackPanel { Spacing = 6 };
        foreach (var commit in comparison.Commits)
        {
            commitList.Children.Add(CoreDetailControls.Card(
                new Thickness(12, 9),
                new TextBlock { Text = commit.Title, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.Medium },
                CoreDetailControls.Muted($"{commit.ShortSha} · {commit.Author ?? "未知作者"} · {CoreDetailControls.FormatTime(commit.CommittedAt)}")));
        }

        var filesHeading = new TextBlock
        {
            Text = "代码变更详情",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
        };

        var later = new Button { Content = "稍后", MinWidth = 88, IsCancel = true };
        later.Classes.Add("secondary-action");
        later.Click += (_, _) => Close(false);
        var update = new Button { Content = "立即更新", MinWidth = 110, IsDefault = true };
        update.Classes.Add("primary-action");
        update.Click += (_, _) => Close(true);
        Content = CoreDetailWindowShell.Build(
            "更新详情",
            $"本地与远端相差 {comparison.TotalCommits.ToString(CultureInfo.InvariantCulture)} 个提交，可逐文件核对后再更新",
            new Control?[] { summary, truncation, commitsHeading, commitList, filesHeading, CoreDetailControls.BuildFileDiffList(comparison.Files) },
            later,
            update);
    }

    private static string ComparisonHeadline(GithubCompareResult comparison) => comparison.Status switch
    {
        "ahead" => $"远端领先本地 {comparison.AheadBy.ToString(CultureInfo.InvariantCulture)} 个提交",
        "behind" => $"本地领先远端 {comparison.BehindBy.ToString(CultureInfo.InvariantCulture)} 个提交",
        "diverged" => $"本地与远端各有新提交（远端 +{comparison.AheadBy.ToString(CultureInfo.InvariantCulture)} / 本地 +{comparison.BehindBy.ToString(CultureInfo.InvariantCulture)}）",
        "identical" => "本地与远端指向同一提交",
        _ => $"GitHub 比较状态：{comparison.Status}",
    };
}

/// <summary>详情弹窗统一外壳：标题 + 副标题 + 可滚动内容 + 底部按钮。</summary>
internal static class CoreDetailWindowShell
{
    internal static Control Build(
        string title,
        string subtitle,
        IReadOnlyList<Control?> content,
        Button close,
        Button primary)
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var control in content)
        {
            if (control is not null)
            {
                panel.Children.Add(control);
            }
        }

        var scroll = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(24, 14, 24, 0),
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(24, 12, 24, 18),
            Children = { close, primary },
        };
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
        };
        Grid.SetRow(scroll, 1);
        Grid.SetRow(actions, 2);
        grid.Children.Add(new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(24, 18, 24, 0),
            Children =
            {
                new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold },
                CoreDetailControls.Muted(subtitle),
            },
        });
        grid.Children.Add(scroll);
        grid.Children.Add(actions);
        return grid;
    }
}
