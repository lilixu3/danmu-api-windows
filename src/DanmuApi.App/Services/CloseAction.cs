namespace DanmuApi.App.Services;

public enum CloseAction
{
    Ask,
    Exit,
    Tray,
}

public static class CloseActionParser
{
    public static CloseAction Parse(string? value)
    {
        if (value is null)
        {
            return CloseAction.Ask;
        }

        return value switch
        {
            "ask" => CloseAction.Ask,
            "exit" => CloseAction.Exit,
            "tray" => CloseAction.Tray,
            _ => throw new FormatException($"close_action 设置值无效: {value}")
        };
    }

    public static string ToStorageValue(CloseAction action) => action switch
    {
        CloseAction.Ask => "ask",
        CloseAction.Exit => "exit",
        CloseAction.Tray => "tray",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "未知的关闭行为")
    };
}

public sealed record CloseActionDecision(CloseAction Action, bool RememberChoice);
