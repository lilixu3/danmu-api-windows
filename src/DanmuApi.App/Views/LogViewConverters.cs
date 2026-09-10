using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DanmuApi.Core;

namespace DanmuApi.App.Views;

/// <summary>
/// 按日志级别返回画笔。日志区在浅色/深色主题下都使用深色终端底，
/// 因此这里使用固定终端调色板而非跟随主题资源：错误红、警告黄、信息绿、成功青、调试灰。
/// </summary>
public sealed class LogLevelBrushConverter : IValueConverter
{
    private static readonly Dictionary<LogLevel, IBrush> Palette = new()
    {
        [LogLevel.Error] = new SolidColorBrush(Color.Parse("#F06B78")),
        [LogLevel.Warn] = new SolidColorBrush(Color.Parse("#F0AD4E")),
        [LogLevel.Info] = new SolidColorBrush(Color.Parse("#4BC887")),
        [LogLevel.Success] = new SolidColorBrush(Color.Parse("#5EC4DB")),
        [LogLevel.Debug] = new SolidColorBrush(Color.Parse("#7D8899")),
        [LogLevel.Other] = new SolidColorBrush(Color.Parse("#A0AAB9")),
    };

    public static readonly LogLevelBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is LogLevel level && Palette.TryGetValue(level, out var brush) ? brush : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToTextWrappingConverter : IValueConverter
{
    public static readonly BoolToTextWrappingConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? TextWrapping.Wrap : TextWrapping.NoWrap;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TextWrapping.Wrap;
}
