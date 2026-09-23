using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using DanmuApi.App.Services;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
// Avalonia 与 Markdig 都有 Inline：渲染目标用 Avalonia 的，遍历源用 Markdig 的，各自起别名。
using DocumentInline = Avalonia.Controls.Documents.Inline;
using MarkdigInline = Markdig.Syntax.Inlines.Inline;

namespace DanmuApi.App.Controls;

/// <summary>
/// GitHub 风格 Markdown 渲染控件：Markdig 只负责解析成 AST，渲染全部由原生 Avalonia 控件完成，
/// **不解释任何 HTML**（DisableHtml 让 &lt;div&gt; 一类内容原样显示，不静默丢弃）。
/// 支持标题、段落、粗体/斜体/删除线、行内代码、围栏代码块、有序/无序与嵌套列表、
/// 任务清单复选框、引用、分隔线、GFM 表格与裸链接；链接与图片都过
/// <see cref="MarkdownUriPolicy"/>，图片再经 <see cref="MarkdownImageLoader"/> 限额加载。
/// 语义与移动端 <c>SimpleMarkdownText</c> 对齐（同一套 GFM 子集与同一套 URI 策略）。
/// </summary>
public sealed class MarkdownView : ContentControl
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoLinks()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .DisableHtml()
        .Build();

    /// <summary>
    /// 主题 token 都定义在 <c>ThemeDictionaries</c> 里（Light/Dark 两套），
    /// 按 Application 级查找是取不到的 —— 必须让控件通过 DynamicResource 绑定，
    /// 由它在自己的可视树里按当前主题解析。以前这里用"查表 + 硬编码兜底色"，
    /// 查不到时静默套用深色兜底，浅色主题下就变成深底深字（实测不可读）。
    /// 现在没有兜底色：键缺了属性就保持默认，由测试断言 token 确实解析出来。
    /// </summary>
    private static void UseThemeBrush(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, new DynamicResourceExtension(key));

    private static readonly Lazy<MarkdownImageLoader> SharedImageLoader =
        new(() => new MarkdownImageLoader(), isThreadSafe: true);

    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    /// <summary>不传就用进程内共享的加载器（只允许 https、限量、带缓存）。</summary>
    public static readonly StyledProperty<MarkdownImageLoader?> ImageLoaderProperty =
        AvaloniaProperty.Register<MarkdownView, MarkdownImageLoader?>(nameof(ImageLoader));

    /// <summary>正文的基础字号；标题按它逐级放大，与核心页的正文尺度保持一致。</summary>
    public static readonly StyledProperty<double> BaseFontSizeProperty =
        AvaloniaProperty.Register<MarkdownView, double>(nameof(BaseFontSize), 12.5);

    public MarkdownView()
    {
        // 渲染结果是控件树，必须能撑满宽度、且默认不抢焦点。
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Focusable = false;
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public MarkdownImageLoader? ImageLoader
    {
        get => GetValue(ImageLoaderProperty);
        set => SetValue(ImageLoaderProperty, value);
    }

    public double BaseFontSize
    {
        get => GetValue(BaseFontSizeProperty);
        set => SetValue(BaseFontSizeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty ||
            change.Property == ImageLoaderProperty ||
            change.Property == BaseFontSizeProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var text = Markdown;
        if (string.IsNullOrWhiteSpace(text))
        {
            Content = null;
            return;
        }

        MarkdownDocument document;
        try
        {
            // GitHub 的正文经常是 CRLF：先规范化，否则表格与围栏识别会出偏差。
            document = MarkParsed(NormalizeLineEndings(text));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            // 解析器自身出问题也不能把内容弄丢：退回原文展示，并让调用方的诊断链能看到。
            Content = new SelectableTextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = BaseFontSize,
            };
            return;
        }

        var panel = new StackPanel { Spacing = 8 };
        var context = new RenderContext(this, panel);
        foreach (var block in document)
        {
            // 顶层块之间用间距而不是空段落分隔，避免"多个空行"挤在一起。
            context.AppendBlock(block, context.Root);
        }

        Content = panel;
    }

    private static MarkdownDocument MarkParsed(string normalized) =>
        Markdig.Markdown.Parse(normalized, Pipeline);

    internal static string NormalizeLineEndings(string markdown) =>
        markdown.Contains('\r')
            ? markdown.Replace("\r\n", "\n").Replace('\r', '\n')
            : markdown;

    internal static MarkdownDocument Parse(string markdown) =>
        MarkParsed(NormalizeLineEndings(markdown));

    // ---- 渲染上下文 -------------------------------------------------------------------

    private sealed class RenderContext
    {
        private readonly MarkdownView _owner;
        private int _imagesRendered;
        private int _listDepth;

        internal RenderContext(MarkdownView owner, Panel root)
        {
            _owner = owner;
            Root = root;
        }

        internal Panel Root { get; }

        /// <summary>渲染时"当前该往哪个容器里加块"（引用/列表项会换容器）。</summary>
        internal void AppendBlock(Block block, Panel target)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    target.Children.Add(BuildHeadingInline(heading));
                    break;
                case ParagraphBlock paragraph:
                    target.Children.Add(BuildParagraph(paragraph));
                    break;
                case ListBlock list:
                    target.Children.Add(BuildList(list));
                    break;
                case QuoteBlock quote:
                    target.Children.Add(BuildQuote(quote));
                    break;
                case FencedCodeBlock fenced:
                    target.Children.Add(BuildCodeBlock(fenced.Lines.ToString(), fenced.Info));
                    break;
                case CodeBlock code:
                    target.Children.Add(BuildCodeBlock(code.Lines.ToString(), null));
                    break;
                case Table table:
                    target.Children.Add(BuildTable(table));
                    break;
                case ThematicBreakBlock:
                    target.Children.Add(BuildRule());
                    break;
                case LeafBlock leaf:
                    // LinkReferenceDefinitionBlock 一类不产生可见内容的块：不渲染也不报错。
                    if (leaf.Inline is not null)
                    {
                        target.Children.Add(BuildParagraph(leaf));
                    }
                    else if (!string.IsNullOrWhiteSpace(leaf.Lines.ToString()))
                    {
                        target.Children.Add(BuildCodeBlock(leaf.Lines.ToString(), null));
                    }
                    break;
                case ContainerBlock container:
                    foreach (var child in container)
                    {
                        AppendBlock(child, target);
                    }
                    break;
            }
        }

        private SelectableTextBlock BuildHeadingInline(LeafBlock heading)
        {
            var level = (heading as HeadingBlock)?.Level ?? 1;
            var size = _owner.BaseFontSize * level switch
            {
                1 => 1.5,
                2 => 1.3,
                3 => 1.15,
                _ => 1.0,
            };
            var block = NewTextBlock(size, FontWeight.SemiBold);
            AppendInlines(block.Inlines!, heading.Inline, TextStyle.None);
            return block;
        }

        private SelectableTextBlock BuildParagraph(LeafBlock block)
        {
            var text = NewTextBlock(_owner.BaseFontSize, FontWeight.Normal);
            AppendInlines(text.Inlines!, block.Inline, TextStyle.None);
            return text;
        }

        private SelectableTextBlock NewTextBlock(double fontSize, FontWeight weight, FontFamily? family = null)
        {
            var block = new SelectableTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = fontSize,
                FontWeight = weight,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            if (family is not null)
            {
                block.FontFamily = family;
            }

            return block;
        }

        private Control BuildList(ListBlock list)
        {
            var panel = new StackPanel { Spacing = 3, Margin = new Thickness(_listDepth == 0 ? 0 : 14, 0, 0, 0) };
            _listDepth++;
            try
            {
                var index = 0;
                foreach (var item in list)
                {
                    index++;
                    if (item is not ListItemBlock listItem)
                    {
                        continue;
                    }

                    panel.Children.Add(BuildListItem(listItem, list.IsOrdered ? index : null));
                }
            }
            finally
            {
                _listDepth--;
            }

            return panel;
        }

        private Control BuildListItem(ListItemBlock item, int? number)
        {
            var marker = BuildListMarker(item, number);
            var content = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var child in item)
            {
                AppendBlock(child, content);
            }

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                ColumnSpacing = 8,
            };
            Grid.SetColumn(marker, 0);
            Grid.SetColumn(content, 1);
            marker.VerticalAlignment = VerticalAlignment.Top;
            marker.Margin = new Thickness(0, number is null ? 5 : 1, 0, 0);
            row.Children.Add(marker);
            row.Children.Add(content);
            return row;
        }

        private Control BuildListMarker(ListItemBlock item, int? number)
        {
            // 任务清单：移动端用复选框，这里同样用复选框（只读，点击不改变源文本）。
            var task = FindTaskList(item);
            if (task is not null)
            {
                return new CheckBox
                {
                    IsChecked = task.Checked,
                    IsEnabled = false,
                    Focusable = false,
                    Margin = new Thickness(0, 0, 2, 0),
                };
            }

            return new TextBlock
            {
                Text = number is null ? "•" : $"{number}.",
                FontSize = _owner.BaseFontSize,
                FontWeight = number is null ? FontWeight.Bold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Top,
            };
        }

        private static TaskList? FindTaskList(ListItemBlock item)
        {
            foreach (var child in item.Descendants())
            {
                if (child is TaskList task)
                {
                    return task;
                }
            }

            foreach (var child in item)
            {
                if (child is ParagraphBlock { Inline: { } inline } &&
                    inline.FirstChild is TaskList task)
                {
                    return task;
                }
            }

            return null;
        }

        private Control BuildQuote(QuoteBlock quote)
        {
            var content = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var child in quote)
            {
                AppendBlock(child, content);
            }

            var quoteBorder = new Border
            {
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(12, 2, 0, 2),
                Child = content,
            };
            UseThemeBrush(quoteBorder, Border.BorderBrushProperty, "HairlineStrongBrush");
            return quoteBorder;
        }

        private Control BuildCodeBlock(string content, string? language)
        {
            var mono = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace");
            var panel = new StackPanel { Spacing = 4 };
            var header = language?.Trim();
            if (!string.IsNullOrEmpty(header))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = header,
                    FontSize = _owner.BaseFontSize - 1.5,
                    FontFamily = mono,
                    Opacity = 0.7,
                });
            }

            // 代码块不折行（与移动端一致）：外层横向滚动，保留原始缩进。
            panel.Children.Add(new SelectableTextBlock
            {
                Text = content.TrimEnd('\n'),
                FontFamily = mono,
                FontSize = _owner.BaseFontSize,
                TextWrapping = TextWrapping.NoWrap,
            });

            var codeBorder = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8),
                Child = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = panel,
                },
            };
            UseThemeBrush(codeBorder, Border.BackgroundProperty, "SurfaceMutedBrush");
            return codeBorder;
        }

        private Control BuildRule()
        {
            var rule = new Border
            {
                Height = 1,
                Margin = new Thickness(0, 4),
            };
            UseThemeBrush(rule, Border.BackgroundProperty, "HairlineBrush");
            return rule;
        }

        private Control BuildTable(Table table)
        {
            var definitions = table.ColumnDefinitions.Count > 0
                ? table.ColumnDefinitions
                : null;
            var rows = table.OfType<TableRow>().ToArray();
            var columns = definitions?.Count ?? rows.Select(row => row.Count).DefaultIfEmpty(0).Max();
            if (columns == 0)
            {
                // 列数判不出来（表头损坏等）：整块按普通文本展示，不丢内容也不画空表。
                return new SelectableTextBlock
                {
                    Text = string.Join('\n', rows.Select(row => string.Join(" | ", row.OfType<TableCell>().Select(CellText)))),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = _owner.BaseFontSize,
                };
            }

            // 每一列都要显式声明，否则 Grid.SetColumn 无处落地、所有单元格会挤在第 0 列重叠。
            var grid = new Grid { RowDefinitions = new RowDefinitions() };
            for (var index = 0; index < rows.Length; index++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            for (var column = 0; column < columns; column++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            }

            for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                var row = rows[rowIndex];
                var isHeader = row.IsHeader || rowIndex == 0;
                var cells = row.OfType<TableCell>().ToArray();
                for (var column = 0; column < columns; column++)
                {
                    var cell = column < cells.Length ? cells[column] : null;
                    var border = BuildTableCell(cell, isHeader, rowIndex, column, definitions);
                    Grid.SetRow(border, rowIndex);
                    Grid.SetColumn(border, column);
                    grid.Children.Add(border);
                }
            }

            // 与移动端同一策略：给表格一个最小宽度并横向滚动，窄窗口下不把单元格压烂。
            grid.MinWidth = columns * TableMinColumnWidth;
            var tableBorder = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = grid,
                },
            };
            UseThemeBrush(tableBorder, Border.BorderBrushProperty, "HairlineBrush");
            return tableBorder;
        }

        private const double TableMinColumnWidth = 150;

        private static string CellText(TableCell cell) => string.Concat(
            cell.Descendants<LiteralInline>().Select(literal => literal.Content.ToString()));

        private Control BuildTableCell(
            TableCell? cell,
            bool isHeader,
            int rowIndex,
            int column,
            IReadOnlyList<TableColumnDefinition>? columnDefinitions)
        {
            var content = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            if (cell is not null)
            {
                foreach (var block in cell)
                {
                    AppendBlock(block, content);
                }
            }

            if (content.Children.Count == 0)
            {
                content.Children.Add(new TextBlock { Text = string.Empty, FontSize = _owner.BaseFontSize });
            }
            else if (isHeader)
            {
                // 表头加粗：Markdig 的表头单元格内容本身没有强调标记，靠这里补字重。
                foreach (var text in content.Children.OfType<SelectableTextBlock>())
                {
                    text.FontWeight = FontWeight.SemiBold;
                }
            }

            var border = new Border
            {
                Padding = new Thickness(10, 7),
                BorderThickness = new Thickness(
                    column > 0 ? 1 : 0,
                    rowIndex > 0 ? 1 : 0,
                    0,
                    0),
                Child = content,
            };
            // 表头与隔行底色走主题 token，浅色/深色主题各自解析（不能用硬编码兜底色）。
            if (isHeader || rowIndex % 2 == 0)
            {
                UseThemeBrush(border, Border.BackgroundProperty, "SurfaceMutedBrush");
            }

            UseThemeBrush(border, Border.BorderBrushProperty, "HairlineBrush");

            // GFM 对齐声明（:---: / ---:）在列宽不足时也要体现，否则数字列会看起来没对齐。
            border.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (columnDefinitions is not null && column < columnDefinitions.Count)
            {
                ((StackPanel)border.Child!).HorizontalAlignment = columnDefinitions[column].Alignment switch
                {
                    TableColumnAlign.Center => HorizontalAlignment.Center,
                    TableColumnAlign.Right => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Stretch,
                };
            }

            return border;
        }

        // ---- 行内 --------------------------------------------------------------------

        [Flags]
        private enum TextStyle
        {
            None = 0,
            Bold = 1,
            Italic = 2,
            Strikethrough = 4,
            Code = 8,
        }

        private void AppendInlines(InlineCollection target, ContainerInline? container, TextStyle style)
        {
            if (container is null)
            {
                return;
            }

            for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
            {
                AppendInline(target, inline, style);
            }
        }

        private void AppendInline(InlineCollection target, MarkdigInline inline, TextStyle style)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(StyledRun(literal.Content.ToString(), style));
                    break;
                case CodeInline code:
                    target.Add(StyledRun(code.Content, style | TextStyle.Code));
                    break;
                case EmphasisInline emphasis:
                    // DelimiterCount==1 是斜体、>=2 是粗体；'~' 的两连是两个字符的删除线。
                    var next = style;
                    if (emphasis.DelimiterChar is '~')
                    {
                        next |= TextStyle.Strikethrough;
                    }
                    else if (emphasis.DelimiterCount >= 2)
                    {
                        next |= TextStyle.Bold;
                    }
                    else
                    {
                        next |= TextStyle.Italic;
                    }

                    foreach (var child in Children(emphasis))
                    {
                        AppendInline(target, child, next);
                    }

                    break;
                case LineBreakInline lineBreak:
                    if (lineBreak.IsHard)
                    {
                        target.Add(new LineBreak());
                    }
                    else
                    {
                        target.Add(new Run(" ") { TextDecorations = null });
                    }

                    break;
                case LinkInline { IsImage: false } link:
                    AppendLink(target, link, style);
                    break;
                case LinkInline image when image.IsImage:
                    AppendImage(target, image, style);
                    break;
                case ContainerInline other:
                    foreach (var child in Children(other))
                    {
                        AppendInline(target, child, style);
                    }

                    break;
            }
        }

        private static IEnumerable<MarkdigInline> Children(ContainerInline container)
        {
            for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
            {
                yield return inline;
            }
        }

        private IEnumerable<DocumentInline> LinkLabelInlines(LinkInline link)
        {
            foreach (var child in Children(link))
            {
                switch (child)
                {
                    case LiteralInline literal:
                        yield return new Run(literal.Content.ToString());
                        break;
                    case CodeInline code:
                        yield return new Run(code.Content);
                        break;
                    case ContainerInline nested:
                        foreach (var grandChild in Children(nested))
                        {
                            if (grandChild is LiteralInline nestedLiteral)
                            {
                                yield return new Run(nestedLiteral.Content.ToString());
                            }
                        }

                        break;
                }
            }
        }

        private Run StyledRun(string text, TextStyle style)
        {
            var run = new Run(text);
            if (style.HasFlag(TextStyle.Bold))
            {
                run.FontWeight = FontWeight.Bold;
            }

            if (style.HasFlag(TextStyle.Italic))
            {
                run.FontStyle = FontStyle.Italic;
            }

            if (style.HasFlag(TextStyle.Strikethrough))
            {
                run.TextDecorations = TextDecorations.Strikethrough;
            }

            if (style.HasFlag(TextStyle.Code))
            {
                run.FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace");
                UseThemeBrush(run, TextElement.BackgroundProperty, "SurfaceMutedBrush");
            }

            return run;
        }

        private void AppendLink(InlineCollection target, LinkInline link, TextStyle style)
        {
            var label = LinkLabel(link);
            var url = link.GetDynamicUrl?.Invoke() ?? link.Url;
            if (!MarkdownUriPolicy.CanOpenLink(url))
            {
                // 不放行的协议不留成"看着能点、点了没反应"的死链接：按普通文本显示。
                target.Add(StyledRun(label, style));
                return;
            }

            var linkText = new TextBlock
            {
                Text = label,
                TextWrapping = TextWrapping.Wrap,
                FontSize = _owner.BaseFontSize,
                TextDecorations = TextDecorations.Underline,
            };
            UseThemeBrush(linkText, TextBlock.ForegroundProperty, "AccentBrush");
            var button = new Button
            {
                Content = linkText,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                VerticalAlignment = VerticalAlignment.Center,
            };
            button.Classes.Add("link-inline");
            ToolTip.SetTip(button, url);
            button.Click += (_, _) => _owner.OpenLink(url!);
            target.Add(new InlineUIContainer(button));
        }

        private void AppendImage(InlineCollection target, LinkInline image, TextStyle style)
        {
            var alt = LinkLabel(image);
            var url = image.GetDynamicUrl?.Invoke() ?? image.Url;
            if (_imagesRendered >= MarkdownImageLoader.MaxImagesPerDocument || !MarkdownUriPolicy.CanLoadImage(url))
            {
                // 超量或被策略拒绝：保留 alt 文本，不留"看着有图、其实没加载"的空位。
                target.Add(StyledRun($"[图片] {alt}", style));
                return;
            }

            _imagesRendered++;
            var placeholder = new StackPanel { Spacing = 4 };
            var caption = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(alt) ? url : alt,
                FontSize = _owner.BaseFontSize - 1,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
            };
            placeholder.Children.Add(caption);
            target.Add(new InlineUIContainer(placeholder));
            _ = LoadImageAsync(placeholder, caption, url!);
        }

        private async Task LoadImageAsync(Panel placeholder, TextBlock caption, string url)
        {
            var loader = _owner.ImageLoader ?? SharedImageLoader.Value;
            var bitmap = await loader.LoadAsync(url).ConfigureAwait(true);
            if (bitmap is null)
            {
                // 加载失败：标题保留，并给出可点的原图链接，不静默留白。
                var fallback = new Button
                {
                    Content = $"[图片加载失败] {caption.Text}",
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0),
                    FontSize = _owner.BaseFontSize - 1,
                };
                fallback.Classes.Add("link-inline");
                fallback.Click += (_, _) => _owner.OpenLink(url);
                placeholder.Children.Add(fallback);
                return;
            }

            var picture = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxHeight = 360,
            };
            placeholder.Children.Add(picture);
        }

        private static string LinkLabel(LinkInline link)
        {
            var parts = new List<string>();
            foreach (var child in Children(link))
            {
                switch (child)
                {
                    case LiteralInline literal:
                        parts.Add(literal.Content.ToString());
                        break;
                    case CodeInline code:
                        parts.Add(code.Content);
                        break;
                    case ContainerInline nested:
                        foreach (var grandChild in Children(nested))
                        {
                            if (grandChild is LiteralInline nestedLiteral)
                            {
                                parts.Add(nestedLiteral.Content.ToString());
                            }
                        }

                        break;
                }
            }

            var label = string.Concat(parts).Trim();
            return label.Length > 0 ? label : link.Url ?? string.Empty;
        }
    }

    private void OpenLink(string url)
    {
        // 二次校验：控件将来若被别处复用，也不能绕过策略。
        if (!MarkdownUriPolicy.CanOpenLink(url))
        {
            LinkOpenFailed?.Invoke(this, url);
            return;
        }

        try
        {
            // 与 IUiDialogService.OpenExternalUrlAsync 同一做法：交给 shell 打开默认浏览器。
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            };
            using var process = System.Diagnostics.Process.Start(start)
                ?? throw new IOException("无法启动系统浏览器");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
        {
            // 打不开不能让异常冒到 UI 线程外：交给宿主的诊断链记录。
            LinkOpenFailed?.Invoke(this, url);
            _ = error;
        }
    }

    /// <summary>链接打开失败时触发，供宿主记录诊断。</summary>
    public event EventHandler<string>? LinkOpenFailed;
}
