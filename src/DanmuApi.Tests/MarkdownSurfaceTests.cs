using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DanmuApi.App.Controls;
using DanmuApi.App.Views;
using DanmuApi.Core;

namespace DanmuApi.Tests;

/// <summary>
/// GitHub 提供的正文（PR 描述、提交说明、发行说明）必须走 Markdown 渲染，而不是当纯文本贴出来。
/// 这里断言的是真正的界面控件：只验证"解析器认识 Markdown"不能证明用户看到的是渲染结果。
/// </summary>
public sealed class MarkdownSurfaceTests
{
    private const string Body = """
        ## 影响范围

        | 场景 | 行为 |
        | --- | --- |
        | 冷启动 | **立即刷新** |

        - [x] 已完成
        """;

    [AvaloniaFact]
    public void PullRequestDescriptionIsRenderedAsMarkdown()
    {
        var files = new[] { new GithubFileChange("worker.js", null, "modified", 3, 1, 4, "@@", null) };
        var window = new PullRequestDetailsWindow(PullRequest(), files);
        try
        {
            Flush(window);
            var markdown = Assert.Single(window.GetVisualDescendants().OfType<MarkdownView>());
            Assert.Equal(Body, markdown.Markdown);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CommitDescriptionIsRenderedAsMarkdown()
    {
        var commit = new GithubCommit(
            new string('a', 40),
            "标题行",
            $"标题行\n\n{Body}",
            "dev",
            DateTimeOffset.UtcNow,
            []);
        var details = new GithubCommitDetails(commit, [], 1, 1, 1);
        var window = new CommitDetailsWindow(details);
        try
        {
            Flush(window);
            // 提交说明只取标题之后的正文部分，并且必须是 Markdown 控件。
            var markdown = Assert.Single(window.GetVisualDescendants().OfType<MarkdownView>());
            Assert.Contains("## 影响范围", markdown.Markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("标题行", markdown.Markdown, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MarkdownControlActuallyRendersTheBodyInsideTheDialog()
    {
        var files = new[] { new GithubFileChange("worker.js", null, "modified", 3, 1, 4, "@@", null) };
        var window = new PullRequestDetailsWindow(PullRequest(), files);
        try
        {
            Flush(window);
            var visible = string.Join(" ", window.GetVisualDescendants().OfType<TextBlock>()
                .Select(text => text.Text ?? InlineText(text))
                .Where(text => text.Length > 0));
            Assert.Contains("影响范围", visible, StringComparison.Ordinal);
            Assert.Contains("冷启动", visible, StringComparison.Ordinal);
            Assert.Contains("已完成", visible, StringComparison.Ordinal);
            // 表格标记与任务清单标记都不该以原始 Markdown 形式出现。
            Assert.DoesNotContain("| --- |", visible, StringComparison.Ordinal);
            Assert.DoesNotContain("[x]", visible, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>窗口必须先显示，内容才会挂进可视树；只构造不显示时可视树是空的。</summary>
    private static void Flush(Window window)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static string InlineText(TextBlock block) =>
        string.Concat((block.Inlines ?? []).OfType<Avalonia.Controls.Documents.Run>().Select(run => run.Text));

    private static GithubPullRequest PullRequest() => new(
        12,
        "标题",
        Body,
        "open",
        "contributor",
        "main",
        "contributor/danmu_api",
        "feature",
        new string('c', 40),
        false,
        false,
        DateTimeOffset.UtcNow,
        "https://github.com/owner/repo/pull/12",
        3,
        1,
        1);
}
