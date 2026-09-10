using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DanmuApi.Core;

namespace DanmuApi.App.Views;

/// <summary>
/// 长操作进度弹窗：阶段文案 + 确定/不定进度条 + 已下载字节数 + 取消按钮。
/// 操作完成前不允许通过关闭窗口逃逸（关闭按钮被拦截），只能等完成或取消。
/// </summary>
public sealed class ProgressDialogWindow : Window
{
    private readonly TextBlock _stageText;
    private readonly TextBlock _percentText;
    private readonly ProgressBar _bar;
    private readonly TextBlock _bytesText;
    private readonly Button _cancelButton;
    private readonly Action _onCancel;
    private bool _allowClose;
    private long _lastTotalBytes;

    public ProgressDialogWindow(string title, Action onCancel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        _onCancel = onCancel ?? throw new ArgumentNullException(nameof(onCancel));
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _stageText = new TextBlock
        {
            Text = "准备中…",
            TextWrapping = TextWrapping.Wrap,
        };
        _percentText = new TextBlock
        {
            Text = string.Empty,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _bar = new ProgressBar
        {
            Height = 6,
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            IsIndeterminate = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _bytesText = new TextBlock { Text = string.Empty, FontSize = 12 };
        _bytesText.Classes.Add("body-muted");
        _cancelButton = new Button
        {
            Content = "取消",
            MinWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _cancelButton.Classes.Add("secondary-action");
        _cancelButton.Click += (_, _) =>
        {
            _cancelButton.IsEnabled = false;
            _cancelButton.Content = "正在取消…";
            _onCancel();
        };

        Content = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(24),
            Children =
            {
                new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold },
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children =
                    {
                        _stageText,
                        CreateAtColumn(_percentText, 1),
                    },
                },
                _bar,
                _bytesText,
                _cancelButton,
            },
        };

        Closing += (_, e) =>
        {
            if (!_allowClose)
            {
                e.Cancel = true;
            }
        };
    }

    public void Apply(CoreInstallProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        _stageText.Text = progress.RouteLabel is null
            ? progress.Detail
            : $"{progress.Detail} · {progress.RouteLabel}";
        if (progress.TotalBytes is > 0 and var total)
        {
            _lastTotalBytes = total;
        }

        var downloaded = progress.DownloadedBytes ?? 0;
        if (_lastTotalBytes > 0 && downloaded > 0)
        {
            var ratio = Math.Clamp((double)downloaded / _lastTotalBytes, 0d, 1d);
            _bar.IsIndeterminate = false;
            _bar.Value = ratio;
            _percentText.Text = $"{Math.Floor(ratio * 100).ToString(CultureInfo.InvariantCulture)}%";
            _bytesText.Text = $"{FormatBytes(downloaded)} / {FormatBytes(_lastTotalBytes)}";
            _bytesText.IsVisible = true;
        }
        else if (downloaded > 0)
        {
            _bar.IsIndeterminate = true;
            _percentText.Text = string.Empty;
            _bytesText.Text = FormatBytes(downloaded);
            _bytesText.IsVisible = true;
        }
        else
        {
            _bar.IsIndeterminate = true;
            _percentText.Text = string.Empty;
            _bytesText.IsVisible = false;
        }
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L)
        {
            return $"{(bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture)} MB";
        }

        if (bytes >= 1024)
        {
            return $"{(bytes / 1024d).ToString("0.0", CultureInfo.InvariantCulture)} KB";
        }

        return $"{bytes.ToString(CultureInfo.InvariantCulture)} B";
    }

    private static Control CreateAtColumn(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }
}
