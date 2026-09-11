using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.App.Views;
using DanmuApi.Core;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

/// <summary>
/// 配置页试点重排的目视核对用渲染用例。
/// 只在设置了 DANMU_TEST_RENDER_DIRECTORY 时输出 PNG；目录必须是绝对路径
/// （相对路径会让 Directory.CreateDirectory 与 bitmap.Save 解析到不同位置并抛
/// DirectoryNotFoundException，详见 docs/UI-REFACTOR-PLAN.md §9.1）。
/// 除渲染外还断言三档宽度下关键元素可见且不重叠，避免"图看着还行但其实没渲染"。
/// </summary>
public sealed class ConfigurationViewRenderTests
{
    [AvaloniaTheory]
    [InlineData(false, 1920, 3)]
    [InlineData(true, 1920, 3)]
    [InlineData(false, 1280, 3)]
    [InlineData(true, 1280, 3)]
    [InlineData(false, 1100, 3)]
    [InlineData(false, 1099, 2)]
    [InlineData(true, 960, 2)]
    [InlineData(false, 900, 2)]
    [InlineData(false, 899, 1)]
    [InlineData(true, 720, 1)]
    public void ConfigurationWorkspaceRendersAcrossWidths(bool dark, int width, int expectedColumns)
    {
        using var fixture = new RenderFixture();
        var model = fixture.CreateViewModel();
        var view = new ConfigurationView { DataContext = model };
        var window = CreateWindow(view, dark, width);
        window.Show();
        try
        {
            // 断点由 UserControl 的 SizeChanged 驱动。headless 下窗口尺寸变化不会自动
            // 把新的宽度派发到窗口内容（实测：尺寸改完连跑三轮布局，UserControl 的
            // Bounds 仍是旧值），所以这里显式设宽，再等 SizeChanged 收敛。
            for (var pass = 0; pass < 4; pass++)
            {
                view.Width = width;
                view.ApplyLayoutForWidth(width);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                if (view.FindControl<Grid>("LayoutRoot")!.ColumnDefinitions.Count == expectedColumns)
                {
                    break;
                }
            }

            Assert.Equal(
                expectedColumns,
                view.FindControl<Grid>("LayoutRoot")!.ColumnDefinitions.Count);

            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var root = view.FindControl<Grid>("LayoutRoot")!;
            var listColumn = view.FindControl<Grid>("ListColumn")!;
            var list = view.FindControl<ListBox>("VariableList")!;
            var rail = view.FindControl<ListBox>("CategoryList")!;
            var detail = view.FindControl<Border>("DetailPanel")!;
            Assert.True(list.IsVisible);
            Assert.True(rail.IsVisible);
            Assert.True(list.Bounds.Width > 0);
            Assert.True(rail.Bounds.Width > 0);

            // 详情面板只在选中变量时出现：未选中时整块隐藏、三栏档位的 Auto 列收成 0，
            // 列表因此独占整行宽度（旧契约是「永远显示一个空卡片 + 一行提示」，已废弃）。
            Assert.False(detail.IsVisible, "未选中变量时详情面板必须隐藏");
            Assert.False(model.HasSelectedVariable);
            if (expectedColumns == 3)
            {
                Assert.Equal(0d, detail.Bounds.Width, 0.5);
                // 详情列收成 0 后，列表宽度必须大于「减去 216 分类栏与 320 详情栏」，
                // 即那 320 确实还给了列表（列间距的确切语义由 Grid 决定，这里不做精确断言）。
                Assert.True(
                    listColumn.Bounds.Width > root.Bounds.Width - 216 - 320,
                    $"列表未收回详情栏占用的宽度：list={listColumn.Bounds.Width} root={root.Bounds.Width}");
            }

            // 断点收敛：列数、详情面板与列表所在行列。
            Assert.Equal(expectedColumns, root.ColumnDefinitions.Count);
            Assert.Equal(expectedColumns == 1 ? 2 : expectedColumns == 2 ? 1 : 0, Grid.GetRow(detail));
            Assert.Equal(expectedColumns == 1 ? 0 : expectedColumns - 1, Grid.GetColumn(detail));
            Assert.Equal(expectedColumns == 1 ? 0 : 1, Grid.GetColumn(listColumn));
            Assert.Equal(expectedColumns == 1 ? 1 : 0, Grid.GetRow(listColumn));

            // 任何一档宽度都不允许横向溢出。
            Assert.True(list.Bounds.Right <= window.Width, $"变量列表溢出：{list.Bounds}");
            Assert.True(rail.Bounds.Bottom <= window.Height, $"分类栏纵向溢出：{rail.Bounds}");

            // 选中变量后详情面板出现并拿到 320 的固定宽度，且不越界。
            model.SelectedVariable = model.FilteredVariables.First();
            window.UpdateLayout();
            Assert.True(detail.IsVisible, "选中变量后详情面板必须出现");
            Assert.Equal(320d, detail.Bounds.Width, 0.5);
            Assert.True(detail.Bounds.Right <= window.Width, $"详情面板溢出：{detail.Bounds}");
            Assert.True(detail.Bounds.Bottom <= window.Height, $"详情面板纵向溢出：{detail.Bounds}");
            model.SelectedVariable = null;
            window.UpdateLayout();

            Save(window, dark, $"config-{(dark ? "dark" : "light")}-{width}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigurationStatesRenderOnWideLayout(bool dark)
    {
        using var fixture = new RenderFixture();
        var model = fixture.CreateViewModel();
        var view = new ConfigurationView { DataContext = model };
        var window = CreateWindow(view, dark, 1280);
        window.Show();
        try
        {
            Capture("config");

            // 选中普通变量 → 详情面板出现，显示当前值与来源。
            model.SelectedVariable = FindRow(model, "TEST_COUNT");
            Capture("config-selected");
            Assert.True(view.FindControl<Border>("DetailPanel")!.IsVisible);
            Assert.Contains("TEST_COUNT", AllText(view), StringComparison.Ordinal);

            // 选中敏感变量 → 详情面板显示脱敏值，且不得出现明文。
            var secret = FindRow(model, "TEST_SECRET_VALUE");
            model.SelectedVariable = secret;
            Capture("config-sensitive");
            Assert.True(secret.IsSensitive);
            Assert.Equal("••••••••", secret.Value);
            var masked = "当前值：••••••••";
            Assert.Equal(masked, model.SelectedVariableValue);
            // 详情面板的绑定是异步刷新到可视树的，这里断言 ViewModel 与列表行的脱敏结果；
            // 明文绝不能出现在任何一条绑定出的文本里。
            var text = AllText(view);
            Assert.DoesNotContain("hidden-secret", text, StringComparison.Ordinal);
            Assert.DoesNotContain("hidden-secret", model.SelectedVariableValue, StringComparison.Ordinal);
            Assert.DoesNotContain("hidden-secret", secret.CurrentValueText, StringComparison.Ordinal);

            // 搜索态 → 页标题变「搜索结果」，仍有结果。
            model.SearchText = "TEST_MODE";
            Capture("config-search");
            Assert.Equal("搜索结果", model.PageTitle);

            // 无结果 + 诊断态。
            model.SearchText = "绝对不会匹配的关键字";
            Capture("config-empty");
            Assert.False(model.HasVariables);
            Assert.False(model.HasSelectedVariable);

            void Capture(string name)
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Save(window, dark, $"{name}-{(dark ? "dark" : "light")}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 变量行的操作按钮布局：每行必须有两个按钮（编辑 / 清除），
    /// 且编辑按钮在所有行里落在同一条竖线上、宽度一致，清除按钮同理。
    /// 这是用户明确要求「加编辑个删除两个按钮一定要对齐」的可执行判据——
    /// 不靠看图，直接比较渲染后的矩形。
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VariableRowsKeepActionButtonsAlignedAndEqualWidth(bool dark)
    {
        using var fixture = new RenderFixture();
        var model = fixture.CreateViewModel();
        var view = new ConfigurationView { DataContext = model };
        var window = CreateWindow(view, dark, 1280);
        window.Show();
        try
        {
            Settle(view, window, 1280);

            var list = view.FindControl<ListBox>("VariableList")!;
            var containers = ListContainers(list);

            var editLefts = new List<double>();
            var clearLefts = new List<double>();
            var editWidths = new List<double>();
            var clearWidths = new List<double>();
            var clearEnabled = new List<bool>();
            var containerKeys = new List<string>();

            foreach (var container in containers)
            {
                var row = Assert.IsType<ConfigurationVariableRow>(container.DataContext);
                containerKeys.Add(row.Key);
                var rowButtons = container.GetVisualDescendants().OfType<Button>()
                    .Where(button => button.Classes.Contains("row-action"))
                    .Where(button => Equals(button.Content, "编辑") || Equals(button.Content, "清除"))
                    .ToList();
                Assert.Equal(2, rowButtons.Count);

                var edit = Assert.Single(rowButtons, button => Equals(button.Content, "编辑"));
                var clear = Assert.Single(rowButtons, button => Equals(button.Content, "清除"));

                // 清除按钮是删掉 .env 里的自定义值，未配置的行必须禁用（不能发删除请求）。
                Assert.True(edit.IsEnabled, "编辑按钮不应被禁用");
                Assert.True(clear.IsEnabled == row.IsConfigured, $"{row.Key} 的清除按钮启用状态与配置状态不一致");
                clearEnabled.Add(clear.IsEnabled);

                var editRect = ButtonRect(edit, view);
                var clearRect = ButtonRect(clear, view);
                Assert.True(editRect.Width > 0 && clearRect.Width > 0, "按钮尺寸必须实际测量过");
                Assert.Equal(editRect.Width, clearRect.Width, 0.5);
                Assert.True(clearRect.Left > editRect.Right, "清除按钮必须在编辑按钮右侧");

                editLefts.Add(editRect.Left);
                clearLefts.Add(clearRect.Left);
                editWidths.Add(editRect.Width);
                clearWidths.Add(clearRect.Width);
            }

            Assert.True(containerKeys.Count >= 2, $"只实例化了 {containerKeys.Count} 行，不足以判断列对齐");

            // 同一列里所有行的按钮必须落在同一条竖线上（容差 0.5px 抗锯齿）。
            Assert.True(editLefts.Max() - editLefts.Min() <= 0.5, $"编辑按钮未对齐：{string.Join(",", editLefts)}");
            Assert.True(clearLefts.Max() - clearLefts.Min() <= 0.5, $"清除按钮未对齐：{string.Join(",", clearLefts)}");
            Assert.True(editWidths.Max() - editWidths.Min() <= 0.5, $"编辑按钮宽度不一致：{string.Join(",", editWidths)}");
            Assert.True(clearWidths.Max() - clearWidths.Min() <= 0.5, $"清除按钮宽度不一致：{string.Join(",", clearWidths)}");

            // 未配置行（VOD_SERVERS）的清除按钮必须禁用。
            // 夹具里 VOD_SERVERS 只是「没有 .env 行」，而核心装了该值时 IsConfigured 仍为 true，
            // 所以先走真实的删除路径（确认对话框 → 核心删除接口 → .env 落地）把它清掉，
            // 得到真正的未配置行，否则这条断言测的是假前提。
            // 不允许手工改 .env：渲染夹具必须验证的是真链路。
            model.SelectedVariable = FindRow(model, "VOD_SERVERS");
            fixture.Dialogs.Confirmation = true;
            fixture.EnvClient.OnDelete = key =>
            {
                var path = Path.Combine(fixture.Paths.NodeProjectDirectory, "config", ".env");
                DotEnvFile.ApplyMutations(path, DotEnvFile.GetFingerprint(path), [DotEnvMutation.Delete(key)]);
            };
            await model.DeleteVariableCommand.ExecuteAsync(model.SelectedVariable);
            Dispatcher.UIThread.RunJobs();

            var unconfiguredCategory = FindCategoryContaining(model, "VOD_SERVERS");
            model.SelectedVariable = FindRow(model, "VOD_SERVERS");
            model.SelectedCategory = unconfiguredCategory;
            Settle(view, window, 1280);

            Assert.False(
                model.SelectedVariable!.IsConfigured,
                $"删除未生效：keys=[{fixture.EnvClient.DeletedKeys.Count}] msgs=[{string.Join(";", fixture.Dialogs.Messages.Select(m => m.Title + "/" + m.IsError + "/" + m.Message))}] env=[{File.ReadAllText(Path.Combine(fixture.Paths.NodeProjectDirectory, "config", ".env"))}]");
            var unconfigured = ListContainers(list).FirstOrDefault(item =>
                Assert.IsType<ConfigurationVariableRow>(item.DataContext).Key == "VOD_SERVERS");
            Assert.NotNull(unconfigured);
            var unconfiguredButtons = unconfigured.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("row-action") && button.Width == 52)
                .OrderBy(button => ButtonRect(button, view).Left)
                .ToList();
            Assert.Equal(2, unconfiguredButtons.Count);
            Assert.Equal("编辑", unconfiguredButtons[0].Content);
            Assert.True(unconfiguredButtons[0].IsEnabled);
            Assert.Equal("清除", unconfiguredButtons[1].Content);
            Assert.False(unconfiguredButtons[1].IsEnabled);
            // 禁用语义同时来自 ViewModel，而不是样式伪装。
            Assert.False(model.CanResetSelectedVariable);
            Assert.True(model.SelectedVariable!.ShowSourceChip);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 行的三个视觉契约：
    /// 1) 每行只有一个状态胶囊（已配置 与 来源 互斥，不同时出现）；
    /// 2) 行内有当前值框，长值默认折叠、点「展开」后显示完整值；
    /// 3) 搜索框左对齐（起始 x 明显小于它所在列的右边界）。
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void VariableRowsExposeSingleChipValueBoxAndLeftAlignedSearch(bool dark)
    {
        using var fixture = new RenderFixture();
        var model = fixture.CreateViewModel();
        var view = new ConfigurationView { DataContext = model };
        var window = CreateWindow(view, dark, 1280);
        window.Show();
        try
        {
            Settle(view, window, 1280);

            var list = view.FindControl<ListBox>("VariableList")!;
            var containers = ListContainers(list);
            Assert.True(containers.Count >= 2, $"只实例化了 {containers.Count} 行");

            foreach (var container in containers)
            {
                var row = Assert.IsType<ConfigurationVariableRow>(container.DataContext);
                // 已配置的行不再挂 .env 来源胶囊（用户：标签太多也容易乱）。
                // Assert.Equal 直接比对枚举会走「引用相等」，这里先物化成字符串。
                var chips = container.GetVisualDescendants().OfType<Border>()
                    .Where(border => border.Classes.Contains("chip") && border.IsVisible)
                    .SelectMany(border => border.GetVisualDescendants().OfType<TextBlock>())
                    .Where(text => text.IsVisible)
                    .Select(text => text.Text)
                    .Where(text => !string.IsNullOrEmpty(text))
                    .ToList();
                if (row.IsConfigured)
                {
                    var only = Assert.Single(chips);
                    Assert.Equal("已配置", only);
                    Assert.DoesNotContain(".env", chips);
                }
                else
                {
                    // 未配置的行显示来源胶囊。这里比较元素而不是序列：
                    // 断言库对 IEnumerable<string> 与 string[] 的比较不总是逐元素（实测会走引用比较）。
                    var chip = Assert.Single(chips);
                    Assert.Equal(row.Source, chip);
                }
            }

            // 值框：每行两份（折叠态 MaxLines=2 / 展开态不限行），同一时刻只有一份可见。
            var boxes = list.GetVisualDescendants().OfType<SelectableTextBlock>()
                .Where(block => block.Classes.Contains("mono"))
                .ToList();
            Assert.Equal(containers.Count * 2, boxes.Count);
            Assert.All(boxes.Where(block => block.IsVisible), block => Assert.Equal(2, block.MaxLines));
            Assert.All(containers, container =>
                Assert.Single(
                    container.GetVisualDescendants().OfType<SelectableTextBlock>(),
                    block => block.Classes.Contains("mono") && block.IsVisible));

            // 长值默认折叠 + 展开命令把同一行另一份值框打开。
            var longRow = FindRow(model, "VOD_SERVERS");
            Assert.True(
                longRow.IsValueExpandable,
                $"值长度={longRow.CurrentValueText.Length}，文本=[{longRow.CurrentValueText}]");
            Assert.False(longRow.IsValueExpanded);
            Assert.Equal("展开", longRow.ValueToggleText);
            model.SelectedVariable = longRow;
            Settle(view, window, 1280);
            var longContainer = ListContainers(list).FirstOrDefault(item =>
                Assert.IsType<ConfigurationVariableRow>(item.DataContext).Key == "VOD_SERVERS");
            Assert.NotNull(longContainer);
            var toggle = longContainer.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Classes.Contains("row-action") && Equals(button.Content, "展开"));
            Assert.True(toggle.IsEnabled);
            toggle.Command!.Execute(toggle.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var expandedRow = model.FilteredVariables.Single(row => row.Key == "VOD_SERVERS");
            Assert.True(expandedRow.IsValueExpanded);
            Assert.Equal("收起", expandedRow.ValueToggleText);

            // 搜索框靠左：控件左边缘必须贴着列表列的左边，而不是居中。
            var search = view.GetVisualDescendants().OfType<TextBox>()
                .Single(box => box.Watermark is not null);
            var searchRect = new Rect(search.TranslatePoint(default, view)!.Value, search.Bounds.Size);
            var column = view.FindControl<Grid>("ListColumn")!;
            var columnRect = new Rect(column.TranslatePoint(default, view)!.Value, column.Bounds.Size);
            Assert.True(searchRect.Left - columnRect.Left <= 2, $"搜索框未靠左：{searchRect} vs {columnRect}");
            Assert.True(columnRect.Right - searchRect.Right > 200, "搜索框右侧应留白而不是被拉伸居中");
        }
        finally
        {
            window.Close();
        }
    }

    private static Rect ButtonRect(Button button, Visual root)
    {
        var origin = button.TranslatePoint(default, root)!.Value;
        return new Rect(origin, button.Bounds.Size);
    }

    /// <summary>
    /// 按 Key 找行：FilteredVariables 只反映当前分类/搜索条件，所以先逐个分类找，
    /// 找到后把该分类留下（后续断言依赖列表内容与选中项一致）。
    /// </summary>
    private static ConfigurationVariableRow FindRow(ConfigurationPageViewModel model, string key)
    {
        var category = FindCategoryContaining(model, key);
        model.SelectedCategory = category;
        return model.FilteredVariables.Single(row => row.Key == key);
    }

    /// <summary>
    /// 逐个切换分类、每次重新取 FilteredVariables，返回第一个包含目标变量的分类。
    /// 不能写成 <c>Categories.First(c =&gt; FilteredVariables.Any(...))</c>：
    /// 谓词里没有赋值，SelectedCategory 从未切换，过滤结果始终是初始分类的。
    /// </summary>
    private static ConfigurationCategory FindCategoryContaining(ConfigurationPageViewModel model, string key)
    {
        foreach (var category in model.Categories)
        {
            model.SelectedCategory = category;
            if (model.FilteredVariables.Any(row => row.Key == key))
            {
                return category;
            }
        }

        throw new InvalidOperationException($"渲染夹具中找不到变量：{key}");
    }

    /// <summary>
    /// 取列表当前已实体化（已进入可视树、Bounds 已测量）的行容器。
    /// headless 下 ItemsPresenter 的祖先链没有 ScrollViewer，虚拟化面板会在一次布局里
    /// 只实例化少量容器，因此这里不做「必须有多少行」的硬断言，也别指望能拿到全部行。
    /// </summary>
    private static List<ListBoxItem> ListContainers(ListBox list)
    {
        var containers = list.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(item => item.Bounds.Width > 0 && item.Bounds.Height > 0)
            .ToList();
        Assert.NotEmpty(containers);
        return containers;
    }

    /// <summary>
    /// 断点由 UserControl 的 SizeChanged 驱动，headless 下不会自动派发；
    /// 显式设宽 + 主动收敛，直到 LayoutRoot 的列数稳定。
    /// </summary>
    private static void Settle(ConfigurationView view, Window window, int width)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            view.Width = width;
            view.ApplyLayoutForWidth(width);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static Window CreateWindow(ConfigurationView view, bool dark, int width)
    {
        var window = new Window
        {
            Width = width,
            Height = 800,
            Padding = new Thickness(24),
            Content = view,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
        };

        // headless 下 Window.Show() 的首次布局不会给 content 一个带最终宽度的
        // measure pass，UserControl 的 SizeChanged 不触发（连跑三轮布局也没用，实测）。
        // 这里在 Show() 之后、布局之前按窗口内容宽度主动收敛一次。
        window.Opened += (_, _) => view.ApplyLayoutForWidth(Math.Max(0, width - window.Padding.Left - window.Padding.Right));
        return window;
    }

    private static string AllText(Visual root) =>
        string.Join(
            "\n",
            root.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(block => block.Text ?? string.Empty));

    private static void Save(Window window, bool dark, string name)
    {
        _ = dark;
        var directory = Environment.GetEnvironmentVariable("DANMU_TEST_RENDER_DIRECTORY");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(directory, name + ".png"));
    }

    private sealed class RenderFixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();

        public RenderFixture()
        {
            Root = Path.Combine(_directory.Path, "runtime-root");
            Paths = new AppPaths(Root, Path.Combine(_directory.Path, "appdata"));
            var catalogDirectory = Path.Combine(Paths.NodeProjectDirectory, "danmu_api_stable", "configs");
            Directory.CreateDirectory(catalogDirectory);
            File.WriteAllText(Path.Combine(catalogDirectory, "envs.js"), """
                const envVarConfig = {
                  'TEST_SECRET_VALUE': { category: 'api', type: 'text', description: '敏感测试值' },
                  'TEST_COUNT': { category: 'cache', type: 'number', description: '数量', min: 0, max: 10 },
                  'TEST_MODE': { category: 'system', type: 'select', options: ['fast', 'safe'], description: '模式' },
                  'VOD_SERVERS': { category: 'source', type: 'text', description: 'VOD 站点配置，支持主站@地址 与 备用@地址 的写法' },
                  'ADMIN_TOKEN': { category: 'api', type: 'text', description: '系统管理访问令牌' },
                };
                this.get('TEST_SECRET_VALUE', '', 'string', true);
                this.get('TEST_COUNT', 3, 'number');
                this.get('TEST_MODE', 'fast', 'string');
                this.get('VOD_SERVERS', '默认@https://example.com', 'string');
                this.get('ADMIN_TOKEN', '', 'string', true);
                """, new System.Text.UTF8Encoding(false));
            var configDirectory = Path.Combine(Paths.NodeProjectDirectory, "config");
            Directory.CreateDirectory(configDirectory);
            File.WriteAllText(
                Path.Combine(configDirectory, ".env"),
                "TEST_SECRET_VALUE=hidden-secret\nTEST_COUNT=5\nVOD_SERVERS=主站@https://example.com\n",
                new System.Text.UTF8Encoding(false));
        }

        public string Root { get; }

        public AppPaths Paths { get; }

        public RenderDialogService Dialogs { get; } = new();

        public RenderEnvClient EnvClient { get; } = new();

        public ConfigurationPageViewModel CreateViewModel() =>
            new(
                Paths,
                new RenderSettingsStore(),
                Dialogs,
                new RenderWriteGate(),
                EnvClient,
                new StubAdminSessionService(adminMode: true, configured: true, sessionToken: "render-session"),
                new RenderDiagnostics());

        public void Dispose() => _directory.Dispose();
    }

    private sealed class RenderSettingsStore : ISettingsStore
    {
        public IReadOnlyDictionary<string, string> Read() =>
            new Dictionary<string, string>(StringComparer.Ordinal);

        public void Write(IReadOnlyDictionary<string, string?> changes) =>
            throw new NotSupportedException("渲染用例不应写入桌面设置");
    }

    /// <summary>
    /// 渲染用例默认不碰写路径，但这一个用例必须走真实的删除链路
    /// （确认对话框 → 核心删除接口 → .env 落地 → 回读校验），
    /// 所以确认框可编程回答，其余对话框方法一律抛异常。
    /// </summary>
    private sealed class RenderDialogService : IUiDialogService
    {
        public bool Confirmation { get; set; }

        public List<(string Title, string Message, bool IsError)> Messages { get; } = [];

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel) =>
            Task.FromResult(Confirmation);

        public Task EditPortAsync(MainWindowViewModel viewModel) => NotUsed();

        public Task EditTokenAsync(MainWindowViewModel viewModel) => NotUsed();

        public Task ShowCacheAsync(MainWindowViewModel viewModel) => NotUsed();

        public Task<CloseActionDecision?> AskCloseActionAsync() =>
            Task.FromException<CloseActionDecision?>(new InvalidOperationException("渲染用例不得触发对话框写路径"));

        public Task<string?> ChooseGithubRouteAsync(
            string reason,
            string selectedProxyId,
            IGithubProxySpeedTester speedTester,
            CancellationToken cancellationToken = default) =>
            Task.FromException<string?>(new InvalidOperationException("渲染用例不得触发对话框写路径"));

        public Task<string?> ChooseRuntimeRootAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<string?>(new InvalidOperationException("渲染用例不得触发对话框写路径"));

        public Task OpenDirectoryAsync(string path, CancellationToken cancellationToken = default) => NotUsed();

        public Task CopyTextAsync(string text) => NotUsed();

        public Task ShowMessageAsync(string title, string message, bool isError = false)
        {
            Messages.Add((title, message, isError));
            return Task.CompletedTask;
        }

        private static Task<T> NotUsed<T>() =>
            Task.FromException<T>(new InvalidOperationException("渲染用例不得触发对话框写路径"));

        private static Task NotUsed() =>
            Task.FromException(new InvalidOperationException("渲染用例不得触发对话框写路径"));
    }

    private sealed class RenderWriteGate : IAdminWriteGate
    {
        public Action? NavigateToSecurity { get; set; }

        public Task<bool> EnsureAsync(string action, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    /// <summary>
    /// 渲染用例里的核心环境变量接口：默认拒绝一切写请求；
    /// 只有显式设置了 <see cref="OnDelete"/> 才按回调落地删除，避免用例静默"通过"。
    /// </summary>
    private sealed class RenderEnvClient : ICoreEnvClient
    {
        public Action<string>? OnDelete { get; set; }

        public List<string> DeletedKeys { get; } = [];

        public Task<CoreEnvDeleteResult> DeleteAsync(
            string host,
            int port,
            string? token,
            string? adminToken,
            string key,
            CancellationToken cancellationToken = default)
        {
            if (OnDelete is null)
            {
                return Task.FromResult(CoreEnvDeleteResult.Failure("渲染用例不调用核心"));
            }

            DeletedKeys.Add(key);
            OnDelete(key);
            return Task.FromResult(CoreEnvDeleteResult.Success());
        }

        public Task<CoreEnvSetResult> SetAsync(
            string host,
            int port,
            string? token,
            string? adminToken,
            string key,
            string value,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CoreEnvSetResult.Failure("渲染用例不调用核心"));

        public Task<CoreEnvValueResult> ReadConfigValueAsync(
            string host,
            int port,
            string? token,
            string? adminToken,
            string key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CoreEnvValueResult.Failure("渲染用例不调用核心"));
    }

    private sealed class RenderDiagnostics : IAppDiagnostics
    {
        public string? LastDiagnostic { get; private set; }

        public void Record(string message, Exception? error = null) => LastDiagnostic = message;
    }
}
