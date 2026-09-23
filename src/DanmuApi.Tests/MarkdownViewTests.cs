using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DanmuApi.App.Controls;
using DanmuApi.App.Services;

namespace DanmuApi.Tests;

/// <summary>
/// Markdown 渲染：解析交给 Markdig，断言落在"渲染出来的控件树"上 ——
/// 因为用户看到的是控件树，只看解析结果无法发现"解析对了但没画出来"。
/// 覆盖范围与移动端的 GFM 契约一致（表格、删除线、任务清单、裸链接）。
/// </summary>
public sealed class MarkdownViewTests
{
    private const string GfmSample = """
        | 状态 | 说明 |
        | --- | --- |
        | 完成 | ~~旧实现~~ 新实现 |

        - [x] 已完成
        - [ ] 待处理

        https://github.com/owner/repo/pull/1
        """;

    [AvaloniaFact]
    public void GfmSampleRendersTableTaskListStrikethroughAndAutolink()
    {
        var (window, view, panel) = Render(GfmSample);
        try
        {
            var descendants = panel.GetVisualDescendants().ToList();

            // 表格：表头 + 1 行数据 = 4 个单元格（ScrollViewer 内部也有 Grid，靠"子项全是单元格 Border"区分）。
            var table = Assert.Single(descendants.OfType<Grid>(),
                grid => grid.Children.Count == 4 && grid.Children.All(child => child is Border));
            Assert.All(table.Children.OfType<Border>(), cell => Assert.IsType<StackPanel>(cell.Child));
            // 每行两个单元格必须落在不同列：漏掉 ColumnDefinitions 时全部挤在第 0 列、文字互相压住。
            Assert.Equal(2, table.ColumnDefinitions.Count);
            foreach (var rowIndex in new[] { 0, 1 })
            {
                var columns = table.Children.Cast<Control>().Where(child => Grid.GetRow(child) == rowIndex)
                    .Select(Grid.GetColumn).OrderBy(value => value).ToArray();
                Assert.Equal([0, 1], columns);
            }

            // 删除线：~~旧实现~~ 必须带上删除线（而不是原样显示波浪线）。
            var strikethrough = descendants.OfType<TextBlock>()
                .SelectMany(text => (IEnumerable<Inline>?)text.Inlines ?? [])
                .OfType<Run>()
                .Where(run => run.TextDecorations == TextDecorations.Strikethrough)
                .ToArray();
            Assert.Contains(strikethrough, run => (run.Text ?? string.Empty).Contains("旧实现", StringComparison.Ordinal));
            Assert.DoesNotContain("~~", VisibleText(panel), StringComparison.Ordinal);

            // 任务清单：两个复选框，一个勾选一个未勾选，且都不可交互。
            var boxes = descendants.OfType<CheckBox>().ToArray();
            Assert.Equal(2, boxes.Length);
            Assert.Contains(boxes, box => box.IsChecked == true);
            Assert.Contains(boxes, box => box.IsChecked == false);
            Assert.All(boxes, box => Assert.False(box.IsEnabled));

            // 裸链接（GFM autolink）：渲染成可点按钮而不是纯文本。
            Assert.Single(descendants.OfType<Button>(),
                button => IsLink(button, "https://github.com/owner/repo/pull/1"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void HeadingsListsQuotesCodeAndRulesRenderAsSeparateControls()
    {
        var markdown = """
            # 一级标题

            段落里有 **粗体**、*斜体* 与 `行内代码`。

            > 引用一行

            ---

            - 无序一项
            - 无序二项

            1. 第一项
            2. 第二项

            ```csharp
            var x = 1;
            ```

            ```js
            let y = 2;
            ```
            """;
        var (window, view, panel) = Render(markdown);
        try
        {
            var texts = panel.GetVisualDescendants().OfType<TextBlock>().ToArray();
            var visible = texts.Select(ExtractText).ToArray();
            Assert.Contains(visible, text => text == "一级标题");
            Assert.Contains(visible, text => text == "•");
            Assert.Contains(visible, text => text == "1.");
            Assert.Contains(visible, text => text == "2.");
            // 标题字号比正文大、字重是 SemiBold。
            var heading = Assert.Single(texts, text => ExtractText(text) == "一级标题");
            Assert.Equal(FontWeight.SemiBold, heading.FontWeight);
            Assert.True(heading.FontSize > 12.5);

            var runs = texts.SelectMany(text => (IEnumerable<Inline>?)text.Inlines ?? []).OfType<Run>().ToArray();
            Assert.Contains(runs, run => (run.Text ?? string.Empty).Contains("粗体", StringComparison.Ordinal) && run.FontWeight == FontWeight.Bold);
            Assert.Contains(runs, run => (run.Text ?? string.Empty).Contains("斜体", StringComparison.Ordinal) && run.FontStyle == FontStyle.Italic);
            Assert.Contains(runs, run =>
                (run.Text ?? string.Empty).Contains("行内代码", StringComparison.Ordinal) &&
                run.FontFamily.ToString().Contains("Consolas", StringComparison.OrdinalIgnoreCase));

            // 引用是一根左侧竖线；分隔线是一条细分隔 Border。
            Assert.Contains(panel.GetVisualDescendants().OfType<Border>(),
                border => border.BorderThickness.Left == 3 && border.BorderThickness.Top == 0);
            Assert.Contains(panel.GetVisualDescendants().OfType<Border>(),
                border => border.Height == 1);

            // 代码块：等宽显示、保留原始空白，并显示语言标签。
            var code = Assert.Single(texts, text => ExtractText(text).Contains("var x = 1;", StringComparison.Ordinal));
            Assert.Contains("Consolas", code.FontFamily.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(visible, text => text == "csharp");
            Assert.Contains(visible, text => text == "js");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DisallowedSchemesAreNotRenderedAsClickableLinks()
    {
        var markdown = """
            [站内](javascript:alert(1))
            [本地文件](file:///C:/Windows/System32/calc.exe)
            [正常](https://example.com/page)
            """;
        var (window, view, panel) = Render(markdown);
        try
        {
            // 只有 https 那条渲染成按钮；危险协议只剩标签文字，
            // 不保留"看着能点、点了没反应"的死链接，也不把目标地址暴露成可点内容。
            var link = Assert.Single(panel.GetVisualDescendants().OfType<Button>());
            Assert.True(IsLink(link, "https://example.com/page"));
            var visible = VisibleText(panel);
            Assert.Contains("站内", visible, StringComparison.Ordinal);
            Assert.Contains("本地文件", visible, StringComparison.Ordinal);
            Assert.DoesNotContain("javascript:", visible, StringComparison.Ordinal);
            Assert.DoesNotContain("file:///", visible, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RawHtmlIsShownAsTextInsteadOfBeingInterpretedOrDropped()
    {
        var markdown = "<div align=\"center\">居中内容</div>\n\n正常段落";
        var (window, view, panel) = Render(markdown);
        try
        {
            var visible = VisibleText(panel);
            // 不解释 HTML，但也不能把内容吞掉：原始标签原样可见。
            Assert.Contains("<div align=\"center\">居中内容</div>", visible, StringComparison.Ordinal);
            Assert.Contains("正常段落", visible, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CrlfBodiesKeepTablesIntact()
    {
        var markdown = string.Join("\r\n",
        [
            "> 该说明位于表格之前",
            "",
            "## 影响范围",
            "",
            "| 场景 | 行为变化 |",
            "| --- | :---: |",
            "| 冷启动 | 自动刷新 |",
            "| 冷启动2 | 自动刷新2 |",
        ]);
        var (window, view, panel) = Render(markdown);
        try
        {
            Assert.Contains("影响范围", VisibleText(panel), StringComparison.Ordinal);
            // 两列表头 + 两行数据 => 6 个单元格。
            var grid = Assert.Single(panel.GetVisualDescendants().OfType<Grid>(), candidate => candidate.Children.Count == 6);
            Assert.Equal(6, grid.Children.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EmptyMarkdownRendersNothing()
    {
        var view = new MarkdownView { Markdown = "   " };
        Assert.Null(view.Content);

        var window = new Window { Width = 400, Height = 200, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Null(view.Content);

            // 清空后也要跟着清掉，不能留着上一次的内容。
            view.Markdown = "# 标题";
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(view.Content);
            view.Markdown = null;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(view.Content);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MarkdownPreviewFilesAreSavedWhenRenderDirectoryIsSet()
    {
        // 设了 DANMU_TEST_RENDER_DIRECTORY 就把真实渲染结果落盘，供人工核对观感。
        var directory = Environment.GetEnvironmentVariable("DANMU_TEST_RENDER_DIRECTORY");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var markdown = """
            # 版本 v1.21.0

            ## 影响范围

            该说明位于表格之前，**重点在这里**，还有 ~~废弃说明~~ 与 `src/index.js`。

            | 场景 | 行为变化 |
            | --- | :---: |
            | 仅 renren 部署 | **整次请求不触碰 Bangumi Data，零下载** |
            | 冷启动或过期刷新 | 仍触发下载；完整说明不应被截断 |
            | 前端由关到开 | **立即触发下载**，并同步更新运行时状态 |

            ### 待办

            - [x] 缓存逻辑已更新
            - [ ] 文档待补充
            - 普通无序项

            1. 第一步
            2. 第二步

            > 引用：构建只读取 GitHub，不会修改远程仓库。

            ```js
            export const VERSION = '1.21.0';
            ```

            参考 [PR #12](https://github.com/owner/repo/pull/12) 与裸链接 https://example.com/docs

            [危险链接不会变成可点](javascript:alert(1))

            <div align="center">这段 HTML 按原文显示</div>
            """;

        var view = new MarkdownView { Markdown = markdown, BaseFontSize = 12.5 };
        var window = new Window
        {
            Width = 820,
            Height = 1180,
            Padding = new Thickness(20),
            Content = view,
        };
        // 背景必须跟随主题：写死白色会让深色预览变成"浅字压白底"，看起来像渲染坏了，
        // 其实只是预览用的画布不对。
        window.Bind(Window.BackgroundProperty, new DynamicResourceExtension("AppBackgroundBrush"));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        try
        {
            using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new Avalonia.PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height)))
            {
                bitmap.Render(window);
                bitmap.Save(Path.Combine(directory, "markdown-light.png"));
            }

            // 深色主题也要落一张：主题 token 解析错了只在某一种主题下才看得出来。
            window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new Avalonia.PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height)))
            {
                bitmap.Render(window);
                bitmap.Save(Path.Combine(directory, "markdown-dark.png"));
            }

            // 表格行必须各自占一行：把每个单元格的 Grid.Row 与实测位置写出来，
            // 免得"看起来重叠"只能靠肉眼判断。
            var lines = new List<string>();
            foreach (var grid in window.GetVisualDescendants().OfType<Grid>()
                .Where(candidate => candidate.Children.Count > 0 && candidate.Children.All(child => child is Border)))
            {
                lines.Add($"grid rows={grid.RowDefinitions.Count} cols={grid.ColumnDefinitions.Count} children={grid.Children.Count}");
                foreach (var child in grid.Children.OfType<Border>())
                {
                    lines.Add($"  row={Grid.GetRow(child)} col={Grid.GetColumn(child)} x={child.Bounds.X:F1} w={child.Bounds.Width:F1} y={child.Bounds.Y:F1} h={child.Bounds.Height:F1}");
                }
            }

            File.WriteAllLines(Path.Combine(directory, "table-layout.txt"), lines);
        }
        finally
        {
            window.Close();
        }
    }

    private static bool IsLink(Button button, string url) =>
        button.Content is TextBlock block && ExtractText(block).Length > 0 &&
        ToolTip.GetTip(button) is string tip && tip == url;

    /// <summary>控件的可见文字：用 Inlines 渲染时 <see cref="TextBlock.Text"/> 是空的，
    /// 断言必须从 Run 里取，否则"看起来渲染失败"其实是断言取错了地方。</summary>
    private static string ExtractText(TextBlock block)
    {
        if (!string.IsNullOrEmpty(block.Text))
        {
            return block.Text;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var inline in block.Inlines ?? [])
        {
            Append(builder, inline);
        }

        return builder.ToString();

        static void Append(System.Text.StringBuilder builder, Inline inline)
        {
            switch (inline)
            {
                case Run run:
                    builder.Append(run.Text);
                    break;
                case LineBreak:
                    builder.Append('\n');
                    break;
                case InlineUIContainer { Child: TextBlock nested }:
                    builder.Append(ExtractText(nested));
                    break;
                case InlineUIContainer { Child: Button { Content: TextBlock label } }:
                    builder.Append(ExtractText(label));
                    break;
                case Span span:
                    foreach (var child in span.Inlines)
                    {
                        Append(builder, child);
                    }

                    break;
            }
        }
    }

    private static string VisibleText(Control root) => string.Join(
        " ",
        root.GetVisualDescendants().OfType<TextBlock>()
            .Select(ExtractText)
            .Where(text => text.Length > 0));

    private static (Window Window, MarkdownView View, StackPanel Panel) Render(string markdown)
    {
        var view = new MarkdownView { Markdown = markdown };
        var window = new Window
        {
            Width = 900,
            Height = 700,
            Content = view,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (window, view, Assert.IsType<StackPanel>(view.Content));
    }
}

public sealed class MarkdownUriPolicyTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo/pull/1")]
    [InlineData("https://github.com/owner/repo/issues/中文说明")]
    [InlineData("http://example.com/path")]
    [InlineData("mailto:owner@example.com")]
    public void LinksAllowWebAndEmailSchemes(string url) => Assert.True(MarkdownUriPolicy.CanOpenLink(url));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("content://settings/system")]
    [InlineData("/owner/repo/issues/1")]
    [InlineData("https://")]
    [InlineData("mailto:")]
    [InlineData("")]
    [InlineData(null)]
    public void LinksRejectLocalExecutableAndMalformedSchemes(string? url) =>
        Assert.False(MarkdownUriPolicy.CanOpenLink(url));

    [Theory]
    [InlineData("https://user-images.githubusercontent.com/a.png")]
    [InlineData("https://example.com/预览/图片.png")]
    public void ImagesRequireAbsoluteHttpsUrls(string url) => Assert.True(MarkdownUriPolicy.CanLoadImage(url));

    [Theory]
    [InlineData("http://example.com/a.png")]
    [InlineData("file:///C:/a.png")]
    [InlineData("//example.com/a.png")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData(null)]
    public void ImagesRejectEverythingElse(string? url) => Assert.False(MarkdownUriPolicy.CanLoadImage(url));
}
