using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DanmuApi.Core;

namespace DanmuApi.App.Views;

public sealed class GithubRoutePickerWindow : Window
{
    private readonly CancellationTokenSource _closeCts = new();
    private readonly Dictionary<string, RouteRow> _rows = new(StringComparer.Ordinal);
    private readonly IGithubProxySpeedTester _speedTester;
    private string _selectedId;
    private bool _closingWithResult;

    public GithubRoutePickerWindow(
        string reason,
        string selectedProxyId,
        IGithubProxySpeedTester speedTester)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _speedTester = speedTester ?? throw new ArgumentNullException(nameof(speedTester));
        _selectedId = GithubProxyCatalog.GetById(selectedProxyId).Id;
        Title = "选择 GitHub 线路";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 720;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var routes = new StackPanel { Spacing = 8 };
        foreach (var option in GithubProxyCatalog.Options)
        {
            var row = CreateRouteRow(option);
            _rows.Add(option.Id, row);
            routes.Children.Add(row.Container);
        }

        var cancel = new Button
        {
            Content = "取消",
            IsCancel = true,
            MinWidth = 88,
        };
        cancel.Classes.Add("secondary-action");
        cancel.Click += (_, _) => Close();
        var confirm = new Button
        {
            Content = "保存线路",
            IsDefault = true,
            MinWidth = 100,
        };
        confirm.Classes.Add("primary-action");
        confirm.Click += (_, _) =>
        {
            _closingWithResult = true;
            Close(_selectedId);
        };
        var retest = new Button
        {
            Content = "重新测速",
            MinWidth = 96,
        };
        retest.Classes.Add("ghost-action");
        retest.Click += (_, _) => _ = RunSpeedTestAsync();

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Spacing = 14,
                Margin = new Thickness(24),
                Children =
                {
                    new TextBlock
                    {
                        Text = "选择 GitHub 线路",
                        FontSize = 20,
                        FontWeight = FontWeight.SemiBold,
                    },
                    Muted(reason),
                    Muted("5 条线路会同时测速。Token 只发送给 GitHub 官方 API。"),
                    routes,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { retest, cancel, confirm },
                    },
                },
            },
        };
        Closed += (_, _) =>
        {
            if (!_closingWithResult)
            {
                _closeCts.Cancel();
            }
            _closeCts.Dispose();
        };
        Opened += (_, _) => _ = RunSpeedTestAsync();
        UpdateSelection();
    }

    private RouteRow CreateRouteRow(GithubProxyOption option)
    {
        var radio = new RadioButton
        {
            GroupName = "github-route",
            IsChecked = string.Equals(option.Id, _selectedId, StringComparison.Ordinal),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var status = Muted("等待测速");
        status.HorizontalAlignment = HorizontalAlignment.Right;
        var details = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = option.Label, FontWeight = FontWeight.SemiBold },
                Muted(option.IsOriginal ? "GitHub 官方" : "公开代理候选"),
            },
        };
        Grid.SetColumn(details, 1);
        Grid.SetColumn(status, 2);
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 10,
            Children = { radio, details, status },
        };
        var container = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10),
            Child = grid,
        };
        container.Classes.Add("route-row");
        void Select()
        {
            _selectedId = option.Id;
            UpdateSelection();
        }
        radio.Click += (_, _) => Select();
        container.PointerPressed += (_, _) => Select();
        return new RouteRow(option, container, radio, status);
    }

    private async Task RunSpeedTestAsync()
    {
        foreach (var row in _rows.Values)
        {
            row.Status.Text = "测速中";
        }

        var progress = new Progress<GithubProxyLatencyResult>(UpdateLatency);
        try
        {
            await _speedTester.TestAllAsync(progress, _closeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_closeCts.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var row in _rows.Values.Where(row => row.Status.Text == "测速中"))
                {
                    row.Status.Text = "测速失败";
                    ToolTip.SetTip(row.Status, error.Message);
                }
            });
        }
    }

    private void UpdateLatency(GithubProxyLatencyResult result)
    {
        if (!_rows.TryGetValue(result.Option.Id, out var row))
        {
            return;
        }

        row.Latency = result.Latency;
        row.Status.Text = result.Latency is null
            ? "不可用"
            : $"{Math.Max(1, (int)Math.Round(result.Latency.Value.TotalMilliseconds))} ms";
        ToolTip.SetTip(row.Status, result.Diagnostic);
        var fastest = _rows.Values
            .Where(item => item.Latency is not null)
            .OrderBy(item => item.Latency)
            .FirstOrDefault();
        foreach (var item in _rows.Values)
        {
            item.Status.FontWeight = ReferenceEquals(item, fastest)
                ? FontWeight.SemiBold
                : FontWeight.Normal;
            if (ReferenceEquals(item, fastest) && item.Latency is not null)
            {
                item.Status.Text = $"最快 · {Math.Max(1, (int)Math.Round(item.Latency.Value.TotalMilliseconds))} ms";
            }
            else if (item.Latency is not null)
            {
                item.Status.Text = $"{Math.Max(1, (int)Math.Round(item.Latency.Value.TotalMilliseconds))} ms";
            }
        }
    }

    private void UpdateSelection()
    {
        foreach (var row in _rows.Values)
        {
            row.Radio.IsChecked = string.Equals(row.Option.Id, _selectedId, StringComparison.Ordinal);
        }
    }

    private static TextBlock Muted(string text)
    {
        var value = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        value.Classes.Add("body-muted");
        return value;
    }

    private sealed class RouteRow(
        GithubProxyOption option,
        Border container,
        RadioButton radio,
        TextBlock status)
    {
        public GithubProxyOption Option { get; } = option;
        public Border Container { get; } = container;
        public RadioButton Radio { get; } = radio;
        public TextBlock Status { get; } = status;
        public TimeSpan? Latency { get; set; }
    }
}
