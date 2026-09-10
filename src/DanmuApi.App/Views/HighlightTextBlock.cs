using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace DanmuApi.App.Views;

/// <summary>
/// 终端风格日志行文本：可选中、可换行，并按搜索词高亮命中片段。
/// 高亮只在需要时构建 Inline，避免每行都额外分配。
/// </summary>
public sealed class HighlightTextBlock : SelectableTextBlock
{
    public static readonly StyledProperty<string?> SearchTermProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string?>(nameof(SearchTerm));

    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<HighlightTextBlock, IBrush?>(nameof(HighlightBrush));

    public static readonly StyledProperty<IBrush?> HighlightForegroundBrushProperty =
        AvaloniaProperty.Register<HighlightTextBlock, IBrush?>(nameof(HighlightForegroundBrush));

    public string? SearchTerm
    {
        get => GetValue(SearchTermProperty);
        set => SetValue(SearchTermProperty, value);
    }

    public IBrush? HighlightBrush
    {
        get => GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    public IBrush? HighlightForegroundBrush
    {
        get => GetValue(HighlightForegroundBrushProperty);
        set => SetValue(HighlightForegroundBrushProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty ||
            change.Property == SearchTermProperty ||
            change.Property == HighlightBrushProperty ||
            change.Property == HighlightForegroundBrushProperty)
        {
            RebuildInlines();
        }
    }

    private void RebuildInlines()
    {
        var text = Text;
        if (string.IsNullOrEmpty(text))
        {
            Inlines?.Clear();
            return;
        }

        var needle = SearchTerm?.Trim();
        if (string.IsNullOrEmpty(needle))
        {
            if (Inlines is not null && Inlines.Count > 0)
            {
                Inlines.Clear();
            }

            return;
        }

        Inlines ??= new InlineCollection();
        Inlines.Clear();
        var index = 0;
        while (true)
        {
            var match = text.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
            {
                Inlines.Add(new Run(text[index..]));
                return;
            }

            if (match > index)
            {
                Inlines.Add(new Run(text[index..match]));
            }

            Inlines.Add(new Run(text[match..(match + needle.Length)])
            {
                Background = HighlightBrush,
                Foreground = HighlightForegroundBrush,
                FontWeight = FontWeight.SemiBold,
            });
            index = match + needle.Length;
            if (index >= text.Length)
            {
                return;
            }
        }
    }
}
