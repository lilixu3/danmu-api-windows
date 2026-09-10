using System.Globalization;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using DanmuApi.App.ViewModels;
using DanmuApi.Runtime;

namespace DanmuApi.App.Views;

public sealed class CacheCleanupWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly StackPanel _itemsPanel;
    private readonly TextBlock _selectionText;
    private readonly TextBlock _diagnosticText;
    private readonly Button _clearButton;
    private readonly Dictionary<string, CheckBox> _checks = new(StringComparer.Ordinal);

    public CacheCleanupWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Title = "核心缓存";
        Width = 660;
        MinWidth = 520;
        Height = 600;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _itemsPanel = new StackPanel { Spacing = 6 };
        _selectionText = new TextBlock
        {
            Text = "未选择",
            VerticalAlignment = VerticalAlignment.Center,
        };
        _selectionText.Classes.Add("body-muted");
        _diagnosticText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        _clearButton = new Button
        {
            Content = "清理已选",
            MinWidth = 100,
            IsDefault = true,
            IsEnabled = false,
        };
        _clearButton.Classes.Add("primary-action");
        _clearButton.Click += async (_, _) => await ClearSelectedAsync();

        var selectAll = new Button { Content = "全选", MinWidth = 72 };
        selectAll.Classes.Add("ghost-action");
        selectAll.Click += (_, _) => SetAll(true);
        var clearSelection = new Button { Content = "取消全选", MinWidth = 86 };
        clearSelection.Classes.Add("ghost-action");
        clearSelection.Click += (_, _) => SetAll(false);
        var close = new Button { Content = "关闭", MinWidth = 72, IsCancel = true };
        close.Classes.Add("secondary-action");
        close.Click += (_, _) => Close();

        var heading = new StackPanel
        {
            Spacing = 5,
            Children =
            {
                new TextBlock { Text = "核心缓存", FontSize = 21, FontWeight = FontWeight.SemiBold },
                CreateMutedText("通过 danmu_api 核心接口清理内存和业务缓存，不会删除本地运行目录。"),
            },
        };
        var selectionBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { selectAll, clearSelection, _selectionText },
        };
        var itemsScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _itemsPanel,
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { close, _clearButton },
        };
        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"),
            Margin = new Thickness(24),
            RowSpacing = 14,
        };
        Grid.SetRow(selectionBar, 1);
        Grid.SetRow(itemsScroll, 2);
        Grid.SetRow(_diagnosticText, 3);
        Grid.SetRow(actions, 4);
        content.Children.Add(heading);
        content.Children.Add(selectionBar);
        content.Children.Add(itemsScroll);
        content.Children.Add(_diagnosticText);
        content.Children.Add(actions);
        Content = content;

        RefreshItems();
    }

    private void RefreshItems()
    {
        _checks.Clear();
        _itemsPanel.Children.Clear();
        foreach (var item in _viewModel.CoreCacheItems)
        {
            var check = new CheckBox
            {
                IsChecked = false,
                Tag = item.Key,
                VerticalAlignment = VerticalAlignment.Top,
                Content = new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock { Text = item.Title, FontWeight = FontWeight.SemiBold },
                        CreateMutedText(item.Description),
                    },
                },
            };
            check.IsCheckedChanged += (_, _) => UpdateSelectionText();
            _checks.Add(item.Key, check);
            var entryBorder = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10),
                Child = check,
            };
            entryBorder.Classes.Add("dashboard-panel");
            _itemsPanel.Children.Add(entryBorder);
        }

        UpdateSelectionText();
    }

    private void SetAll(bool selected)
    {
        foreach (var check in _checks.Values)
        {
            check.IsChecked = selected;
        }

        UpdateSelectionText();
    }

    private void UpdateSelectionText()
    {
        var selected = _checks.Values.Count(check => check.IsChecked == true);
        _selectionText.Text = selected == 0
            ? "未选择"
            : $"已选 {selected.ToString(CultureInfo.InvariantCulture)} 项";
        _clearButton.IsEnabled = selected > 0;
    }

    private async Task ClearSelectedAsync()
    {
        var keys = _checks.Values
            .Where(check => check.IsChecked == true && check.Tag is string)
            .Select(check => (string)check.Tag!)
            .ToArray();
        if (keys.Length == 0)
        {
            ShowDiagnostic("请至少选择一个核心缓存项。", succeeded: false);
            return;
        }

        var confirmation = CreateConfirmation(keys.Length);
        var confirmed = await confirmation.ShowDialog<bool?>(this);
        if (confirmed != true)
        {
            return;
        }

        _clearButton.IsEnabled = false;
        try
        {
            var result = await _viewModel.ClearCoreCachesAsync(keys);
            if (result.Succeeded)
            {
                var details = _viewModel.CoreCacheItems
                    .Where(item => keys.Contains(item.Key, StringComparer.Ordinal))
                    .Select(item => result.ClearedItems.TryGetValue(item.Key, out var count)
                        ? $"{item.Title}：核心返回 {count.ToString(CultureInfo.InvariantCulture)}"
                        : $"{item.Title}：核心已处理");
                ShowDiagnostic(string.Join(Environment.NewLine, details), succeeded: true);
                SetAll(false);
            }
            else
            {
                ShowDiagnostic(result.Diagnostic, succeeded: false);
            }
        }
        catch (OperationCanceledException)
        {
            ShowDiagnostic("核心缓存请求已取消。", succeeded: false);
        }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidOperationException)
        {
            ShowDiagnostic($"核心缓存清理失败：{error.Message}", succeeded: false);
        }
        finally
        {
            UpdateSelectionText();
        }
    }

    private static TextBlock CreateMutedText(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };
        block.Classes.Add("body-muted");
        return block;
    }

    private void ShowDiagnostic(string text, bool succeeded)
    {
        _diagnosticText.Text = text;
        _diagnosticText.Classes.Remove("success-text");
        _diagnosticText.Classes.Remove("danger-text");
        _diagnosticText.Classes.Add(succeeded ? "success-text" : "danger-text");
        _diagnosticText.IsVisible = true;
    }

    private static Window CreateConfirmation(int count)
    {
        var confirm = new Window
        {
            Title = "确认清理核心缓存",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var cancel = new Button { Content = "取消", MinWidth = 76, IsCancel = true };
        cancel.Classes.Add("secondary-action");
        var accept = new Button { Content = "确认清理", MinWidth = 90, IsDefault = true };
        accept.Classes.Add("primary-action");
        accept.Click += (_, _) => confirm.Close(true);
        cancel.Click += (_, _) => confirm.Close(false);
        confirm.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(24),
            Children =
            {
                new TextBlock { Text = "确认清理核心缓存？", FontSize = 20, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = $"即将请求核心清理已选择的 {count.ToString(CultureInfo.InvariantCulture)} 项缓存。此操作不会删除本地文件。",
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, accept },
                },
            },
        };
        return confirm;
    }
}
