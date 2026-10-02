using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using DanmuApi.App.Services;
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

    /// <summary>GitHub 正文（PR 描述、提交说明）都是 Markdown：统一走 MarkdownView 渲染。
    /// 解析失败时控件内部会退回原文，所以这里不做二次兜底；
    /// 链接打不开、图片加载失败都经 <paramref name="linkFailure"/> 上报给宿主的诊断链。</summary>
    internal static Control Markdown(string text, Action<string>? linkFailure = null, double baseFontSize = 12.5)
    {
        var view = new Controls.MarkdownView
        {
            Markdown = text,
            BaseFontSize = baseFontSize,
        };
        if (linkFailure is not null)
        {
            view.LinkOpenFailed += (_, url) => linkFailure($"Markdown 链接无法打开：{url}");
        }

        return view;
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
    public CommitDetailsWindow(GithubCommitDetails details, Action<string>? linkFailure = null)
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
                CoreDetailControls.Markdown(description, linkFailure));
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

/// <summary>PR 详情：只读展示元数据与文件变动（对齐移动端的 PR 详情面板）。
/// 这里没有安装入口 —— 合并只通过 PR 实验室的「构建本地 PR 组合」。</summary>
public sealed class PullRequestDetailsWindow : Window
{
    public PullRequestDetailsWindow(GithubPullRequest pullRequest, IReadOnlyList<GithubFileChange> files, Action<string>? linkFailure = null)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        Title = $"PR #{pullRequest.Number.ToString(CultureInfo.InvariantCulture)} 详情";
        Width = 980;
        Height = 660;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

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
                CoreDetailControls.Markdown(pullRequest.Body, linkFailure));
        var filesHeading = new TextBlock
        {
            Text = files.Count == 0 ? "文件变动（GitHub 未返回）" : "文件变动",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
        };

        var close = new Button { Content = "关闭", MinWidth = 88, IsCancel = true, IsDefault = true };
        close.Classes.Add("primary-action");
        close.Click += (_, _) => Close();
        Content = CoreDetailWindowShell.Build(
            $"PR #{pullRequest.Number.ToString(CultureInfo.InvariantCulture)}",
            "回到 PR 实验室用「加入队列」把它排进待构建的本地组合",
            new Control?[] { summary, body, filesHeading, CoreDetailControls.BuildFileDiffList(files) },
            close,
            null);
    }
}

/// <summary>
/// 「构建本地 PR 组合」确认框（对齐移动端 PR 实验室的构建对话框）：
/// 风险提示 + 有序队列 + 「安装后切换」开关。
/// </summary>
public sealed class PullRequestBuildConfirmWindow : Window
{
    private bool _activateAfterInstall;

    public PullRequestBuildConfirmWindow(PullRequestBuildPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        _activateAfterInstall = prompt.ActivateAfterInstall;
        Title = "构建本地 PR 组合";
        Width = 760;
        Height = 620;
        MinWidth = 520;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var warning = new TextBlock
        {
            Text = "PR 中的代码将在本机执行。构建只读取 GitHub，不会修改或合并远程仓库。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.Parse("#D97706")),
        };
        var target = CoreDetailControls.Card(
            new Thickness(16),
            new TextBlock { Text = $"目标：{prompt.VariantLabel} · {prompt.DisplayName}", FontWeight = FontWeight.SemiBold },
            CoreDetailControls.Mono($"{prompt.Repository}@{prompt.Branch}"),
            CoreDetailControls.Mono($"基线提交 {prompt.BaseCommitSha}"),
            prompt.InheritedPullRequestNumbers.Count == 0
                ? CoreDetailControls.Muted("基线之上只并入本次选择的 PR")
                : CoreDetailControls.Muted($"将继承已安装的本地组合：{Format(prompt.InheritedPullRequestNumbers)}"));

        var queue = new StackPanel { Spacing = 5 };
        for (var index = 0; index < prompt.PullRequests.Count; index++)
        {
            var pullRequest = prompt.PullRequests[index];
            queue.Children.Add(new TextBlock
            {
                Text = $"{index + 1}. #{pullRequest.Number.ToString(CultureInfo.InvariantCulture)} {pullRequest.Title}",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
            });
        }

        var activateTitle = new TextBlock { Text = "安装后切换", FontWeight = FontWeight.SemiBold, FontSize = 13 };
        var activateHint = CoreDetailControls.Muted("服务运行时会安全重启并保留恢复点");
        var toggle = new CheckBox
        {
            IsChecked = _activateAfterInstall,
            VerticalAlignment = VerticalAlignment.Center,
            Content = "安装完成后把运行核心切换到该变体",
        };
        toggle.IsCheckedChanged += (_, _) => _activateAfterInstall = toggle.IsChecked == true;

        var cancel = new Button { Content = "取消", MinWidth = 88, IsCancel = true };
        cancel.Classes.Add("secondary-action");
        cancel.Click += (_, _) => Close(PullRequestBuildConfirmation.Canceled);
        var confirm = new Button { Content = "开始构建", MinWidth = 110, IsDefault = true };
        confirm.Classes.Add("primary-action");
        confirm.Click += (_, _) => Close(new PullRequestBuildConfirmation(true, _activateAfterInstall));
        Content = CoreDetailWindowShell.Build(
            "构建本地 PR 组合",
            $"{prompt.PullRequests.Count.ToString(CultureInfo.InvariantCulture)} 个 PR 将按顺序并入 {prompt.VariantLabel} 的当前核心",
            new Control?[]
            {
                warning,
                target,
                CoreDetailControls.Card(new Thickness(16), (Control)new TextBlock { Text = "合并顺序", FontWeight = FontWeight.SemiBold }, queue),
                CoreDetailControls.Card(new Thickness(16), (Control)activateTitle, (Control)activateHint, (Control)toggle),
            },
            cancel,
            confirm);
    }

    private static string Format(IReadOnlyList<int> numbers) =>
        string.Join(" ", numbers.Select(number => $"#{number.ToString(CultureInfo.InvariantCulture)}"));
}

/// <summary>
/// 本地 PR 组合更新前的核对框：逐个 PR 说明远端最新提交**到底包不包含**它，
/// 让用户在"更新并重新并入 / 仅更新（丢掉这些改动）/ 取消"之间明确选一个。
///
/// 每一行都带证据（比对了哪个提交、GitHub 怎么说），因为这里最危险的错误是
/// "看着像包含、其实没包含"——用户必须能自己复核结论。
/// </summary>
public sealed class PullRequestStackUpdateWindow : Window
{
    public PullRequestStackUpdateWindow(PullRequestStackUpdatePrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        Title = "更新本地 PR 组合";
        Width = 780;
        Height = 640;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var target = CoreDetailControls.Card(
            new Thickness(16),
            new TextBlock { Text = $"目标：{prompt.VariantLabel} · {prompt.DisplayName}", FontWeight = FontWeight.SemiBold },
            CoreDetailControls.Mono($"{prompt.Repository}@{prompt.Branch}"),
            CoreDetailControls.Mono($"当前基线 {CorePullRequestPresenceReport.Shorten(prompt.BaseCommitSha)} → 远端最新 {CorePullRequestPresenceReport.Shorten(prompt.RemoteSha)}"));

        var panels = new List<Control?> { target };

        if (prompt.ContainedEntries.Count > 0)
        {
            panels.Add(BuildSection(
                $"远端已包含 {prompt.ContainedEntries.Count.ToString(CultureInfo.InvariantCulture)} 个 PR（更新后会失去「本地并入」的标记，但改动都在）",
                prompt.ContainedEntries,
                "✓"));
        }

        var notContained = prompt.Entries.Where(entry => entry.Presence != CorePullRequestPresence.Contained).ToArray();
        if (notContained.Length > 0)
        {
            panels.Add(BuildSection(
                $"远端不包含 {notContained.Length.ToString(CultureInfo.InvariantCulture)} 个 PR（直接更新会丢掉这些改动）",
                notContained,
                "✗"));
        }

        if (prompt.Diagnostics.Count > 0)
        {
            var diagnostics = new StackPanel { Spacing = 4 };
            diagnostics.Children.Add(new TextBlock { Text = "读取过程中的问题（不影响已给出的结论）", FontWeight = FontWeight.SemiBold, FontSize = 13 });
            foreach (var diagnostic in prompt.Diagnostics)
            {
                diagnostics.Children.Add(CoreDetailControls.Muted(diagnostic));
            }

            panels.Add(CoreDetailControls.Card(new Thickness(16), diagnostics));
        }

        var cancel = new Button { Content = "取消", MinWidth = 88, IsCancel = true };
        cancel.Classes.Add("secondary-action");
        cancel.Click += (_, _) => Close(PullRequestStackUpdateDecision.Canceled);

        var updateOnly = new Button { Content = "仅更新（丢掉这些改动）", MinWidth = 160 };
        updateOnly.Classes.Add("secondary-action");
        updateOnly.Click += (_, _) => Close(new PullRequestStackUpdateDecision(PullRequestStackUpdateChoice.UpdateOnly, []));

        var buttons = new List<Button> { updateOnly };
        if (prompt.ReMergeableNumbers.Count > 0)
        {
            var reMerge = new Button
            {
                Content = $"更新并重新并入 {prompt.ReMergeableNumbers.Count.ToString(CultureInfo.InvariantCulture)} 个 PR",
                MinWidth = 190,
                IsDefault = true,
            };
            reMerge.Classes.Add("primary-action");
            reMerge.Click += (_, _) => Close(new PullRequestStackUpdateDecision(
                PullRequestStackUpdateChoice.UpdateAndReMerge,
                prompt.ReMergeableNumbers));
            buttons.Insert(0, reMerge);
        }

        buttons.Add(cancel);
        Content = CoreDetailWindowShell.Build(
            "更新本地 PR 组合",
            "更新会把核心基线换成远端最新提交；远端没有的那些 PR 需要重新并入",
            panels,
            buttons.ToArray());
    }

    private static Control BuildSection(string title, IReadOnlyList<CorePullRequestPresenceEntry> entries, string marker)
    {
        var panel = new StackPanel { Spacing = 7 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 13 });
        foreach (var entry in entries)
        {
            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(new TextBlock
            {
                Text = $"{marker} #{entry.Number.ToString(CultureInfo.InvariantCulture)} {DescribeState(entry)}",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
            });
            row.Children.Add(CoreDetailControls.Muted(entry.Evidence));
            if (entry.Presence != CorePullRequestPresence.Contained && !entry.CanReMerge)
            {
                row.Children.Add(CoreDetailControls.Muted(DescribeBlockedReason(entry)));
            }

            panel.Children.Add(row);
        }

        return CoreDetailControls.Card(new Thickness(16), panel);
    }

    /// <summary>不能自动重新并入时要说清是哪种情况，别把"读不到"写成"已关闭"。</summary>
    private static string DescribeBlockedReason(CorePullRequestPresenceEntry entry) => entry switch
    {
        { IsMerged: true } => "无法自动重新并入：这个 PR 已经被合并，需要在 PR 实验室单独处理",
        { BaseBranchMatches: false } => "无法自动重新并入：PR 的目标分支已经变了，需要到 PR 实验室重新确认",
        { State: "closed" } => "无法自动重新并入：PR 已关闭且没有被合并",
        { Presence: CorePullRequestPresence.Unknown } => "无法自动重新并入：读不到这个 PR 当前是否还能合并，请刷新后重试",
        _ => "无法自动重新并入：这个 PR 当前不是可合并状态",
    };

    private static string DescribeState(CorePullRequestPresenceEntry entry) => entry.Presence switch
    {
        CorePullRequestPresence.Contained => "已包含",
        CorePullRequestPresence.Missing when entry.CanReMerge => "不包含（更新后会按最新 head 重新并入）",
        CorePullRequestPresence.Missing => "不包含",
        _ => "无法确认（更新后请自行复核）",
    };
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
        Button? primary) =>
        Build(title, subtitle, content, primary is null ? [close] : new[] { close, primary });

    /// <summary>多按钮版本：需要「取消 / 只更新 / 更新并重新并入」这类三选一时用（按钮顺序=传入顺序）。</summary>
    internal static Control Build(
        string title,
        string subtitle,
        IReadOnlyList<Control?> content,
        params Button[] actions)
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
        var actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(24, 12, 24, 18),
        };
        foreach (var action in actions)
        {
            actionRow.Children.Add(action);
        }
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
        };
        Grid.SetRow(scroll, 1);
        Grid.SetRow(actionRow, 2);
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
        grid.Children.Add(actionRow);
        return grid;
    }
}
